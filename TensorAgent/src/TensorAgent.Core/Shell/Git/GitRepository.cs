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
using System.Text;

namespace TensorAgent.Core.Shell.Git;

/// <summary>
/// A repository: where its <c>.git</c> is, what its refs say, and how a revision spelled
/// by a human turns into an object name.
///
/// <para>
/// <b>Discovery</b> walks up from the working directory looking for <c>.git</c>, and
/// stops at the confinement boundary rather than at the filesystem root. That is the
/// whole reason it is written out by hand instead of just probing: without the boundary
/// check, a session whose work root sits inside a checkout of something else would find
/// <em>that</em> repository and start committing to it.
/// </para>
/// <para>
/// <b>Refs</b> are read from loose files under <c>refs/</c> first and then from
/// <c>packed-refs</c>, which is git's precedence and the one that stays correct after
/// <c>git pack-refs</c>. The <c>reftable</c> backend (git 2.45+, opt-in) is detected and
/// refused; it is a completely different on-disk format and pretending to read it would
/// report a repository with no branches.
/// </para>
/// </summary>
internal sealed class GitRepository : IDisposable
{
    /// <summary>
    /// The branch a fresh repository starts on. Git's own built-in default is still
    /// <c>master</c> (it warns and suggests configuring <c>init.defaultBranch</c>);
    /// <c>main</c> is chosen here because nothing reads this constant except HEAD, and
    /// every tool — including the real git binary — follows HEAD rather than assuming.
    /// </summary>
    public const string DefaultBranch = "main";

    private readonly GitFileGate _gate;
    private GitConfig? _config;

    private GitRepository(GitFileGate gate, string gitDir, string workTree)
    {
        _gate = gate;
        GitDir = gitDir;
        WorkTree = workTree;
        Objects = new GitObjectDatabase(gate, Path.Combine(gitDir, "objects"));
    }

    public string GitDir { get; }

    /// <summary>The checkout root. Equal to the parent of <see cref="GitDir"/> for a normal repository.</summary>
    public string WorkTree { get; }

    public GitObjectDatabase Objects { get; }

    public GitFileGate Gate => _gate;

    public GitConfig Config => _config ??= GitConfig.Read(_gate, Path.Combine(GitDir, "config"));

    public string IndexPath => Path.Combine(GitDir, "index");

    // =====================================================================================
    // discovery and creation
    // =====================================================================================

    /// <summary>
    /// Finds the repository containing <paramref name="startDirectory"/>, or null.
    /// Walks up only through directories the session may read, so a <c>.git</c> above the
    /// confinement boundary is invisible rather than merely unusable.
    /// </summary>
    public static GitRepository? Discover(GitFileGate gate, string startDirectory)
    {
        string? current = Path.GetFullPath(startDirectory);
        while (!string.IsNullOrEmpty(current) && gate.CanRead(current))
        {
            string dotGit = Path.Combine(current, ".git");
            if (gate.DirectoryExists(dotGit))
                return Open(gate, gate.Read(dotGit), gate.Read(current));

            if (gate.FileExists(dotGit))
            {
                // A `.git` *file* is the linked-worktree / submodule form: `gitdir: <path>`.
                string text = gate.ReadAllText(dotGit).Trim();
                const string prefix = "gitdir:";
                if (!text.StartsWith(prefix, StringComparison.Ordinal))
                    throw new GitFormatException("invalid gitfile format: " + dotGit);
                string target = text[prefix.Length..].Trim();
                if (!Path.IsPathRooted(target))
                    target = Path.Combine(current, target);

                // Resolved through the gate like anything else: a gitfile pointing outside
                // the session is a confinement failure, not a valid repository.
                return Open(gate, gate.Read(target), gate.Read(current));
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent == current)
                break;
            current = parent;
        }

        return null;
    }

    private static GitRepository Open(GitFileGate gate, string gitDir, string workTree)
    {
        if (gate.DirectoryExists(Path.Combine(gitDir, "reftable")))
        {
            throw new GitFatalException(
                "this repository uses the reftable ref backend, which this shell's built-in git cannot read. "
                + "Only the files backend (refs/ and packed-refs) is supported.");
        }

        var repo = new GitRepository(gate, gitDir, workTree);
        string? format = repo.Config.Get("extensions.objectformat");
        if (format != null && !string.Equals(format, "sha1", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitFatalException(
                "this repository uses the " + format + " object format; this shell's built-in git implements SHA-1 only.");
        }

        return repo;
    }

    /// <summary>
    /// Creates a repository. Writes the minimum real git requires and nothing decorative:
    /// no hook samples (they are examples, and this host cannot run them anyway) and no
    /// <c>description</c> (used only by gitweb).
    /// </summary>
    public static GitRepository Init(GitFileGate gate, string directory, out bool alreadyExisted)
    {
        string gitDir = Path.Combine(directory, ".git");
        alreadyExisted = gate.DirectoryExists(gitDir);

        gate.CreateDirectory(directory);
        gate.CreateDirectory(gitDir);
        gate.CreateDirectory(Path.Combine(gitDir, "objects", "info"));
        gate.CreateDirectory(Path.Combine(gitDir, "objects", "pack"));
        gate.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        gate.CreateDirectory(Path.Combine(gitDir, "refs", "tags"));
        gate.CreateDirectory(Path.Combine(gitDir, "info"));

        if (!alreadyExisted)
        {
            gate.WriteAllTextAtomic(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/" + DefaultBranch + "\n");

            // repositoryformatversion 0 is the plain format every git since 1.7 reads.
            // filemode true because this shell records the executable bit faithfully;
            // saying false would make real git ignore a mode change we did record.
            var config = new StringBuilder();
            config.Append("[core]\n");
            config.Append("\trepositoryformatversion = 0\n");
            config.Append("\tfilemode = true\n");
            config.Append("\tbare = false\n");
            config.Append("\tlogallrefupdates = true\n");
            gate.WriteAllTextAtomic(Path.Combine(gitDir, "config"), config.ToString());
        }

        return Open(gate, gate.Read(gitDir), gate.Read(directory));
    }

    // =====================================================================================
    // refs
    // =====================================================================================

    /// <summary>The ref HEAD points at, or null when HEAD is detached at a raw object name.</summary>
    public string? HeadRefName()
    {
        string headPath = Path.Combine(GitDir, "HEAD");
        if (!_gate.FileExists(headPath))
            return "refs/heads/" + DefaultBranch;
        string text = _gate.ReadAllText(headPath).Trim();
        return text.StartsWith("ref:", StringComparison.Ordinal) ? text[4..].Trim() : null;
    }

    /// <summary>The commit HEAD names, or null when the branch has no commits yet ("unborn").</summary>
    public string? ResolveHead()
    {
        string? refName = HeadRefName();
        if (refName == null)
        {
            string text = _gate.ReadAllText(Path.Combine(GitDir, "HEAD")).Trim();
            return GitObjectId.IsFullId(text) ? text.ToLowerInvariant() : null;
        }

        return ReadRef(refName);
    }

    public string CurrentBranchName()
    {
        string? refName = HeadRefName();
        if (refName == null)
            return "HEAD (detached)";
        const string heads = "refs/heads/";
        return refName.StartsWith(heads, StringComparison.Ordinal) ? refName[heads.Length..] : refName;
    }

    /// <summary>
    /// Resolves a full ref name to an object id, following symbolic refs. Loose files win
    /// over <c>packed-refs</c>: after <c>git pack-refs</c> both exist for a while and the
    /// loose one is the newer.
    /// </summary>
    public string? ReadRef(string name, int depth = 0)
    {
        if (depth > 5)
            throw new GitFatalException("ref " + name + " is a symbolic reference loop");

        string loose = Path.Combine(GitDir, name.Replace('/', Path.DirectorySeparatorChar));
        if (_gate.FileExists(loose))
        {
            string text = _gate.ReadAllText(loose).Trim();
            if (text.StartsWith("ref:", StringComparison.Ordinal))
                return ReadRef(text[4..].Trim(), depth + 1);
            return GitObjectId.IsFullId(text) ? text.ToLowerInvariant() : null;
        }

        return PackedRefs().TryGetValue(name, out string? packed) ? packed : null;
    }

    private Dictionary<string, string>? _packedRefs;

    private Dictionary<string, string> PackedRefs()
    {
        if (_packedRefs != null)
            return _packedRefs;
        _packedRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(GitDir, "packed-refs");
        if (!_gate.FileExists(path))
            return _packedRefs;

        foreach (string line in _gate.ReadAllLines(path))
        {
            // '#' is the capability header; '^' is the peeled value of the tag on the line
            // above, which this reader does not need because it peels tags itself.
            if (line.Length == 0 || line[0] == '#' || line[0] == '^')
                continue;
            int space = line.IndexOf(' ');
            if (space != GitObjectId.HexLength)
                continue;
            string id = line[..space];
            if (GitObjectId.IsFullId(id))
                _packedRefs[line[(space + 1)..].Trim()] = id.ToLowerInvariant();
        }

        return _packedRefs;
    }

    /// <summary>Every ref under <paramref name="prefix"/> (e.g. <c>refs/heads/</c>), loose and packed, sorted.</summary>
    public List<(string Name, string Id)> ListRefs(string prefix)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string id) in PackedRefs())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                refs[name] = id;
        }

        string root = Path.Combine(GitDir, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        if (_gate.DirectoryExists(root))
        {
            string real = _gate.Read(root);
            foreach (string file in Directory.EnumerateFiles(real, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(real, file).Replace(Path.DirectorySeparatorChar, '/');
                string name = prefix.TrimEnd('/') + "/" + relative;
                string? id = ReadRef(name);
                if (id != null)
                    refs[name] = id;
            }
        }

        return refs.Select(kv => (kv.Key, kv.Value)).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    public void UpdateRef(string name, string id)
    {
        _gate.WriteAllTextAtomic(Path.Combine(GitDir, name.Replace('/', Path.DirectorySeparatorChar)), id + "\n");
        _packedRefs = null;
    }

    public void SetHeadTo(string refName)
    {
        _gate.WriteAllTextAtomic(Path.Combine(GitDir, "HEAD"), "ref: " + refName + "\n");
    }

    /// <summary>
    /// Appends to <c>.git/logs/&lt;ref&gt;</c> and <c>.git/logs/HEAD</c>.
    ///
    /// <para>
    /// Nothing here reads reflogs. They are written because the real git binary does read
    /// them, and an agent that used this builtin to make a mess will reach for
    /// <c>git reflog</c> to get out of it — a repository with a history but no reflog
    /// would strand it. The format is one line per update:
    /// <c>old new who &lt;email&gt; time zone TAB message</c>.
    /// </para>
    /// </summary>
    public void AppendReflog(string refName, string? oldId, string newId, GitSignature who, string message)
    {
        if (!Config.GetBool("core.logallrefupdates", true))
            return;
        string line = (oldId ?? GitObjectId.Zero) + " " + newId + " " + who.Format() + "\t" + message + "\n";
        _gate.AppendAllText(Path.Combine(GitDir, "logs", refName.Replace('/', Path.DirectorySeparatorChar)), line);
        if (refName != "HEAD" && string.Equals(HeadRefName(), refName, StringComparison.Ordinal))
            _gate.AppendAllText(Path.Combine(GitDir, "logs", "HEAD"), line);
    }

    // =====================================================================================
    // revisions
    // =====================================================================================

    /// <summary>
    /// Turns a revision as a human writes it into an object name.
    ///
    /// <para>
    /// Supported: <c>HEAD</c> and <c>@</c>, a branch or tag name (searched in git's own
    /// order — the exact ref, then <c>refs/</c>, <c>refs/tags/</c>, <c>refs/heads/</c>,
    /// <c>refs/remotes/</c>), a full or abbreviated object name, and the two ancestry
    /// operators <c>~n</c> (n generations of first parents) and <c>^n</c> (the n-th
    /// parent). Not supported, and refused by name rather than ignored: <c>@{...}</c>
    /// reflog and upstream selectors, <c>:/text</c> message search, and <c>a...b</c>
    /// merge-base ranges.
    /// </para>
    /// </summary>
    public string ResolveRevision(string revision)
    {
        if (revision.Length == 0)
            throw new GitFatalException("ambiguous argument '': unknown revision");
        if (revision.Contains("@{", StringComparison.Ordinal))
            throw new GitFatalException("'" + revision + "': @{...} revision selectors are not implemented by this shell's built-in git");
        if (revision.StartsWith(":/", StringComparison.Ordinal))
            throw new GitFatalException("'" + revision + "': :/text revision search is not implemented by this shell's built-in git");

        // Split the ancestry suffix off the base name, right to left.
        int split = revision.Length;
        for (int i = 0; i < revision.Length; i++)
        {
            if (revision[i] is '~' or '^')
            {
                split = i;
                break;
            }
        }

        string baseName = revision[..split];
        string suffix = revision[split..];
        string id = ResolveName(baseName.Length == 0 ? "HEAD" : baseName, revision);

        int pos = 0;
        while (pos < suffix.Length)
        {
            char op = suffix[pos++];
            int digits = pos;
            while (digits < suffix.Length && char.IsAsciiDigit(suffix[digits]))
                digits++;
            string number = suffix[pos..digits];
            pos = digits;
            int n = number.Length == 0 ? 1 : int.Parse(number, CultureInfo.InvariantCulture);

            if (op == '~')
            {
                for (int i = 0; i < n; i++)
                    id = FirstParent(id, revision);
            }
            else
            {
                if (n == 0)
                    continue;                                   // `rev^0` means "the commit itself"
                GitCommit commit = ReadCommit(PeelToCommit(id, revision));
                if (commit.Parents.Count < n)
                    throw new GitFatalException(revision + ": needs " + n + " parents but has " + commit.Parents.Count);
                id = commit.Parents[n - 1];
            }
        }

        return id;
    }

    private string FirstParent(string id, string revision)
    {
        GitCommit commit = ReadCommit(PeelToCommit(id, revision));
        if (commit.Parents.Count == 0)
            throw new GitFatalException(revision + ": no parent (this is the root commit)");
        return commit.Parents[0];
    }

    private string ResolveName(string name, string original)
    {
        if (name is "HEAD" or "@")
        {
            return ResolveHead()
                ?? throw new GitFatalException("ambiguous argument 'HEAD': unknown revision or path not in the working tree.");
        }

        foreach (string candidate in new[] { name, "refs/" + name, "refs/tags/" + name, "refs/heads/" + name, "refs/remotes/" + name, "refs/remotes/" + name + "/HEAD" })
        {
            string? id = ReadRef(candidate);
            if (id != null)
                return id;
        }

        if (GitObjectId.IsFullId(name) && Objects.Exists(name))
            return name.ToLowerInvariant();

        if (name.Length is >= 4 and < GitObjectId.HexLength && GitObjectId.IsHex(name))
        {
            List<string> matches = Objects.ResolvePrefix(name);
            if (matches.Count == 1)
                return matches[0];
            if (matches.Count > 1)
                throw new GitFatalException("ambiguous argument '" + original + "': " + matches.Count + " objects share this prefix");
        }

        throw new GitFatalException("ambiguous argument '" + original + "': unknown revision or path not in the working tree.");
    }

    /// <summary>Follows annotated tags down to the commit they point at.</summary>
    public string PeelToCommit(string id, string original)
    {
        for (int i = 0; i < 10; i++)
        {
            GitObjectData data = Objects.Read(id);
            if (data.Type == GitObjectType.Commit)
                return id;
            if (data.Type != GitObjectType.Tag)
                throw new GitFatalException(original + ": expected a commit, found a " + GitObjectTypes.Name(data.Type));
            id = GitTag.Parse(data.Payload).Object;
        }

        throw new GitFatalException(original + ": tag chain is too deep");
    }

    public GitCommit ReadCommit(string id) => GitCommit.Parse(id, Objects.Read(id, GitObjectType.Commit).Payload);

    // =====================================================================================
    // trees
    // =====================================================================================

    /// <summary>
    /// Flattens a tree to <c>path -&gt; (mode, id)</c>. Submodule entries (<c>160000</c>)
    /// are kept in the map so that status can report them unchanged rather than as
    /// deletions, but nothing here ever descends into one.
    /// </summary>
    public Dictionary<string, GitTreeEntry> ReadTreeRecursive(string treeId)
    {
        var result = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        Walk(treeId, string.Empty, result, 0);
        return result;
    }

    private void Walk(string treeId, string prefix, Dictionary<string, GitTreeEntry> into, int depth)
    {
        if (depth > 100)
            throw new GitFormatException("tree nesting is deeper than 100 levels");
        foreach (GitTreeEntry entry in GitTree.Parse(Objects.Read(treeId, GitObjectType.Tree).Payload))
        {
            string path = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            if (entry.IsTree)
                Walk(entry.Id, path, into, depth + 1);
            else
                into[path] = entry with { Name = path };
        }
    }

    /// <summary>The tree of a commit, flattened; empty when <paramref name="commitId"/> is null (an unborn branch).</summary>
    public Dictionary<string, GitTreeEntry> ReadCommitTree(string? commitId)
        => commitId == null
            ? new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal)
            : ReadTreeRecursive(ReadCommit(commitId).Tree);

    /// <summary>
    /// Builds tree objects from the index and returns the root's id.
    ///
    /// <para>
    /// The index is a flat, sorted list of full paths; trees are nested. The grouping
    /// below rebuilds the nesting in one pass, writing each subtree before the parent that
    /// names it, because a tree's id depends on the ids of its children. Entries at a
    /// merge stage are refused: committing one would silently record whichever stage
    /// happened to sort first as the resolution.
    /// </para>
    /// </summary>
    public string WriteTreeFromIndex(GitIndex index)
    {
        if (index.HasConflicts)
            throw new GitFatalException("cannot write a tree: the index has unmerged entries", 128);
        return WriteTreeLevel(index.Entries.Where(e => e.Stage == 0).ToList(), string.Empty);
    }

    private string WriteTreeLevel(List<GitIndexEntry> entries, string prefix)
    {
        var here = new List<GitTreeEntry>();
        var subdirectories = new Dictionary<string, List<GitIndexEntry>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (GitIndexEntry entry in entries)
        {
            string relative = prefix.Length == 0 ? entry.Path : entry.Path[(prefix.Length + 1)..];
            int slash = relative.IndexOf('/');
            if (slash < 0)
            {
                here.Add(new GitTreeEntry(entry.Mode, relative, entry.Id));
                continue;
            }

            string directory = relative[..slash];
            if (!subdirectories.TryGetValue(directory, out List<GitIndexEntry>? list))
            {
                subdirectories[directory] = list = new List<GitIndexEntry>();
                order.Add(directory);
            }

            list.Add(entry);
        }

        foreach (string directory in order)
        {
            string childPrefix = prefix.Length == 0 ? directory : prefix + "/" + directory;
            string id = WriteTreeLevel(subdirectories[directory], childPrefix);
            here.Add(new GitTreeEntry(GitFileMode.Tree, directory, id));
        }

        return Objects.Write(GitObjectType.Tree, GitTree.Serialize(here));
    }

    public void Dispose() => Objects.Dispose();
}
