// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text;

namespace TensorAgent.Core.Python;

/// <summary>
/// One distribution <c>prepare-python.sh</c> staged into the app bundle.
/// </summary>
/// <param name="Name">The name the distribution's METADATA gives it, e.g. <c>python-pptx</c>.</param>
/// <param name="CanonicalName">PEP 503's spelling of it: lower case, runs of <c>-_.</c> as one dash.</param>
/// <param name="Version">The staged version.</param>
/// <param name="Compiled">
/// True when the distribution ships extension modules. On iOS those are signed
/// frameworks inside the bundle, which is why such a distribution can be imported
/// and can never be replaced by a download.
/// </param>
public sealed record BundledDistribution(string Name, string CanonicalName, string Version, bool Compiled);

/// <summary>
/// What the app bundle already provides, read off the staged package directory.
///
/// <para>
/// The installer needs this before it asks PyPI anything. <c>pip install lxml</c> from
/// a model is not a request to download lxml — it is the model's habit before
/// <c>import lxml</c>, and on this platform the only lxml that can ever load is the
/// one compiled into the bundle. Answering that request by consulting the index found
/// only compiled wheels and refused, in words that told the model lxml was unavailable
/// on a device where it had been importable the whole time. So the bundle is asked
/// first, and the index only for what the bundle does not have.
/// </para>
/// <para>
/// Read from <c>*.dist-info</c> rather than from a list kept in code, so the answer is
/// whatever the staging actually put there: a package added to prepare-python.sh is
/// known here without a second edit, and one removed there stops being promised.
/// </para>
/// </summary>
public static class BundledPackages
{
    private const int MaxMetadataLines = 512;

    /// <summary>
    /// Every distribution under <paramref name="packagesDirectory"/> with a readable
    /// <c>METADATA</c>, sorted by name. An unreadable or missing directory is an empty
    /// answer, not an exception: a build without staged packages has none to offer.
    /// </summary>
    public static IReadOnlyList<BundledDistribution> Scan(string? packagesDirectory)
    {
        if (string.IsNullOrWhiteSpace(packagesDirectory) || !Directory.Exists(packagesDirectory))
            return Array.Empty<BundledDistribution>();

        var found = new List<BundledDistribution>();
        IEnumerable<string> infos;
        try
        {
            infos = Directory.EnumerateDirectories(packagesDirectory, "*.dist-info", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<BundledDistribution>();
        }

        foreach (string info in infos)
        {
            try
            {
                if (TryRead(info, out BundledDistribution? distribution) && distribution is not null)
                    found.Add(distribution);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One damaged dist-info must not hide the rest of the bundle.
            }
        }

        found.Sort((a, b) => string.CompareOrdinal(a.CanonicalName, b.CanonicalName));
        return found;
    }

    /// <summary>PEP 503 normalization, the same rule the installer's ledger uses.</summary>
    public static string Canonical(string name)
    {
        var canonical = new StringBuilder(name.Length);
        bool separator = false;
        foreach (char c in name)
        {
            if (c is '-' or '_' or '.')
            {
                separator = true;
                continue;
            }
            if (separator && canonical.Length > 0)
                canonical.Append('-');
            canonical.Append(char.ToLowerInvariant(c));
            separator = false;
        }
        return canonical.ToString();
    }

    private static bool TryRead(string distInfo, out BundledDistribution? distribution)
    {
        distribution = null;
        string metadata = Path.Combine(distInfo, "METADATA");
        if (!File.Exists(metadata))
            return false;

        string? name = null, version = null;
        int lines = 0;
        foreach (string line in File.ReadLines(metadata))
        {
            // The header block ends at the first blank line; the description follows
            // and can be as long as a README.
            if (line.Length == 0 || lines++ > MaxMetadataLines)
                break;
            if (name is null && line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                name = line[5..].Trim();
            else if (version is null && line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                version = line[8..].Trim();
            if (name is not null && version is not null)
                break;
        }
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
            return false;

        distribution = new BundledDistribution(name, Canonical(name), version, HasCompiledModules(distInfo));
        return true;
    }

    /// <summary>
    /// Whether the wheel carried extension modules. RECORD still names them by their
    /// original <c>.so</c> file names after the staging has turned each one into a
    /// framework and a <c>.fwork</c> placeholder, so both spellings count.
    /// </summary>
    private static bool HasCompiledModules(string distInfo)
    {
        string record = Path.Combine(distInfo, "RECORD");
        if (!File.Exists(record))
            return false;
        foreach (string line in File.ReadLines(record))
        {
            int comma = line.IndexOf(',');
            string path = comma < 0 ? line : line[..comma];
            if (path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".fwork", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".pyd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
