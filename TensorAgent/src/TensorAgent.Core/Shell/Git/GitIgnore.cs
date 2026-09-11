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

namespace TensorAgent.Core.Shell.Git;

/// <summary>
/// <c>.gitignore</c> matching, enough of it to keep <c>git status</c> honest.
///
/// <para>
/// This matters more than it looks. <c>status</c> is the subcommand an agent runs to
/// decide what to stage, and a workspace with a <c>node_modules</c> or a
/// <c>__pycache__</c> in it buries the three files it actually changed under thousands of
/// untracked lines. Ignoring <c>.gitignore</c> would make <c>status</c> useless exactly
/// when the workspace is real.
/// </para>
/// <para>
/// <b>Implemented:</b> per-directory <c>.gitignore</c> files with rules scoped to their
/// own directory, <c>.git/info/exclude</c>, blank lines and <c>#</c> comments,
/// <c>!</c> negation with last-match-wins, trailing <c>/</c> for directory-only,
/// a leading or embedded <c>/</c> anchoring the pattern to the file's directory,
/// basename patterns matching at any depth, and <c>**</c>.
/// <b>Not implemented:</b> the global excludes file (<c>core.excludesFile</c> lives
/// outside the session), <c>.git/info/sparse-checkout</c>, and attribute-driven
/// exclusion. Each is stated rather than silently approximated because an unexpectedly
/// <em>ignored</em> file is the dangerous direction — it is the one an agent then fails
/// to commit.
/// </para>
/// </summary>
internal sealed class GitIgnore
{
    private readonly List<Level> _levels = new();

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly);

    private sealed record Level(string Directory, List<Rule> Rules);

    /// <summary>
    /// Starts a matcher for <paramref name="workTree"/> holding the repository-wide
    /// excludes. Per-directory files are pushed by the scan as it descends.
    /// </summary>
    public static GitIgnore Create(GitFileGate gate, string gitDir, string workTree)
    {
        var ignore = new GitIgnore();
        string exclude = Path.Combine(gitDir, "info", "exclude");
        var rules = new List<Rule>();
        if (gate.FileExists(exclude))
            Parse(gate.ReadAllLines(exclude), rules);
        ignore._levels.Add(new Level(string.Empty, rules));
        return ignore;
    }

    /// <summary>Pushes the <c>.gitignore</c> of <paramref name="relativeDirectory">a directory</paramref> as it is entered.</summary>
    public void Push(GitFileGate gate, string workTree, string relativeDirectory)
    {
        string file = Path.Combine(workTree, relativeDirectory.Replace('/', Path.DirectorySeparatorChar), ".gitignore");
        var rules = new List<Rule>();
        if (gate.FileExists(file))
        {
            Parse(gate.ReadAllLines(file), rules);
        }

        _levels.Add(new Level(relativeDirectory, rules));
    }

    public void Pop() => _levels.RemoveAt(_levels.Count - 1);

    /// <summary>
    /// Asks about one path outside a walk, building the ancestor stack for it first.
    ///
    /// <para>
    /// <see cref="IsIgnored"/> answers against whatever levels are currently pushed, which
    /// during the scan is exactly the directory being read. A one-off question —
    /// <c>git add build/out.o</c> asking whether that named file is excluded — has no such
    /// stack, and asking a freshly created matcher would consult only
    /// <c>info/exclude</c> and quietly answer "not ignored" for everything the root
    /// <c>.gitignore</c> covers. This pushes each ancestor's rules on the way down, and
    /// stops early when a parent directory is itself excluded, since git does not descend
    /// into one.
    /// </para>
    /// </summary>
    public static bool IsPathIgnored(GitFileGate gate, string gitDir, string workTree, string relativePath, bool isDirectory)
    {
        var ignore = Create(gate, gitDir, workTree);
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        ignore.Push(gate, workTree, string.Empty);
        string prefix = string.Empty;
        for (int i = 0; i < segments.Length; i++)
        {
            string current = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
            bool last = i == segments.Length - 1;
            if (ignore.IsIgnored(current, last ? isDirectory : true))
                return true;
            if (last)
                break;
            ignore.Push(gate, workTree, current);
            prefix = current;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="path"/> (relative to the work tree, slash-separated) is
    /// excluded. Rules are consulted deepest-first, and within one file the last match
    /// wins — which together are git's precedence: a nested <c>.gitignore</c> overrides
    /// its parent, and <c>!keep.txt</c> after <c>*.txt</c> keeps the file.
    /// </summary>
    public bool IsIgnored(string path, bool isDirectory)
    {
        for (int level = _levels.Count - 1; level >= 0; level--)
        {
            Level here = _levels[level];
            if (here.Rules.Count == 0)
                continue;
            string relative = here.Directory.Length == 0 ? path : path[(here.Directory.Length + 1)..];
            for (int i = here.Rules.Count - 1; i >= 0; i--)
            {
                Rule rule = here.Rules[i];
                if (rule.DirectoryOnly && !isDirectory)
                    continue;
                if (rule.Pattern.IsMatch(relative))
                    return !rule.Negated;
            }
        }

        return false;
    }

    private static void Parse(IEnumerable<string> lines, List<Rule> into)
    {
        foreach (string raw in lines)
        {
            string line = raw;
            if (line.Length == 0 || line[0] == '#')
                continue;

            // Trailing whitespace is not part of a pattern unless it was escaped.
            int end = line.Length;
            while (end > 0 && line[end - 1] == ' ' && (end < 2 || line[end - 2] != '\\'))
                end--;
            line = line[..end];
            if (line.Length == 0)
                continue;

            bool negated = line[0] == '!';
            if (negated)
                line = line[1..];
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
                line = line[1..];
            if (line.Length == 0)
                continue;

            bool directoryOnly = line[^1] == '/';
            if (directoryOnly)
                line = line[..^1];
            if (line.Length == 0)
                continue;

            // A slash anywhere but at the end anchors the pattern to this .gitignore's
            // directory; without one, the pattern matches a basename at any depth.
            bool anchored = line.IndexOf('/') >= 0;
            if (line[0] == '/')
                line = line[1..];
            if (line.Length == 0)
                continue;

            var sb = new StringBuilder("^");
            if (!anchored)
                sb.Append("(?:.*/)?");
            sb.Append(TranslateSegments(line));

            // Ignoring a directory ignores everything beneath it, which matters for a
            // direct query even though the scan also stops descending.
            sb.Append("(?:/.*)?$");
            into.Add(new Rule(
                new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline),
                negated,
                directoryOnly));
        }
    }

    /// <summary>
    /// Translates a gitignore pattern. The shell's <see cref="GlobMatcher"/> does the
    /// per-segment work — <c>*</c> that does not cross a slash, <c>?</c>, bracket
    /// expressions — and this adds the one rule that is gitignore's own: <c>**</c> as a
    /// whole path segment spans any number of directories, including none, which a plain
    /// <c>.*</c> would get wrong at <c>a/**/b</c> matching <c>a/b</c>.
    /// </summary>
    private static string TranslateSegments(string pattern)
    {
        string[] segments = pattern.Split('/');
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment == "**")
            {
                if (i == segments.Length - 1)
                    sb.Append(sb.Length == 0 ? ".*" : "/.*");
                else if (i == 0)
                    sb.Append("(?:[^/]+/)*");
                else
                    sb.Append("(?:/[^/]+)*/");
                continue;
            }

            if (i > 0 && !sb.ToString().EndsWith("/", StringComparison.Ordinal) && !sb.ToString().EndsWith(")*", StringComparison.Ordinal))
                sb.Append('/');
            else if (i > 0 && sb.ToString().EndsWith(")*", StringComparison.Ordinal))
                sb.Append('/');
            sb.Append(GlobMatcher.ToRegexText(segment, matchSlash: false));
        }

        return sb.ToString();
    }
}
