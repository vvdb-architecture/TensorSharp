// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Python;

/// <summary>
/// One file PyPI publishes for one version of one package.
/// </summary>
/// <param name="FileName">The wheel or archive name, which is also where its tags are.</param>
/// <param name="Url">Where to fetch it.</param>
/// <param name="Sha256">The digest the index promises, hex, lower case.</param>
/// <param name="PackageType">PyPI's own word: <c>bdist_wheel</c>, <c>sdist</c>.</param>
/// <param name="Yanked">Whether the index has withdrawn it.</param>
internal sealed record PyPiFile(string FileName, string Url, string Sha256, string PackageType, bool Yanked);

/// <summary>
/// The host's <c>pip install</c>: pure-Python wheels, fetched and unpacked here,
/// because there is no pip on this host and no process to run one in.
///
/// <para>
/// It installs less than pip does, on purpose. A wheel with compiled extensions
/// cannot be installed at all — iOS will not load a dynamic library that was not
/// signed into the app bundle, which is why the binary packages the app needs
/// (<c>numpy</c>, <c>Pillow</c>) are staged at build time by
/// <c>prepare-python.sh</c> and why anything else must be pure Python. And
/// dependencies are not resolved: a name the model did not ask for is a name the
/// user did not agree to fetch, so a missing dependency is reported and left for
/// the model to ask for by name.
/// </para>
/// </summary>
public sealed class WheelInstaller : IInstallHook
{
    /// <summary>The index. Fixed here, not read from the command line: the shell already refuses <c>--index-url</c>.</summary>
    internal const string IndexHost = "pypi.org";

    private const string IndexRoot = "https://pypi.org/pypi";

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    private readonly ExecutionPolicy _policy;
    private readonly HttpClient _http;

    /// <summary>
    /// Bound to the session's policy, because <see cref="CanInstall"/> has no
    /// argument and the answer depends entirely on whether that session may
    /// reach the network.
    /// </summary>
    public WheelInstaller(ExecutionPolicy policy, HttpClient? http = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _http = http ?? SharedHttp;
    }

    /// <inheritdoc />
    public bool CanInstall => _policy.AllowNetwork && _policy.IsHostAllowed(IndexHost);

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (!_policy.AllowNetwork)
                return ExecutionPolicy.NetworkDisabledMessage;
            if (!_policy.IsHostAllowed(IndexHost))
                return $"{IndexHost} {ExecutionPolicy.HostNotAllowedSuffix}";
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, string> environment = new Dictionary<string, string>();
        string where = request.TargetDirectory;

        if (!string.Equals(request.Language, "python", StringComparison.OrdinalIgnoreCase))
            return ExecutionResult.Failed($"this installer only handles Python packages, not '{request.Language}'", where, environment);

        // The request's own policy decides, not the one this instance was built
        // with: the session may have changed since.
        if (!request.Policy.AllowNetwork)
            return ExecutionResult.Failed(ExecutionPolicy.NetworkDisabledMessage, where, environment);
        if (!request.Policy.IsHostAllowed(IndexHost))
            return ExecutionResult.Failed($"{IndexHost} {ExecutionPolicy.HostNotAllowedSuffix}", where, environment);
        if (request.Packages.Count == 0)
            return ExecutionResult.Failed("no packages named", where, environment, 2);

        var log = new StringBuilder();
        void Say(string line)
        {
            log.Append(line).Append('\n');
            request.OnOutputLine?.Invoke(line);
        }

        ConfinedPaths confined;
        try
        {
            Directory.CreateDirectory(where);
            // The archive's members are confined to the directory the host chose
            // to install into, which is not the same tree a script may write to.
            confined = new ConfinedPaths(request.Policy with { WorkRoot = where });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ExecutionResult.Failed($"{where} could not be prepared: {ex.Message}", where, environment);
        }

        var failures = new StringBuilder();
        int installed = 0;
        foreach (string spec in request.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySplit(spec, out string name, out string? version, out string? invalid))
            {
                failures.Append(invalid).Append('\n');
                continue;
            }

            string? failure;
            try
            {
                failure = await InstallOneAsync(name, version, where, confined, Say, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                failure = $"{name}: {ex.Message}";
            }

            if (failure is null)
                installed++;
            else
                failures.Append(failure).Append('\n');
        }

        if (failures.Length > 0)
            return new ExecutionResult(1, log.ToString(), failures.ToString(), false, where, environment, TimeSpan.Zero);

        Say($"installed {installed} package{(installed == 1 ? string.Empty : "s")} into {where}");
        return new ExecutionResult(0, log.ToString(), string.Empty, false, where, environment, TimeSpan.Zero);
    }

    private async Task<string?> InstallOneAsync(
        string name,
        string? version,
        string target,
        ConfinedPaths confined,
        Action<string> say,
        CancellationToken cancellationToken)
    {
        string url = version is null
            ? $"{IndexRoot}/{Uri.EscapeDataString(name)}/json"
            : $"{IndexRoot}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/json";

        say($"looking up {name}{(version is null ? string.Empty : "==" + version)}");
        using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return $"{name}: no such package{(version is null ? string.Empty : " or version " + version)} on {IndexHost}";
        if (!response.IsSuccessStatusCode)
            return $"{name}: {IndexHost} answered {(int)response.StatusCode} {response.ReasonPhrase}";

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!TrySelectWheel(json, out PyPiFile? wheel, out string? why) || wheel is null)
            return $"{name}: {why}";

        say($"downloading {wheel.FileName}");
        byte[] payload = await _http.GetByteArrayAsync(wheel.Url, cancellationToken).ConfigureAwait(false);

        string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!string.Equals(digest, wheel.Sha256, StringComparison.OrdinalIgnoreCase))
            return $"{name}: {wheel.FileName} does not match the sha256 {IndexHost} published (got {digest}, expected {wheel.Sha256})";

        using var buffer = new MemoryStream(payload, writable: false);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        if (!TryExtract(archive, target, confined, out int files, out string? refusal))
            return $"{name}: {refusal}";

        say($"installed {wheel.FileName} ({files} files)");
        return null;
    }

    /// <summary>
    /// Picks the one file that can be installed here, or says why none can.
    ///
    /// <para>
    /// Only <c>py3-none-any</c> (or <c>py2.py3-none-any</c>) qualifies: the
    /// <c>none</c> ABI and the <c>any</c> platform together are what "no compiled
    /// code" means in a wheel name. Anything else — a <c>cp313</c> wheel, a
    /// source distribution that would have to be built — is refused in those
    /// words, because "install failed" would send a model looking for a fix that
    /// does not exist on this device.
    /// </para>
    /// </summary>
    internal static bool TrySelectWheel(string indexJson, out PyPiFile? wheel, out string? reason)
    {
        wheel = null;
        reason = null;
        List<PyPiFile> files;
        try
        {
            files = ReadFiles(indexJson);
        }
        catch (JsonException ex)
        {
            reason = $"the index answered with something that is not JSON: {ex.Message}";
            return false;
        }

        if (files.Count == 0)
        {
            reason = $"{IndexHost} lists no files for it";
            return false;
        }

        foreach (PyPiFile file in files)
        {
            if (file.Yanked || !string.Equals(file.PackageType, "bdist_wheel", StringComparison.Ordinal))
                continue;
            if (!IsPureWheel(file.FileName))
                continue;
            if (file.Sha256.Length == 0)
            {
                reason = $"{file.FileName} is published without a sha256, so it cannot be verified";
                return false;
            }
            if (!file.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{file.FileName} is published over {file.Url.Split(':')[0]}, not https";
                return false;
            }
            wheel = file;
            return true;
        }

        bool sawWheel = files.Any(f => string.Equals(f.PackageType, "bdist_wheel", StringComparison.Ordinal));
        reason = sawWheel
            ? "every published wheel is built for a specific interpreter and platform, which means compiled code. "
              + "This host can only install pure-Python (py3-none-any) wheels: extension modules have to be signed "
              + "into the app at build time"
            : "only a source distribution is published, and building one needs a compiler this host does not have. "
              + "This host can only install pure-Python (py3-none-any) wheels";
        return false;
    }

    /// <summary>
    /// True for a wheel whose tags promise no compiled code:
    /// <c>name-version[-build]-pythontag-abitag-platformtag.whl</c> with
    /// <c>none</c> and <c>any</c> in the last two.
    /// </summary>
    internal static bool IsPureWheel(string fileName)
    {
        if (!fileName.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = fileName[..^4].Split('-');
        if (parts.Length < 5)
            return false;
        string platform = parts[^1];
        string abi = parts[^2];
        string python = parts[^3];
        if (!string.Equals(abi, "none", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(platform, "any", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (string tag in python.Split('.'))
        {
            if (tag.StartsWith("py3", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("cp3", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Unpacks a wheel, putting every member through the same confinement a
    /// script's own writes go through.
    ///
    /// <para>
    /// A member name comes from the archive, not from the user, so it gets both
    /// checks the shell's <c>unzip</c> applies: it must stay under the directory
    /// being extracted into (the zip-slip rule — the confinement alone would let
    /// <c>../../tmp/x</c> through, because the temp root is writable too), and it
    /// must satisfy the confinement, which is what stops a member from landing
    /// through a symlink somewhere else entirely.
    /// </para>
    /// <para>
    /// Every member is checked before any member is written. An archive that
    /// turns hostile halfway through would otherwise leave its first few files
    /// behind, and a refusal that says nothing was installed has to be true.
    /// </para>
    /// </summary>
    internal static bool TryExtract(ZipArchive archive, string target, ConfinedPaths confined, out int files, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(confined);
        files = 0;
        refusal = null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));

        var planned = new List<(ZipArchiveEntry Entry, string Destination)>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string member = entry.FullName.Replace('\\', '/');
            if (member.Length == 0 || member.EndsWith('/'))
                continue;

            if (Path.IsPathRooted(member) || member.StartsWith("../", StringComparison.Ordinal)
                || member.Contains("/../", StringComparison.Ordinal) || member == ".."
                || member.EndsWith("/..", StringComparison.Ordinal))
            {
                refusal = $"the archive member '{entry.FullName}' points outside {root}; nothing was installed";
                return false;
            }

            string combined = Path.GetFullPath(Path.Combine(root, member));
            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                refusal = $"the archive member '{entry.FullName}' points outside {root}; nothing was installed";
                return false;
            }

            if (!confined.TryResolve(combined, root, PathAccess.Write, out string resolved, out _))
            {
                refusal = $"the archive member '{entry.FullName}' would be written outside {root}; nothing was installed";
                return false;
            }

            planned.Add((entry, resolved));
        }

        foreach ((ZipArchiveEntry entry, string destination) in planned)
        {
            string? directory = Path.GetDirectoryName(destination);
            if (directory is { Length: > 0 })
                Directory.CreateDirectory(directory);
            entry.ExtractToFile(destination, overwrite: true);
            files++;
        }

        return true;
    }

    /// <summary>Splits <c>name</c> or <c>name==version</c>, refusing anything a name may not contain.</summary>
    internal static bool TrySplit(string spec, out string name, out string? version, out string? error)
    {
        name = string.Empty;
        version = null;
        error = null;

        string text = spec.Trim();
        int marker = text.IndexOf("==", StringComparison.Ordinal);
        if (marker >= 0)
        {
            version = text[(marker + 2)..].Trim();
            text = text[..marker].Trim();
        }

        if (text.Length == 0 || !IsName(text))
        {
            error = $"'{spec}' is not a package name this host will fetch; "
                + "only names, optionally with ==version, are accepted";
            return false;
        }
        if (version is { Length: 0 })
        {
            error = $"'{spec}' names no version after ==";
            return false;
        }
        if (version is not null && !IsVersion(version))
        {
            error = $"'{spec}' does not name a version this host will fetch";
            return false;
        }

        name = text;
        return true;
    }

    private static bool IsName(string value)
    {
        if (!char.IsAsciiLetterOrDigit(value[0]) || !char.IsAsciiLetterOrDigit(value[^1]))
            return false;
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                return false;
        }
        return true;
    }

    private static bool IsVersion(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '+' or '!'))
                return false;
        }
        return true;
    }

    private static List<PyPiFile> ReadFiles(string indexJson)
    {
        var files = new List<PyPiFile>();
        using JsonDocument document = JsonDocument.Parse(indexJson);
        if (!document.RootElement.TryGetProperty("urls", out JsonElement urls) || urls.ValueKind != JsonValueKind.Array)
            return files;

        foreach (JsonElement entry in urls.EnumerateArray())
        {
            string fileName = Text(entry, "filename");
            string url = Text(entry, "url");
            string type = Text(entry, "packagetype");
            string sha = string.Empty;
            if (entry.TryGetProperty("digests", out JsonElement digests) && digests.ValueKind == JsonValueKind.Object)
                sha = Text(digests, "sha256");
            bool yanked = entry.TryGetProperty("yanked", out JsonElement yankedValue)
                && yankedValue.ValueKind == JsonValueKind.True;
            if (fileName.Length > 0 && url.Length > 0)
                files.Add(new PyPiFile(fileName, url, sha.ToLower(CultureInfo.InvariantCulture), type, yanked));
        }
        return files;
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
