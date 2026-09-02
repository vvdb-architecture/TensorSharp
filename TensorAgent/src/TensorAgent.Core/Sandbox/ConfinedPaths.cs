// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorSharp.AgentHost.Skills;

namespace TensorAgent.Core.Sandbox;

/// <summary>How a path is about to be used; decides which roots may contain it.</summary>
public enum PathAccess
{
    Read,
    Write,
}

/// <summary>
/// A path was outside what the policy allows. The message is exactly what a
/// command prints — <c>PATH: Permission denied</c> — because that is what the
/// OS sandbox would have said on macOS and the model already knows how to read it.
/// </summary>
public sealed class ConfinementException : IOException
{
    public ConfinementException(string path, string? detail = null)
        : base(detail == null ? $"{path}: Permission denied" : $"{path}: Permission denied ({detail})")
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// The one place a runtime decides whether a path is inside the policy.
///
/// <para>
/// Lexical containment is not enough: a symlink inside the work directory that
/// points at <c>/etc</c> would pass a prefix check and then read anything. So the
/// check follows <see cref="SkillPathGuard"/>'s rule and walks every existing
/// component, resolving links as it goes, and compares the REAL path against the
/// REAL roots. Roots are resolved the same way because on macOS the temp
/// directory itself is a link (<c>/var</c> → <c>/private/var</c>).
/// </para>
/// </summary>
public sealed class ConfinedPaths
{
    private readonly string[] _writable;
    private readonly string[] _readable;

    public ConfinedPaths(ExecutionPolicy policy, IEnumerable<string>? extraReadable = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;

        var writable = new List<string> { RealPath(policy.WorkRoot), RealPath(policy.TempRoot) };
        // An exact writable path is a root of exactly one file, which is what makes it
        // safe to grant: the prefix test below then matches that file and nothing beside it.
        foreach (string path in policy.WritablePaths)
            writable.Add(RealPath(path));
        var readable = new List<string>(writable);
        foreach (string root in policy.ReadableRoots)
            readable.Add(RealPath(root));
        if (!string.IsNullOrEmpty(policy.PackageRoot))
            readable.Add(RealPath(policy.PackageRoot));
        if (extraReadable != null)
        {
            foreach (string root in extraReadable)
                readable.Add(RealPath(root));
        }

        _writable = writable.ToArray();
        _readable = readable.Distinct(StringComparer.Ordinal).ToArray();
    }

    public ExecutionPolicy Policy { get; }

    /// <summary>Real, canonical roots that may be written into.</summary>
    public IReadOnlyList<string> WritableRoots => _writable;

    /// <summary>Real, canonical roots that may be read (includes the writable ones).</summary>
    public IReadOnlyList<string> ReadableRoots => _readable;

    /// <summary>
    /// The pseudo-files every shell command line uses. They are not on any root and
    /// need no check; callers special-case them before opening anything.
    /// </summary>
    public static bool IsDevNull(string path) => path is "/dev/null";

    /// <summary>
    /// Resolve <paramref name="path"/> (relative to <paramref name="cwd"/>) to the real
    /// absolute path, or throw <see cref="ConfinementException"/> when the policy does
    /// not allow the access.
    /// </summary>
    public string Resolve(string path, string cwd, PathAccess access)
    {
        if (!TryResolve(path, cwd, access, out string resolved, out string? error))
            throw new ConfinementException(path, error);
        return resolved;
    }

    /// <summary>Non-throwing form of <see cref="Resolve"/>.</summary>
    public bool TryResolve(string path, string cwd, PathAccess access, out string resolved, out string? error)
    {
        resolved = string.Empty;
        error = null;

        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0)
        {
            error = "empty path";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }

        string real = RealPath(full);
        if (!IsAllowed(real, access))
        {
            error = null;
            return false;
        }

        resolved = real;
        return true;
    }

    /// <summary>True when the REAL path <paramref name="realPath"/> sits under a root that permits <paramref name="access"/>.</summary>
    public bool IsAllowed(string realPath, PathAccess access)
    {
        string[] roots = access == PathAccess.Write ? _writable : _readable;
        foreach (string root in roots)
        {
            if (SkillPathGuard.IsUnder(root, realPath))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The real path of <paramref name="path"/>: every component that exists is
    /// followed through any chain of links; components that do not exist yet are
    /// appended as written. Never throws — a broken link or an unreadable directory
    /// is treated as "does not exist", and the containment check above still holds.
    /// </summary>
    public static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        string current = root;
        string rest = full.Substring(root.Length);
        foreach (string segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(current, segment);
            string? final = ResolveFinal(candidate);
            current = final ?? candidate;
        }

        return SkillPathGuard.NormalizeDirectory(current);
    }

    private static string? ResolveFinal(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                // A dangling link "exists" as a link even though its target does not.
                if (info.LinkTarget == null)
                    return null;
            }

            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
