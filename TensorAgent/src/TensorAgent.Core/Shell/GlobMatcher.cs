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
using System.Text.RegularExpressions;

namespace TensorAgent.Core.Shell;

/// <summary>
/// Shell pattern matching (<c>fnmatch</c> semantics: <c>*</c>, <c>?</c>, <c>[...]</c>,
/// <c>[!...]</c>, <c>[[:alpha:]]</c>, backslash escapes) and pathname expansion.
///
/// <para>
/// Written here rather than taken from a globbing package because the two rules a
/// shell user relies on — a leading dot is not matched by <c>*</c>, and a pattern
/// that matches nothing stays literal — are exactly the two a general-purpose
/// matcher does not implement.
/// </para>
/// </summary>
internal static class GlobMatcher
{
    public static bool HasGlobChars(string s) => s.IndexOfAny(new[] { '*', '?', '[' }) >= 0;

    /// <summary>Backslash-escape every glob metacharacter so the text matches only itself.</summary>
    public static string Escape(string literal)
    {
        if (literal.IndexOfAny(new[] { '*', '?', '[', ']', '\\' }) < 0)
            return literal;
        var sb = new StringBuilder(literal.Length + 4);
        foreach (char c in literal)
        {
            if (c is '*' or '?' or '[' or ']' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>fnmatch: the whole of <paramref name="text"/> must match.</summary>
    public static bool IsMatch(string pattern, string text, bool ignoreCase = false)
        => ToRegex(pattern, ignoreCase, matchSlash: true).IsMatch(text);

    public static Regex ToRegex(string pattern, bool ignoreCase, bool matchSlash)
    {
        var sb = new StringBuilder("^");
        Translate(pattern, sb, matchSlash);
        sb.Append('$');
        return new Regex(sb.ToString(), (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>The regex text (unanchored) for <paramref name="pattern"/>, for callers that build larger expressions.</summary>
    public static string ToRegexText(string pattern, bool matchSlash)
    {
        var sb = new StringBuilder();
        Translate(pattern, sb, matchSlash);
        return sb.ToString();
    }

    private static void Translate(string pattern, StringBuilder sb, bool matchSlash)
    {
        string any = matchSlash ? "." : "[^/]";
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '*':
                    if (!matchSlash && i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                    }
                    else
                        sb.Append(any).Append('*');
                    break;
                case '?':
                    sb.Append(any);
                    break;
                case '[':
                {
                    int close = FindBracketClose(pattern, i);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }
                    sb.Append('[');
                    int j = i + 1;
                    if (j < close && (pattern[j] == '!' || pattern[j] == '^'))
                    {
                        sb.Append('^');
                        j++;
                    }
                    for (; j < close; j++)
                    {
                        char b = pattern[j];
                        if (b == '[' && j + 1 < close && pattern[j + 1] == ':')
                        {
                            int end = pattern.IndexOf(":]", j + 2, StringComparison.Ordinal);
                            if (end > 0 && end < close)
                            {
                                sb.Append(ClassText(pattern.Substring(j + 2, end - j - 2)));
                                j = end + 1;
                                continue;
                            }
                        }
                        if (b is '\\' or '[' or ']' or '^')
                            sb.Append('\\');
                        sb.Append(b);
                    }
                    sb.Append(']');
                    i = close;
                    break;
                }
                case '\\':
                    if (i + 1 < pattern.Length)
                    {
                        i++;
                        sb.Append(Regex.Escape(pattern[i].ToString()));
                    }
                    else
                        sb.Append("\\\\");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
    }

    /// <summary>The index of the `]` closing the bracket expression at <paramref name="open"/>, or -1.</summary>
    internal static int FindBracketClose(string pattern, int open)
    {
        int j = open + 1;
        if (j < pattern.Length && (pattern[j] == '!' || pattern[j] == '^'))
            j++;
        if (j < pattern.Length && pattern[j] == ']')
            j++; // a leading ] is literal
        for (; j < pattern.Length; j++)
        {
            if (pattern[j] == '[' && j + 1 < pattern.Length && pattern[j + 1] == ':')
            {
                int end = pattern.IndexOf(":]", j + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    j = end + 1;
                    continue;
                }
            }
            if (pattern[j] == ']')
                return j;
        }
        return -1;
    }

    internal static string ClassText(string name) => name switch
    {
        "alpha" => "a-zA-Z",
        "digit" => "0-9",
        "alnum" => "a-zA-Z0-9",
        "upper" => "A-Z",
        "lower" => "a-z",
        "space" => " \\t\\n\\r\\f\\v",
        "blank" => " \\t",
        "punct" => "!-/:-@\\[-`{-~",
        "print" => " -~",
        "graph" => "!-~",
        "cntrl" => "\\x00-\\x1f\\x7f",
        "xdigit" => "0-9A-Fa-f",
        "word" => "a-zA-Z0-9_",
        _ => string.Empty,
    };

    /// <summary>
    /// Pathname expansion of <paramref name="pattern"/> relative to <paramref name="cwd"/>.
    /// Returns the matches spelled the way the pattern was (relative stays relative),
    /// sorted; empty when nothing matched. Directories that <paramref name="canRead"/>
    /// refuses are not listed. Symbolic links to directories are never descended.
    /// </summary>
    public static List<string> ExpandPath(string pattern, string cwd, Func<string, bool> canRead)
    {
        var results = new List<string>();
        bool absolute = pattern.StartsWith('/');
        string[] segments = pattern.Split('/');
        var prefixes = new List<string> { absolute ? "/" : string.Empty };

        for (int s = absolute ? 1 : 0; s < segments.Length; s++)
        {
            string segment = segments[s];
            bool last = s == segments.Length - 1;
            var next = new List<string>();
            if (segment.Length == 0)
            {
                // "a//b" or a trailing slash
                foreach (string prefix in prefixes)
                    next.Add(prefix.Length == 0 ? "/" : prefix.EndsWith('/') ? prefix : prefix + "/");
                prefixes = next;
                continue;
            }

            if (!HasGlobChars(segment))
            {
                string literal = Unescape(segment);
                foreach (string prefix in prefixes)
                {
                    string candidate = Join(prefix, literal);
                    string full = Path.GetFullPath(Path.Combine(cwd, candidate));
                    if (last ? (File.Exists(full) || Directory.Exists(full)) : Directory.Exists(full))
                        next.Add(candidate);
                }
                prefixes = next;
                continue;
            }

            if (segment == "**")
            {
                foreach (string prefix in prefixes)
                {
                    string dir = Path.GetFullPath(Path.Combine(cwd, prefix.Length == 0 ? "." : prefix));
                    next.Add(prefix);
                    Walk(dir, prefix, next, canRead, 0);
                }
                prefixes = next.Distinct(StringComparer.Ordinal).ToList();
                continue;
            }

            Regex regex = ToRegex(segment, ignoreCase: false, matchSlash: true);
            bool matchHidden = segment.StartsWith('.');
            foreach (string prefix in prefixes)
            {
                string dir = Path.GetFullPath(Path.Combine(cwd, prefix.Length == 0 ? "." : prefix));
                if (!Directory.Exists(dir) || !canRead(dir))
                    continue;
                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (string entry in entries)
                {
                    string name = Path.GetFileName(entry);
                    if (name.StartsWith('.') && !matchHidden)
                        continue;
                    if (!regex.IsMatch(name))
                        continue;
                    if (!last && !Directory.Exists(entry))
                        continue;
                    next.Add(Join(prefix, name));
                }
            }
            prefixes = next;
        }

        results.AddRange(prefixes.Where(p => p.Length > 0));
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static void Walk(string dir, string prefix, List<string> into, Func<string, bool> canRead, int depth)
    {
        if (depth > 32 || !canRead(dir))
            return;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (name.StartsWith('.'))
                continue;
            if (new DirectoryInfo(entry).LinkTarget != null)
                continue;
            string rel = Join(prefix, name);
            into.Add(rel);
            Walk(entry, rel, into, canRead, depth + 1);
        }
    }

    private static string Join(string prefix, string name)
        => prefix.Length == 0 ? name : prefix.EndsWith('/') ? prefix + name : prefix + "/" + name;

    private static string Unescape(string segment)
    {
        if (segment.IndexOf('\\') < 0)
            return segment;
        var sb = new StringBuilder(segment.Length);
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '\\' && i + 1 < segment.Length)
                i++;
            sb.Append(segment[i]);
        }
        return sb.ToString();
    }
}
