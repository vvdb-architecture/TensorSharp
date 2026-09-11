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
/// One side of a comparison: which paths exist, and how to get the bytes of each.
///
/// <para>
/// Modelling HEAD, the index and the working tree as the same shape is what makes
/// <c>git diff</c>, <c>git diff --cached</c> and <c>git show</c> one code path instead of
/// three. It is also how git itself is organised — <c>diff-tree</c>, <c>diff-index</c>
/// and <c>diff-files</c> all feed one renderer.
/// </para>
/// </summary>
internal sealed class GitDiffSide
{
    private readonly Func<string, GitTreeEntry, byte[]> _read;

    private GitDiffSide(Dictionary<string, GitTreeEntry> entries, Func<string, GitTreeEntry, byte[]> read)
    {
        Entries = entries;
        _read = read;
    }

    public Dictionary<string, GitTreeEntry> Entries { get; }

    public byte[] Read(string path) => _read(path, Entries[path]);

    /// <summary>A commit's tree, or an empty side when <paramref name="commitId"/> is null (no commits yet).</summary>
    public static GitDiffSide FromCommit(GitRepository repo, string? commitId)
        => new(repo.ReadCommitTree(commitId), (_, entry) => repo.Objects.Read(entry.Id, GitObjectType.Blob).Payload);

    public static GitDiffSide FromTree(GitRepository repo, string treeId)
        => new(repo.ReadTreeRecursive(treeId), (_, entry) => repo.Objects.Read(entry.Id, GitObjectType.Blob).Payload);

    public static GitDiffSide FromIndex(GitRepository repo, GitIndex index)
    {
        var entries = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        foreach (GitIndexEntry entry in index.Entries)
        {
            if (entry.Stage == 0)
                entries[entry.Path] = new GitTreeEntry(entry.Mode, entry.Path, entry.Id);
        }

        return new GitDiffSide(entries, (_, entry) => repo.Objects.Read(entry.Id, GitObjectType.Blob).Payload);
    }

    /// <summary>
    /// The working tree, restricted to <paramref name="paths"/> — normally the index's
    /// paths. Untracked files are deliberately absent: <c>git diff</c> does not show them
    /// either, because a diff against nothing is the whole file and would drown the real
    /// change.
    /// </summary>
    public static GitDiffSide FromWorkTree(GitFileGate gate, GitRepository repo, IEnumerable<string> paths)
    {
        var entries = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string absolute = Path.Combine(repo.WorkTree, path.Replace('/', Path.DirectorySeparatorChar));
            if (!GitWorkTree.TryRead(gate, absolute, out GitWorkTree.GitWorkTreeFile file))
                continue;
            contents[path] = file.Content;
            entries[path] = new GitTreeEntry(file.Mode, path, GitObjectId.Compute(GitObjectType.Blob, file.Content));
        }

        return new GitDiffSide(entries, (path, _) => contents[path]);
    }
}

/// <summary>
/// Renders unified diffs in git's format, on top of the shell's own
/// <see cref="DiffAlgorithm"/>.
///
/// <para>
/// The hunk arithmetic is shared with the <c>diff</c> builtin rather than written twice;
/// what git adds on top is the envelope — the <c>diff --git</c> line, the abbreviated
/// blob ids on the <c>index</c> line, <c>new file mode</c> / <c>deleted file mode</c>,
/// <c>/dev/null</c> in place of the missing side, and the
/// <c>\ No newline at end of file</c> marker. Those are what make the output something
/// <c>git apply</c> and <c>patch</c> can consume, so they are produced faithfully rather
/// than approximated.
/// </para>
/// <para>
/// One honest caveat: git's Myers implementation and this LCS walk can choose different
/// — both correct — edit scripts for the same change, so hunk boundaries may differ from
/// what the real binary prints. The diffs apply; they are not always byte-identical.
/// </para>
/// </summary>
internal static class GitDiff
{
    public sealed record Result(string Text, int FilesChanged, int Insertions, int Deletions);

    public static Result Render(GitDiffSide a, GitDiffSide b, Func<string, bool>? include, int context = 3, bool nameOnly = false, bool statOnly = false)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in a.Entries.Keys)
        {
            if (include == null || include(path))
                paths.Add(path);
        }

        foreach (string path in b.Entries.Keys)
        {
            if (include == null || include(path))
                paths.Add(path);
        }

        var sb = new StringBuilder();
        var stat = new List<(string Path, int Added, int Removed, bool Binary)>();
        int files = 0;
        int insertions = 0;
        int deletions = 0;

        foreach (string path in paths)
        {
            bool inA = a.Entries.TryGetValue(path, out GitTreeEntry entryA);
            bool inB = b.Entries.TryGetValue(path, out GitTreeEntry entryB);
            if (inA && inB && entryA.Id == entryB.Id && entryA.Mode == entryB.Mode)
                continue;

            // A submodule's recorded commit is not a blob and cannot be inflated; report
            // the id change rather than trying to read it.
            if ((inA && entryA.Mode == GitFileMode.GitLink) || (inB && entryB.Mode == GitFileMode.GitLink))
            {
                files++;
                sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
                sb.Append("Subproject commit ").Append(inB ? entryB.Id : GitObjectId.Zero).Append('\n');
                stat.Add((path, 0, 0, true));
                continue;
            }

            byte[] contentA = inA ? a.Read(path) : Array.Empty<byte>();
            byte[] contentB = inB ? b.Read(path) : Array.Empty<byte>();
            files++;

            if (nameOnly)
            {
                sb.Append(path).Append('\n');
                continue;
            }

            sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
            if (!inA)
                sb.Append("new file mode ").Append(GitFileMode.FormatPadded(entryB.Mode)).Append('\n');
            else if (!inB)
                sb.Append("deleted file mode ").Append(GitFileMode.FormatPadded(entryA.Mode)).Append('\n');
            else if (entryA.Mode != entryB.Mode)
            {
                sb.Append("old mode ").Append(GitFileMode.FormatPadded(entryA.Mode)).Append('\n');
                sb.Append("new mode ").Append(GitFileMode.FormatPadded(entryB.Mode)).Append('\n');
            }

            string idA = inA ? GitObjectId.Abbreviate(entryA.Id) : "0000000";
            string idB = inB ? GitObjectId.Abbreviate(entryB.Id) : "0000000";
            if (inA && inB && entryA.Id == entryB.Id)
            {
                // Mode-only change: git prints the header and stops.
                stat.Add((path, 0, 0, false));
                continue;
            }

            sb.Append("index ").Append(idA).Append("..").Append(idB);
            if (inA && inB && entryA.Mode == entryB.Mode)
                sb.Append(' ').Append(GitFileMode.FormatPadded(entryA.Mode));
            sb.Append('\n');

            if (GitWorkTree.LooksBinary(contentA) || GitWorkTree.LooksBinary(contentB))
            {
                sb.Append("Binary files ").Append(inA ? "a/" + path : "/dev/null")
                  .Append(" and ").Append(inB ? "b/" + path : "/dev/null").Append(" differ\n");
                stat.Add((path, 0, 0, true));
                continue;
            }

            (List<string> linesA, bool endsA) = GitWorkTree.SplitLines(contentA);
            (List<string> linesB, bool endsB) = GitWorkTree.SplitLines(contentB);
            string body = DiffAlgorithm.Unified(linesA, linesB, inA ? "a/" + path : "/dev/null", inB ? "b/" + path : "/dev/null", context);
            body = AddNoNewlineMarkers(body, linesA, endsA, linesB, endsB);

            int added = 0;
            int removed = 0;
            foreach (string line in ShellText.Lines(body))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith('+'))
                    added++;
                else if (line.StartsWith('-'))
                    removed++;
            }

            insertions += added;
            deletions += removed;
            stat.Add((path, added, removed, false));
            sb.Append(body);
        }

        if (statOnly)
            return new Result(FormatStat(stat, insertions, deletions), files, insertions, deletions);
        return new Result(sb.ToString(), files, insertions, deletions);
    }

    /// <summary>
    /// Appends <c>\ No newline at end of file</c> after the last output line belonging to
    /// a side whose content did not end with one.
    ///
    /// <para>
    /// This is not decoration. Without the marker the patch says the file ends in a
    /// newline, so applying it adds one — a one-byte corruption that shows up later as a
    /// spurious diff. When both sides lack the terminator and share their final line, the
    /// marker is emitted once, exactly as git does.
    /// </para>
    /// </summary>
    private static string AddNoNewlineMarkers(string body, List<string> linesA, bool endsA, List<string> linesB, bool endsB)
    {
        if (body.Length == 0 || (endsA && endsB))
            return body;

        const string marker = "\\ No newline at end of file";
        List<string> lines = ShellText.Lines(body);
        int markA = -1;
        int markB = -1;
        if (!endsA && linesA.Count > 0)
            markA = LastIndexOf(lines, '-', linesA[^1]);
        if (!endsB && linesB.Count > 0)
            markB = LastIndexOf(lines, '+', linesB[^1]);

        var inserts = new SortedSet<int>();
        if (markA >= 0)
            inserts.Add(markA);
        if (markB >= 0)
            inserts.Add(markB);
        if (inserts.Count == 0)
            return body;

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]).Append('\n');
            if (inserts.Contains(i))
                sb.Append(marker).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The last line carrying <paramref name="sign"/> or a context space whose payload is <paramref name="text"/>.</summary>
    private static int LastIndexOf(List<string> lines, char sign, string text)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i];
            if (line.Length == 0)
                continue;
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                continue;
            if ((line[0] == sign || line[0] == ' ') && string.Equals(line[1..], text, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string FormatStat(List<(string Path, int Added, int Removed, bool Binary)> stat, int insertions, int deletions)
    {
        if (stat.Count == 0)
            return string.Empty;
        var sb = new StringBuilder();
        int width = stat.Max(s => s.Path.Length);
        foreach ((string path, int added, int removed, bool binary) in stat)
        {
            sb.Append(' ').Append(path.PadRight(width)).Append(" | ");
            if (binary)
            {
                sb.Append("Bin\n");
                continue;
            }

            sb.Append((added + removed).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            sb.Append('+', added).Append('-', removed).Append('\n');
        }

        sb.Append(GitWorkTree.FormatDiffStat(stat.Count, insertions, deletions)).Append('\n');
        return sb.ToString();
    }
}
