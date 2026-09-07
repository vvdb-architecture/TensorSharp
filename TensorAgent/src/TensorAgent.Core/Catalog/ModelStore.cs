// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Buffers;
using System.Security.Cryptography;
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
        if (model.SideloadOnly)
        {
            throw new InvalidOperationException(
                $"{model.DisplayName} has no verified publisher download URL. Import " +
                $"the hash-pinned {model.Weights.FileName} file from the Models page instead.");
        }
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

    /// <summary>
    /// Import the exact local artifact described by a sideload-only catalog card.
    /// Bytes are copied to a same-directory staging file, checked for both length and
    /// SHA-256, and only then atomically replace the loadable destination. A wrong pick,
    /// cancellation, or read error therefore cannot damage a previously imported model.
    /// </summary>
    public async Task ImportAsync(
        CatalogModel model,
        Stream source,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        if (!model.SideloadOnly)
            throw new InvalidOperationException($"{model.DisplayName} is downloaded by the catalog, not imported.");
        if (!source.CanRead)
            throw new ArgumentException("The selected model file cannot be read.", nameof(source));

        CatalogFile weights = model.Weights;
        string directory = DirectoryFor(model);
        string destination = PathFor(model, weights);
        string staging = destination + ".import-" + Guid.NewGuid().ToString("N");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            Directory.CreateDirectory(directory);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            await using (var target = new FileStream(
                staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    copied = checked(copied + read);
                    if (copied > weights.Bytes)
                    {
                        throw new InvalidDataException(
                            $"{Path.GetFileName(weights.FileName)} is larger than the expected " +
                            $"{weights.Bytes:N0} bytes and is not the pinned catalog artifact.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(copied);
                }
                await target.FlushAsync(ct).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }

            if (copied != weights.Bytes)
            {
                throw new InvalidDataException(
                    $"The selected file is {copied:N0} bytes; {weights.FileName} must be " +
                    $"exactly {weights.Bytes:N0} bytes.");
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, weights.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The selected file's SHA-256 is {actualHash}, not the pinned {weights.Sha256}. " +
                    "Choose the exact GGUF named on the card.");
            }

            File.Move(staging, destination, overwrite: true);
            Notify(destination);
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An abandoned staging file is never loadable and must not mask the
                // validation/read error that caused this cleanup path.
            }
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

    /// <summary>
    /// Delete model directories no catalog entry claims, and say how much that freed.
    ///
    /// <para>
    /// A directory is named by its entry's id, and an id changes whenever the entry
    /// changes which FILE it points at -- swapping Gemma 4 12B from UD-IQ3_XXS to
    /// UD-IQ2_M turns <c>gemma-4-12b-iq3xxs</c> into <c>gemma-4-12b-iq2m</c>. The old
    /// directory then belongs to no entry, so the Models list cannot show it and the
    /// user cannot delete it: 4.6 GB of a superseded quantization, invisible,
    /// on a device where storage is the scarcest thing there is. This is the only place
    /// that can reclaim it.
    /// </para>
    /// <para>
    /// Checked against the WHOLE catalog rather than what this device is offered
    /// (<see cref="ModelCatalog.ForDevice"/>), because an entry gated to a larger device
    /// is still a real entry -- deleting weights for a model an iPad can run, because a
    /// phone cannot, would be a data-loss bug wearing a tidy-up's clothes.
    /// </para>
    /// </summary>
    /// <returns>Bytes freed.</returns>
    public long SweepOrphanedModels(IReadOnlyList<CatalogModel>? catalog = null)
    {
        var known = new HashSet<string>(
            (catalog ?? ModelCatalog.BuiltIn).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        long freed = 0;
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        foreach (string directory in directories.ToList())
        {
            if (known.Contains(Path.GetFileName(directory)))
                continue;
            try
            {
                long bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                Directory.Delete(directory, recursive: true);
                freed += bytes;
                Console.WriteLine(
                    $"TensorAgent: removed {Path.GetFileName(directory)}, which no catalog entry "
                    + $"claims any more ({bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB freed)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A sweep that cannot delete is not a reason to fail a launch.
                Console.WriteLine($"TensorAgent: could not remove {directory}: {ex.Message}");
            }
        }

        // And loose FILES, which belong to no entry by construction: this directory
        // holds one sub-folder per catalog id (see DirectoryFor) and nothing else ever
        // writes into it. One can still arrive -- a weights file pushed onto the device
        // by hand, landing beside the per-model folders instead of inside one -- and it
        // is worse off than an orphaned directory: the Models list is built from entries,
        // so a stray file has no row, no size against any model, and no delete button,
        // while being the largest kind of file this app deals in.
        IEnumerable<string> strays;
        try { strays = Directory.EnumerateFiles(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return freed; }

        foreach (string file in strays.ToList())
        {
            try
            {
                long bytes = new FileInfo(file).Length;
                File.Delete(file);
                freed += bytes;
                Console.WriteLine(
                    $"TensorAgent: removed the stray file {Path.GetFileName(file)} from the models "
                    + $"directory, which no catalog entry claims ({bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB freed)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"TensorAgent: could not remove {file}: {ex.Message}");
            }
        }
        return freed;
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
