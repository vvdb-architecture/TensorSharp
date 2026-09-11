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
/// POSIX basic and extended regular expressions rendered as .NET regular expressions,
/// for <c>sed</c>, <c>grep</c> and <c>awk</c>.
///
/// <para>
/// The translation covers what shows up on a command line: BRE's escaped grouping
/// and intervals (<c>\(\)</c>, <c>\{m,n\}</c>) and the GNU escapes (<c>\+</c>,
/// <c>\?</c>, <c>\|</c>), bracket expressions with character classes, word
/// boundaries (<c>\&lt;</c>, <c>\&gt;</c>, <c>\b</c>), and the back-references both
/// dialects share. What it does not do is emulate leftmost-longest alternation:
/// <c>a|ab</c> on "ab" matches "a" here and "ab" in GNU grep. That difference has
/// never mattered in a shell one-liner.
/// </para>
/// </summary>
internal static class PosixRegex
{
    private static readonly Dictionary<(string, bool, bool), Regex> Cache = new();
    private static readonly object CacheGate = new();

    public static Regex Compile(string pattern, bool extended, bool ignoreCase = false)
    {
        var key = (pattern, extended, ignoreCase);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out Regex? cached))
                return cached;
        }
        string translated = Translate(pattern, extended);
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        Regex regex;
        try
        {
            regex = new Regex(translated, options, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"invalid regular expression '{pattern}': {ex.Message}", ex);
        }
        lock (CacheGate)
        {
            if (Cache.Count > 512)
                Cache.Clear();
            Cache[key] = regex;
        }
        return regex;
    }

    public static string Translate(string pattern, bool extended)
    {
        var sb = new StringBuilder(pattern.Length + 8);
        int groupDepth = 0;
        bool atStart = true; // where '*' is literal and '^' is an anchor in BRE

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '\\':
                {
                    if (i + 1 >= pattern.Length)
                    {
                        sb.Append("\\\\");
                        break;
                    }
                    char n = pattern[++i];
                    if (!extended && n is '(' or ')' or '{' or '}' or '|' or '+' or '?')
                    {
                        // BRE: escaped means special.
                        sb.Append(n);
                        if (n == '(')
                        {
                            groupDepth++;
                            atStart = true;
                            continue;
                        }
                        if (n == '|')
                        {
                            atStart = true;
                            continue;
                        }
                        if (n == ')')
                            groupDepth--;
                        break;
                    }
                    switch (n)
                    {
                        case '<':
                            sb.Append("\\b(?=\\w)");
                            break;
                        case '>':
                            sb.Append("\\b(?<=\\w)");
                            break;
                        case 'n':
                            sb.Append("\\n");
                            break;
                        case 't':
                            sb.Append("\\t");
                            break;
                        case 'r':
                            sb.Append("\\r");
                            break;
                        case 'w':
                        case 'W':
                        case 's':
                        case 'S':
                        case 'd':
                        case 'D':
                        case 'b':
                        case 'B':
                            sb.Append('\\').Append(n);
                            break;
                        case '`':
                            sb.Append("\\A");
                            break;
                        case '\'':
                            sb.Append("\\z");
                            break;
                        default:
                            if (char.IsAsciiDigit(n))
                                sb.Append('\\').Append(n);
                            else
                                sb.Append(Regex.Escape(n.ToString()));
                            break;
                    }
                    break;
                }
                case '[':
                {
                    int close = GlobMatcher.FindBracketClose(pattern, i);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }
                    sb.Append('[');
                    int j = i + 1;
                    if (j < close && pattern[j] == '^')
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
                                sb.Append(GlobMatcher.ClassText(pattern.Substring(j + 2, end - j - 2)));
                                j = end + 1;
                                continue;
                            }
                        }
                        if (b is '\\' or '[' or ']')
                            sb.Append('\\');
                        sb.Append(b);
                    }
                    sb.Append(']');
                    i = close;
                    break;
                }
                case '(':
                case ')':
                case '{':
                case '}':
                case '|':
                case '+':
                case '?':
                    if (extended)
                    {
                        if (c == '{' && !IsInterval(pattern, i))
                        {
                            sb.Append("\\{");
                            break;
                        }
                        sb.Append(c);
                        if (c == '(')
                        {
                            groupDepth++;
                            atStart = true;
                            continue;
                        }
                        if (c == '|')
                        {
                            atStart = true;
                            continue;
                        }
                        if (c == ')')
                            groupDepth--;
                    }
                    else
                        sb.Append('\\').Append(c);
                    break;
                case '*':
                    if (atStart)
                        sb.Append("\\*");
                    else
                        sb.Append('*');
                    break;
                case '^':
                    if (extended || atStart)
                        sb.Append('^');
                    else
                        sb.Append("\\^");
                    break;
                case '$':
                    if (extended || i == pattern.Length - 1 || (i + 2 <= pattern.Length && pattern[i + 1] == '\\' && i + 2 < pattern.Length && (pattern[i + 2] == ')' || pattern[i + 2] == '|')))
                        sb.Append("(?=\\n?\\z)");
                    else
                        sb.Append("\\$");
                    break;
                case '.':
                    sb.Append('.');
                    break;
                case '#':
                case ' ':
                    sb.Append(c);
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
            atStart = false;
        }
        _ = groupDepth;
        return sb.ToString();
    }

    private static bool IsInterval(string pattern, int open)
    {
        int close = pattern.IndexOf('}', open);
        if (close < 0)
            return false;
        for (int i = open + 1; i < close; i++)
        {
            if (!char.IsAsciiDigit(pattern[i]) && pattern[i] != ',')
                return false;
        }
        return close > open + 1;
    }

    /// <summary>
    /// A sed/awk replacement text as a .NET replacement: <c>&amp;</c> → whole match,
    /// <c>\1</c> → group, <c>\n</c> → newline, <c>\&amp;</c> → literal ampersand.
    /// </summary>
    public static string TranslateReplacement(string replacement)
    {
        var sb = new StringBuilder(replacement.Length + 8);
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c == '\\' && i + 1 < replacement.Length)
            {
                char n = replacement[++i];
                if (char.IsAsciiDigit(n))
                    sb.Append("${").Append(n).Append('}');
                else if (n == 'n')
                    sb.Append('\n');
                else if (n == 't')
                    sb.Append('\t');
                else if (n == '&')
                    sb.Append('&');
                else if (n == '$')
                    sb.Append("$$");
                else
                    sb.Append(n);
            }
            else if (c == '&')
                sb.Append("$0");
            else if (c == '$')
                sb.Append("$$");
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
