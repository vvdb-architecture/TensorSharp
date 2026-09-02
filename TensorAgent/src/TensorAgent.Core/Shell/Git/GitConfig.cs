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

namespace TensorAgent.Core.Shell.Git;

/// <summary>
/// <c>.git/config</c>, and only <c>.git/config</c>.
///
/// <para>
/// Real git layers four files: system, global (<c>~/.gitconfig</c>), local, and worktree.
/// The three outside the repository are deliberately not read here — the session's home
/// directory is outside the confinement, so reading it would be refused anyway, and a
/// user's global identity is not something a sandboxed agent should silently sign
/// commits with. The identity therefore comes from, in order: the <c>GIT_AUTHOR_*</c> /
/// <c>GIT_COMMITTER_*</c> environment variables, this file, and then a stated default —
/// never from a file the session cannot see.
/// </para>
/// <para>
/// The parser handles what git's own does for the keys that matter here: <c>[section]</c>
/// and <c>[section "subsection"]</c> headers, <c>key = value</c>, <c>key</c> alone
/// (meaning true), <c>#</c> and <c>;</c> comments, double-quoted values with backslash
/// escapes, and line continuations. Key names are case-insensitive; subsection names are
/// not, which is git's rule and not a typo.
/// </para>
/// </summary>
internal sealed class GitConfig
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static GitConfig Empty() => new();

    public static GitConfig Read(GitFileGate gate, string path)
    {
        var config = new GitConfig();
        if (!gate.FileExists(path))
            return config;
        config.ParseInto(gate.ReadAllText(path));
        return config;
    }

    internal void ParseInto(string text)
    {
        string section = string.Empty;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            if (line[0] == '[')
            {
                int close = line.LastIndexOf(']');
                if (close < 0)
                    continue;
                string header = line[1..close].Trim();
                int quote = header.IndexOf('"');
                if (quote >= 0)
                {
                    int endQuote = header.LastIndexOf('"');
                    string name = header[..quote].Trim().ToLowerInvariant();
                    string sub = endQuote > quote ? Unescape(header[(quote + 1)..endQuote]) : string.Empty;
                    section = name + "." + sub + ".";
                }
                else
                {
                    section = header.ToLowerInvariant() + ".";
                }

                continue;
            }

            int eq = line.IndexOf('=');
            string key;
            string value;
            if (eq < 0)
            {
                key = StripComment(line).Trim();
                value = "true";
            }
            else
            {
                key = line[..eq].Trim();
                value = ParseValue(line[(eq + 1)..]);
            }

            if (key.Length == 0)
                continue;
            string full = section + key.ToLowerInvariant();
            if (!_values.TryGetValue(full, out List<string>? list))
                _values[full] = list = new List<string>();
            list.Add(value);
        }
    }

    /// <summary>Strips an unquoted trailing comment, then unescapes and unquotes what is left.</summary>
    private static string ParseValue(string raw)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                i++;
                sb.Append(raw[i] switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => raw[i] });
                continue;
            }

            if (!quoted && (c == '#' || c == ';'))
                break;
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOfAny(new[] { '#', ';' });
        return hash < 0 ? line : line[..hash];
    }

    private static string Unescape(string text) => text.Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>The last value set for <c>section.key</c> or <c>section.sub.key</c>, or null.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out List<string>? list) && list.Count > 0 ? list[^1] : null;

    public bool GetBool(string key, bool fallback)
    {
        string? value = Get(key);
        if (value == null)
            return fallback;
        return value.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" or "" => false,
            _ => fallback,
        };
    }

    /// <summary>Every key set, in no particular order — what <c>config --list</c> enumerates.</summary>
    public IEnumerable<KeyValuePair<string, string>> All()
    {
        foreach ((string key, List<string> values) in _values)
        {
            foreach (string value in values)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    /// <summary>
    /// Rewrites <c>.git/config</c> with one key changed. The file is regenerated from the
    /// parsed model rather than patched in place, so a comment in the original is lost —
    /// stated here because that is a real, if minor, difference from <c>git config</c>,
    /// which edits the line and leaves everything around it alone.
    /// </summary>
    public void Set(string key, string value)
    {
        _values[key] = new List<string> { value };
    }

    public bool Unset(string key) => _values.Remove(key);

    public string Serialize()
    {
        var bySection = new SortedDictionary<string, List<(string Key, string Value)>>(StringComparer.Ordinal);
        foreach ((string full, List<string> values) in _values)
        {
            int lastDot = full.LastIndexOf('.');
            string section = lastDot < 0 ? string.Empty : full[..lastDot];
            string key = lastDot < 0 ? full : full[(lastDot + 1)..];
            if (!bySection.TryGetValue(section, out List<(string, string)>? list))
                bySection[section] = list = new List<(string, string)>();
            foreach (string value in values)
                list.Add((key, value));
        }

        var sb = new StringBuilder();
        foreach ((string section, List<(string Key, string Value)> entries) in bySection)
        {
            int dot = section.IndexOf('.');
            if (dot < 0)
                sb.Append('[').Append(section).Append("]\n");
            else
                sb.Append('[').Append(section[..dot]).Append(" \"").Append(section[(dot + 1)..]).Append("\"]\n");
            foreach ((string key, string value) in entries)
                sb.Append('\t').Append(key).Append(" = ").Append(value).Append('\n');
        }

        return sb.ToString();
    }
}
