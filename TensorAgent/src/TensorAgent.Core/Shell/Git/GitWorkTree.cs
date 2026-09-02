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
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell.Git;

/// <summary>What a path is doing, in the vocabulary <c>git status</c> prints.</summary>
internal enum GitChange
{
    None,
    Added,
    Modified,
    Deleted,
    TypeChanged,
    Unmerged,
}

/// <summary>One path's two statuses: what is staged, and what is not.</summary>
internal sealed record GitStatusEntry(string Path, GitChange Staged, GitChange Unstaged);

/// <summary>A snapshot of the three-way comparison HEAD ↔ index ↔ working tree.</summary>
internal sealed class GitStatusResult
{
    public required List<GitStatusEntry> Changes { get; init; }

    public required List<string> Untracked { get; init; }

    public required string Branch { get; init; }

    /// <summary>True before the first commit, when HEAD names a branch that does not exist yet.</summary>
    public required bool Unborn { get; init; }

    public required bool Detached { get; init; }
}

/// <summary>
/// The working tree: reading files as git would hash them, listing them the way
/// <c>status</c> needs, and the three-way comparison that everything else is built on.
/// </summary>
internal static class GitWorkTree
{
    /// <summary>What the working tree holds at one path, as git would record it.</summary>
    internal readonly record struct GitWorkTreeFile(int Mode, byte[] Content, string RealPath);

    /// <summary>
    /// Reads a working-tree path the way git stages it, never following a symlink.
    ///
    /// <para>
    /// A symlink hashes as a blob whose content is the link <em>target text</em>, and mode
    /// <c>120000</c>. Getting this wrong is not cosmetic: dereferencing would copy the
    /// target's bytes into a blob under the link's name, so a link to a file elsewhere in
    /// the session would be silently inlined into the repository and a checkout would
    /// replace the link with a copy. The path is confined through
    /// <see cref="GitFileGate.ResolveLeaf"/>, which validates the containing directory
    /// without opening the final component.
    /// </para>
    /// <para>
    /// No CRLF translation is performed. Git's <c>core.autocrlf</c> would rewrite line
    /// endings between the working tree and the blob; this shell always stores the bytes
    /// on disk. On the platforms it runs on the setting is off by default, and honouring
    /// it halfway — converting on add but not on checkout — is how a file ends up
    /// permanently reported as modified.
    /// </para>
    /// </summary>
    public static bool TryRead(GitFileGate gate, string absolutePath, out GitWorkTreeFile file)
    {
        file = default;
        if (!gate.TryResolveLeaf(absolutePath, PathAccess.Read, out string leaf))
            return false;

        var info = new FileInfo(leaf);
        if (info.LinkTarget != null)
        {
            file = new GitWorkTreeFile(GitFileMode.Symlink, ShellText.Utf8.GetBytes(info.LinkTarget), leaf);
            return true;
        }

        if (!info.Exists)
            return false;
        file = new GitWorkTreeFile(ModeOf(leaf), File.ReadAllBytes(leaf), leaf);
        return true;
    }

    /// <summary>Whether anything at all sits at the path — a file or a link, dangling or not.</summary>
    public static bool Exists(GitFileGate gate, string absolutePath)
    {
        if (!gate.TryResolveLeaf(absolutePath, PathAccess.Read, out string leaf))
            return false;
        var info = new FileInfo(leaf);
        return info.Exists || info.LinkTarget != null;
    }

    /// <summary>
    /// The mode git would record for a path that is known not to be a link. Only the
    /// executable bit is preserved, because that is the only permission bit the format
    /// has room for.
    /// </summary>
    public static int ModeOf(string realPath)
    {
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(realPath);
            bool executable = (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            return executable ? GitFileMode.Executable : GitFileMode.Regular;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // A filesystem with no permission bits at all: everything is a plain file.
            return GitFileMode.Regular;
        }
    }

    /// <summary>Fills an index entry's cached stat data from the file on disk.</summary>
    public static void StampStat(GitIndexEntry entry, string realPath, long contentLength)
    {
        var info = new FileInfo(realPath);
        entry.Size = contentLength;
        DateTimeOffset mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        entry.MTimeSeconds = (uint)Math.Max(0, mtime.ToUnixTimeSeconds());
        entry.MTimeNanoseconds = (uint)(mtime.UtcDateTime.Ticks % TimeSpan.TicksPerSecond * 100);
        DateTimeOffset ctime = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
        entry.CTimeSeconds = (uint)Math.Max(0, ctime.ToUnixTimeSeconds());
        entry.CTimeNanoseconds = (uint)(ctime.UtcDateTime.Ticks % TimeSpan.TicksPerSecond * 100);
    }

    /// <summary>
    /// Every file in the working tree, as work-tree-relative slash paths, excluding
    /// <c>.git</c> and anything <c>.gitignore</c> excludes.
    ///
    /// <para>
    /// Two directories are stepped over rather than walked. An ignored directory is not
    /// descended into at all, which is what makes <c>status</c> fast in a workspace with a
    /// <c>node_modules</c>. And a subdirectory that contains its own <c>.git</c> is a
    /// nested repository or a submodule: its files belong to it, and listing them here
    /// would produce thousands of untracked paths that <c>git add .</c> must not stage.
    /// </para>
    /// <para>
    /// A null <paramref name="ignore"/> lists everything, which is what <c>git add -f</c>
    /// asks for: the user has said the exclusion does not apply this time, and filtering
    /// anyway would make the pathspec appear to match nothing.
    /// </para>
    /// </summary>
    public static List<string> ListFiles(GitFileGate gate, GitRepository repo, GitIgnore? ignore, CancellationToken cancellation)
    {
        var files = new List<string>();
        Walk(gate, repo, ignore, string.Empty, files, 0, cancellation);
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void Walk(GitFileGate gate, GitRepository repo, GitIgnore? ignore, string relative, List<string> into, int depth, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > 64)
            return;

        string absolute = relative.Length == 0
            ? repo.WorkTree
            : Path.Combine(repo.WorkTree, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!gate.TryRead(absolute, out string real) || !Directory.Exists(real))
            return;

        ignore?.Push(gate, repo.WorkTree, relative);
        try
        {
            var entries = new List<string>(Directory.EnumerateFileSystemEntries(real));
            entries.Sort(StringComparer.Ordinal);
            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (name == ".git")
                    continue;
                string child = relative.Length == 0 ? name : relative + "/" + name;

                var info = new FileInfo(entry);
                bool isDirectory = info.LinkTarget == null && Directory.Exists(entry);
                if (ignore != null && ignore.IsIgnored(child, isDirectory))
                    continue;

                if (isDirectory)
                {
                    if (Directory.Exists(Path.Combine(entry, ".git")) || File.Exists(Path.Combine(entry, ".git")))
                        continue;                                 // a nested repository is not ours to list
                    Walk(gate, repo, ignore, child, into, depth + 1, cancellation);
                }
                else
                {
                    into.Add(child);
                }
            }
        }
        finally
        {
            ignore?.Pop();
        }
    }

    /// <summary>
    /// Compares HEAD, the index, and the working tree.
    ///
    /// <para>
    /// <b>Why the working-tree comparison always hashes.</b> Git decides a file is
    /// unchanged when its cached stat data still matches, and only hashes when it does
    /// not — then spends a page of code on the "racy git" problem, where a file written in
    /// the same second the index was written looks unchanged forever. This implementation
    /// takes the simpler and strictly safer branch: a size difference is a change without
    /// reading anything, and otherwise the file is hashed. Workspaces here are scratch
    /// trees of source files, so the cost is small, and the failure it removes — reporting
    /// a modified file as clean, so an agent commits without it — is the expensive one.
    /// </para>
    /// </summary>
    public static GitStatusResult Compute(GitFileGate gate, GitRepository repo, GitIndex index, CancellationToken cancellation)
    {
        string? head = repo.ResolveHead();
        Dictionary<string, GitTreeEntry> headTree = repo.ReadCommitTree(head);

        var changes = new Dictionary<string, (GitChange Staged, GitChange Unstaged)>(StringComparer.Ordinal);

        void Record(string path, GitChange? staged = null, GitChange? unstaged = null)
        {
            changes.TryGetValue(path, out (GitChange Staged, GitChange Unstaged) current);
            changes[path] = (staged ?? current.Staged, unstaged ?? current.Unstaged);
        }

        // --- HEAD vs index: what a commit would record ---------------------------------
        var staged = new HashSet<string>(StringComparer.Ordinal);
        foreach (GitIndexEntry entry in index.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Stage != 0)
            {
                Record(entry.Path, GitChange.Unmerged, GitChange.Unmerged);
                staged.Add(entry.Path);
                continue;
            }

            staged.Add(entry.Path);
            if (!headTree.TryGetValue(entry.Path, out GitTreeEntry inHead))
                Record(entry.Path, staged: GitChange.Added);
            else if (inHead.Id != entry.Id || inHead.Mode != entry.Mode)
                Record(entry.Path, staged: GitChange.Modified);
        }

        foreach (string path in headTree.Keys)
        {
            if (!staged.Contains(path))
                Record(path, staged: GitChange.Deleted);
        }

        // --- index vs working tree: what `git add` would pick up ------------------------
        var ignore = GitIgnore.Create(gate, repo.GitDir, repo.WorkTree);
        List<string> worktree = ListFiles(gate, repo, ignore, cancellation);

        foreach (GitIndexEntry entry in index.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Stage != 0 || entry.Mode == GitFileMode.GitLink)
                continue;
            string absolute = Path.Combine(repo.WorkTree, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!TryRead(gate, absolute, out GitWorkTreeFile file))
            {
                Record(entry.Path, unstaged: GitChange.Deleted);
                continue;
            }

            if (file.Mode != entry.Mode)
            {
                Record(entry.Path, unstaged: GitFileMode.IsBlob(file.Mode) && GitFileMode.IsBlob(entry.Mode) ? GitChange.Modified : GitChange.TypeChanged);
                continue;
            }

            if (file.Content.LongLength != entry.Size || GitObjectId.Compute(GitObjectType.Blob, file.Content) != entry.Id)
                Record(entry.Path, unstaged: GitChange.Modified);
        }

        // --- what is in neither ---------------------------------------------------------
        var untracked = new List<string>();
        foreach (string path in worktree)
        {
            if (!staged.Contains(path))
                untracked.Add(path);
        }

        string? headRef = repo.HeadRefName();
        var result = new GitStatusResult
        {
            Changes = changes
                .Where(c => c.Value.Staged != GitChange.None || c.Value.Unstaged != GitChange.None)
                .Select(c => new GitStatusEntry(c.Key, c.Value.Staged, c.Value.Unstaged))
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ToList(),
            Untracked = untracked,
            Branch = repo.CurrentBranchName(),
            Unborn = head == null && headRef != null,
            Detached = headRef == null,
        };
        return result;
    }

    /// <summary>The two-letter porcelain v1 code, e.g. <c>M </c>, <c> M</c>, <c>A </c>, <c>??</c>.</summary>
    public static string PorcelainCode(GitChange staged, GitChange unstaged)
    {
        if (staged == GitChange.Unmerged || unstaged == GitChange.Unmerged)
            return "UU";
        return string.Concat(Letter(staged), Letter(unstaged));
    }

    private static string Letter(GitChange change) => change switch
    {
        GitChange.Added => "A",
        GitChange.Modified => "M",
        GitChange.Deleted => "D",
        GitChange.TypeChanged => "T",
        GitChange.Unmerged => "U",
        _ => " ",
    };

    /// <summary>The long-form label git prints beside a path under "Changes to be committed".</summary>
    public static string HumanLabel(GitChange change) => change switch
    {
        GitChange.Added => "new file:   ",
        GitChange.Modified => "modified:   ",
        GitChange.Deleted => "deleted:    ",
        GitChange.TypeChanged => "typechange: ",
        GitChange.Unmerged => "both modified:   ",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether content should be treated as binary. Git's own test: a NUL byte in the
    /// first 8000 bytes. Used to print <c>Binary files ... differ</c> instead of pouring
    /// a compiled artefact into the agent's context.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> content) => ShellText.LooksBinary(content);

    /// <summary>Splits blob content into lines for the diff, remembering whether the last one was terminated.</summary>
    public static (List<string> Lines, bool EndsWithNewline) SplitLines(byte[] content)
    {
        string text = ShellText.Utf8.GetString(content);
        bool ends = text.Length == 0 || text.EndsWith('\n');
        return (ShellText.Lines(text), ends);
    }

    /// <summary>
    /// Writes a blob into the working tree, creating parents and applying the executable
    /// bit or recreating the link.
    ///
    /// <para>
    /// The destination is resolved leaf-unfollowed for the same reason reads are: if the
    /// path currently holds a symlink, resolving would write <em>through</em> it and
    /// modify the target instead of the file that was asked for — so
    /// <c>git checkout -- link.txt</c> would corrupt whatever the link points at. The link
    /// is deleted and replaced instead.
    /// </para>
    /// </summary>
    public static void Checkout(GitFileGate gate, GitRepository repo, string relativePath, int mode, byte[] content)
    {
        string absolute = Path.Combine(repo.WorkTree, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? parent = Path.GetDirectoryName(Path.GetFullPath(absolute));
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(gate.Write(parent));
        string real = gate.ResolveLeaf(absolute, PathAccess.Write);

        if (mode == GitFileMode.Symlink)
        {
            var existing = new FileInfo(real);
            if (existing.Exists || existing.LinkTarget != null)
                File.Delete(real);
            File.CreateSymbolicLink(real, ShellText.Utf8.GetString(content));
            return;
        }

        if (new FileInfo(real).LinkTarget != null)
            File.Delete(real);
        File.WriteAllBytes(real, content);
        try
        {
            File.SetUnixFileMode(
                real,
                mode == GitFileMode.Executable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // No permission bits on this filesystem; the content is what matters.
        }
    }

    /// <summary>The <c>N files changed, N insertions(+), N deletions(-)</c> line, in git's exact pluralisation.</summary>
    public static string FormatDiffStat(int files, int insertions, int deletions)
    {
        var sb = new StringBuilder();
        sb.Append(' ').Append(files).Append(files == 1 ? " file changed" : " files changed");
        if (insertions > 0)
            sb.Append(", ").Append(insertions).Append(insertions == 1 ? " insertion(+)" : " insertions(+)");
        if (deletions > 0)
            sb.Append(", ").Append(deletions).Append(deletions == 1 ? " deletion(-)" : " deletions(-)");
        return sb.ToString();
    }
}
