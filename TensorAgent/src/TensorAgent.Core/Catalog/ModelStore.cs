// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorAgent.Core.Downloads;

namespace TensorAgent.Core.Catalog;

/// <summary>Where a catalog entry stands on this device.</summary>
public enum InstallState
{
    /// <summary>Nothing of it is on disk.</summary>
    NotInstalled,
    /// <summary>Some required files are missing or partial.</summary>
    Partial,
    /// <summary>Every required file is present at its expected size.</summary>
    Installed,
}

/// <summary>Progress of a whole entry's download (several files).</summary>
/// <param name="FileName">The file being transferred now.</param>
/// <param name="FileIndex">1-based index of that file among the files to fetch.</param>
/// <param name="FileCount">How many files this download fetches.</param>
/// <param name="BytesReceived">Bytes on disk across all files of the entry.</param>
/// <param name="TotalBytes">Bytes the entry needs in total.</param>
/// <param name="BytesPerSecond">Current rate.</param>
/// <param name="Phase">"downloading" or "verifying".</param>
public readonly record struct ModelDownloadProgress(
    string FileName, int FileIndex, int FileCount, long BytesReceived, long TotalBytes, double BytesPerSecond, string Phase)
{
    public double Fraction => TotalBytes > 0 ? Math.Min(1.0, (double)BytesReceived / TotalBytes) : 0;
    public TimeSpan? Eta => BytesPerSecond > 1 ? TimeSpan.FromSeconds((TotalBytes - BytesReceived) / BytesPerSecond) : null;
}

/// <summary>
/// The on-device model library: one folder per catalog entry under a root the app chooses
/// (Application Support, excluded from iCloud backup by the app's platform hook), files
/// stored under their catalog names so the engine's companion discovery (a projector
/// beside its model, a VAE beside its DiT) works unchanged.
/// </summary>
public sealed class ModelStore
{
    private readonly ResumableDownloader _downloader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Root directory holding one sub-folder per catalog entry.</summary>
    public string Root { get; }

    /// <summary>Called for every file the store creates, so a platform can mark it (e.g. as
    /// excluded from backup). Best effort; exceptions are swallowed.</summary>
    public Action<string>? OnFileCreated { get; set; }

    public ModelStore(string root, ResumableDownloader? downloader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _downloader = downloader ?? new ResumableDownloader();
    }

    public string DirectoryFor(CatalogModel model) => Path.Combine(Root, model.Id);

    public string PathFor(CatalogModel model, CatalogFile file) => Path.Combine(DirectoryFor(model), file.FileName);

    /// <summary>Path of the loadable weights, or null when not installed.</summary>
    public string? WeightsPath(CatalogModel model) =>
        StateOf(model) == InstallState.Installed ? PathFor(model, model.Weights) : null;

    /// <summary>Path of an installed optional/required companion by role, or null.</summary>
    public string? CompanionPath(CatalogModel model, CatalogFileRole role)
    {
        CatalogFile? file = model.Files.FirstOrDefault(f => f.Role == role);
        if (file is null)
            return null;
        string path = PathFor(model, file);
        return IsComplete(path, file) ? path : null;
    }

    public InstallState StateOf(CatalogModel model)
    {
        bool any = false, all = true;
        foreach (CatalogFile file in model.Files)
        {
            if (file.Optional)
                continue;
            bool present = IsComplete(PathFor(model, file), file);
            any |= present;
            all &= present;
        }
        if (all) return InstallState.Installed;
        if (any || Directory.Exists(DirectoryFor(model)) && Directory.EnumerateFileSystemEntries(DirectoryFor(model)).Any())
            return InstallState.Partial;
        return InstallState.NotInstalled;
    }

    public bool IsFileInstalled(CatalogModel model, CatalogFile file) => IsComplete(PathFor(model, file), file);

    /// <summary>Bytes on disk for the entry, including partial files.</summary>
    public long InstalledBytes(CatalogModel model)
    {
        string dir = DirectoryFor(model);
        if (!Directory.Exists(dir))
            return 0;
        long total = 0;
        foreach (string f in Directory.EnumerateFiles(dir))
            total += new FileInfo(f).Length;
        return total;
    }

    /// <summary>Bytes still to transfer for the given files (required ones by default).</summary>
    public long RemainingBytes(CatalogModel model, bool includeOptional = false)
    {
        long remaining = 0;
        foreach (CatalogFile file in model.Files)
        {
            if (file.Optional && !includeOptional)
                continue;
            string path = PathFor(model, file);
            if (IsComplete(path, file))
                continue;
            string part = ResumableDownloader.PartPath(path);
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            remaining += Math.Max(0, file.Bytes - have);
        }
        return remaining;
    }

    /// <summary>
    /// Download every required file (and the optional ones named in
    /// <paramref name="optionalRoles"/>) that is not already complete. Files are fetched one
    /// after another - a phone's link is the bottleneck, not the server - and each is
    /// verified against its SHA-256 before it is renamed into place.
    /// </summary>
    public async Task DownloadAsync(
        CatalogModel model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct,
        IReadOnlyCollection<CatalogFileRole>? optionalRoles = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryFor(model));
            var wanted = model.Files
                .Where(f => !f.Optional || (optionalRoles?.Contains(f.Role) ?? false))
                .ToList();
            long total = wanted.Sum(f => f.Bytes);
            long doneBefore = 0;
            var pending = new List<CatalogFile>();
            foreach (CatalogFile file in wanted)
            {
                if (IsComplete(PathFor(model, file), file))
                    doneBefore += file.Bytes;
                else
                    pending.Add(file);
            }

            for (int i = 0; i < pending.Count; i++)
            {
                CatalogFile file = pending[i];
                string path = PathFor(model, file);
                long baseBytes = doneBefore;
                int index = i + 1;
                var fileProgress = new Progress<DownloadProgress>(p =>
                    progress?.Report(new ModelDownloadProgress(
                        file.FileName, index, pending.Count, baseBytes + p.BytesReceived, total, p.BytesPerSecond, p.Phase)));
                await _downloader.DownloadAsync(file.Url, path, file.Bytes, file.Sha256, fileProgress, ct).ConfigureAwait(false);
                doneBefore += file.Bytes;
                Notify(path);
            }
            progress?.Report(new ModelDownloadProgress(string.Empty, pending.Count, pending.Count, total, total, 0, "downloading"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Remove every file of the entry, partial downloads included.</summary>
    public void Delete(CatalogModel model)
    {
        string dir = DirectoryFor(model);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>Remove one optional companion (e.g. a projector) to free memory/disk.</summary>
    public void DeleteFile(CatalogModel model, CatalogFile file)
    {
        string path = PathFor(model, file);
        if (File.Exists(path)) File.Delete(path);
        string part = ResumableDownloader.PartPath(path);
        if (File.Exists(part)) File.Delete(part);
    }

    private static bool IsComplete(string path, CatalogFile file) =>
        File.Exists(path) && new FileInfo(path).Length == file.Bytes;

    private void Notify(string path)
    {
        try { OnFileCreated?.Invoke(path); }
        catch { /* a backup-exclusion failure must not fail the download */ }
    }
}
