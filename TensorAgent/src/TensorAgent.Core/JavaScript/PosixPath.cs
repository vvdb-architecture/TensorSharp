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

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// Node's <c>path.posix</c>, reimplemented.
///
/// <para>
/// <see cref="System.IO.Path"/> looks like it would do, and then quietly does not:
/// <c>Path.GetDirectoryName("a")</c> is the empty string where Node's
/// <c>path.dirname('a')</c> is <c>"."</c>, <c>Path.GetFullPath</c> collapses a
/// trailing separator that <c>path.normalize</c> keeps, and <c>Path.Combine</c>
/// throws away everything to the left of an absolute segment where
/// <c>path.join</c> does not. Those differences are exactly the ones a script
/// notices, so the module a script gets is written against Node's rules rather
/// than .NET's.
/// </para>
/// </summary>
internal static class PosixPath
{
    internal const char Separator = '/';

    internal static bool IsAbsolute(string path) => path.Length > 0 && path[0] == Separator;

    /// <summary>
    /// Collapses <c>.</c> and <c>..</c>. A trailing separator is preserved, as
    /// Node preserves it; an empty result becomes <c>"."</c>.
    /// </summary>
    internal static string Normalize(string path)
    {
        if (path.Length == 0)
            return ".";
        bool absolute = IsAbsolute(path);
        bool trailing = path[^1] == Separator;

        var parts = new List<string>();
        foreach (string segment in path.Split(Separator))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment != "..")
            {
                parts.Add(segment);
                continue;
            }
            if (parts.Count > 0 && parts[^1] != "..")
                parts.RemoveAt(parts.Count - 1);
            else if (!absolute)
                parts.Add("..");
            // Above the root of an absolute path, ".." is the root itself.
        }

        string joined = string.Join(Separator, parts);
        if (absolute)
            return joined.Length == 0 ? "/" : "/" + joined + (trailing ? "/" : string.Empty);
        if (joined.Length == 0)
            return ".";
        return trailing ? joined + "/" : joined;
    }

    /// <summary>Node's <c>path.join</c>: concatenate the non-empty parts, then normalize.</summary>
    internal static string Join(IReadOnlyList<string> parts)
    {
        var sb = new StringBuilder();
        foreach (string part in parts)
        {
            if (part.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append(Separator);
            sb.Append(part);
        }
        return sb.Length == 0 ? "." : Normalize(sb.ToString());
    }

    /// <summary>
    /// Node's <c>path.resolve</c>: walk the parts from the right until one is
    /// absolute, falling back to <paramref name="cwd"/>. The result never keeps a
    /// trailing separator (except for the root itself).
    /// </summary>
    internal static string Resolve(string cwd, IReadOnlyList<string> parts)
    {
        string resolved = string.Empty;
        bool absolute = false;
        for (int i = parts.Count - 1; i >= 0 && !absolute; i--)
        {
            string part = parts[i];
            if (part.Length == 0)
                continue;
            resolved = resolved.Length == 0 ? part : part + Separator + resolved;
            absolute = IsAbsolute(part);
        }
        if (!absolute)
            resolved = resolved.Length == 0 ? cwd : cwd + Separator + resolved;

        string normalized = Normalize(resolved);
        if (normalized.Length > 1 && normalized[^1] == Separator)
            normalized = normalized[..^1];
        return normalized.Length == 0 ? "/" : normalized;
    }

    internal static string Dirname(string path)
    {
        if (path.Length == 0)
            return ".";
        bool absolute = IsAbsolute(path);
        int end = path.Length - 1;
        while (end > 0 && path[end] == Separator)
            end--;
        int slash = path.LastIndexOf(Separator, end);
        if (slash < 0)
            return absolute ? "/" : ".";
        if (slash == 0)
            return "/";
        return path[..slash];
    }

    internal static string Basename(string path, string? suffix = null)
    {
        if (path.Length == 0)
            return string.Empty;
        int end = path.Length;
        while (end > 0 && path[end - 1] == Separator)
            end--;
        if (end == 0)
            return string.Empty;
        int slash = path.LastIndexOf(Separator, end - 1);
        string name = path[(slash + 1)..end];
        if (!string.IsNullOrEmpty(suffix) && name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];
        return name;
    }

    /// <summary>The last dot onward, with Node's rule that a leading dot is not an extension.</summary>
    internal static string Extname(string path)
    {
        string name = Basename(path);
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }

    internal static string Relative(string cwd, string from, string to)
    {
        string fromAbsolute = Resolve(cwd, new[] { from });
        string toAbsolute = Resolve(cwd, new[] { to });
        if (string.Equals(fromAbsolute, toAbsolute, StringComparison.Ordinal))
            return string.Empty;

        string[] fromParts = fromAbsolute.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        string[] toParts = toAbsolute.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < fromParts.Length && common < toParts.Length
               && string.Equals(fromParts[common], toParts[common], StringComparison.Ordinal))
            common++;

        var parts = new List<string>();
        for (int i = common; i < fromParts.Length; i++)
            parts.Add("..");
        for (int i = common; i < toParts.Length; i++)
            parts.Add(toParts[i]);
        return string.Join(Separator, parts);
    }
}
