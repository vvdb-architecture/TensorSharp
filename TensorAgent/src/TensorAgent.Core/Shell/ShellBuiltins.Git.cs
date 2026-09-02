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
using System.Text.RegularExpressions;
using TensorAgent.Core.Sandbox;
using TensorAgent.Core.Shell.Git;

namespace TensorAgent.Core.Shell;

/// <summary>
/// <c>git</c>, implemented against the on-disk format rather than against a binary.
///
/// <para>
/// <b>Why it exists.</b> A coding agent working in this shell needs three things that
/// nothing else here provides: to see what it changed (<c>diff</c>, <c>status</c>), to
/// undo a bad edit (<c>checkout --</c>), and to checkpoint before trying something
/// (<c>commit</c>). Without them the only recovery from a botched edit is to remember
/// the original text, which models do badly.
/// </para>
/// <para>
/// <b>Why it is written by hand.</b> There is no <c>git</c> to shell out to — iOS
/// forbids <see cref="System.Diagnostics.Process"/>, which is the reason this whole
/// shell exists — and LibGit2Sharp needs a native <c>libgit2</c> that would have to be
/// cross-compiled and statically linked into the app. So the object database, the index,
/// the refs and the packfile reader are C#, in <c>Shell/Git/</c>, and the repositories
/// this produces are ordinary git repositories that the real binary reads and validates.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> Everything that needs the network:
/// <c>clone</c>, <c>fetch</c>, <c>push</c>, <c>pull</c>, <c>remote</c>,
/// <c>ls-remote</c>, <c>submodule</c>. They are refused by name with the reason, not
/// stubbed — the smart HTTP protocol is a large piece of work whose only purpose here
/// would be to reach a network this session may not have, and the workspace is local
/// scratch rather than a checkout of something upstream. Also absent: merging,
/// rebasing, cherry-picking, stashing and branch switching, all of which rewrite the
/// working tree and none of which the three use cases above need.
/// </para>
/// <para>
/// <b>The rule that shapes the error handling.</b> No subcommand and no flag is ever a
/// silent no-op. An unrecognised flag exits 129 with git's own <c>error: unknown
/// option</c>; a real git subcommand that is not implemented exits 128 saying so by
/// name; a <c>commit</c> that would record nothing exits 1. The failure mode being
/// designed out is an agent that runs <c>git commit</c>, sees status 0, and tells a user
/// its work is saved when nothing was written.
/// </para>
/// </summary>
internal static partial class ShellBuiltins
{
    /// <summary>
    /// The identity used when neither the environment nor <c>.git/config</c> names one.
    ///
    /// <para>
    /// Real git refuses to commit without an identity and tells the user to run
    /// <c>git config user.email</c>. That is right for a person and wrong here: the
    /// global config lives outside the session's confinement, so there is nothing for the
    /// agent to configure and the refusal would be a dead end. Committing under an
    /// obviously synthetic name is the better failure — it is visible in <c>git log</c>
    /// and nobody mistakes it for a person.
    /// </para>
    /// </summary>
    private const string DefaultGitAuthorName = "TensorAgent";

    private const string DefaultGitAuthorEmail = "tensoragent@localhost";

    /// <summary>Subcommands this builtin implements, for the "not implemented" message to contrast against.</summary>
    private static readonly string[] ImplementedGitCommands =
    {
        "add", "branch", "cat-file", "checkout", "commit", "config", "diff", "hash-object",
        "init", "log", "ls-files", "mv", "restore", "rev-parse", "rm", "show", "status", "tag",
    };

    /// <summary>
    /// Real git subcommands that need the network. Listed separately from the merely
    /// unimplemented so the refusal can say <i>why</i> rather than just "no".
    /// </summary>
    private static readonly string[] NetworkGitCommands =
    {
        "clone", "fetch", "pull", "push", "remote", "ls-remote", "submodule", "request-pull", "send-pack",
        "fetch-pack", "receive-pack", "upload-pack", "http-fetch", "http-push", "daemon", "credential",
    };

    /// <summary>
    /// Names the real binary answers to. A name in here that is not implemented gets
    /// "not implemented by this shell's built-in git"; anything else gets git's own
    /// "is not a git command", because telling a model that <c>stauts</c> is unimplemented
    /// would send it looking for a flag instead of fixing the typo.
    /// </summary>
    private static readonly HashSet<string> KnownGitCommands = new(StringComparer.Ordinal)
    {
        "add", "am", "annotate", "apply", "archive", "bisect", "blame", "branch", "bundle", "cat-file",
        "check-attr", "check-ignore", "checkout", "checkout-index", "cherry", "cherry-pick", "clean",
        "clone", "commit", "commit-tree", "config", "count-objects", "credential", "daemon", "describe",
        "diff", "diff-files", "diff-index", "diff-tree", "difftool", "fast-export", "fast-import", "fetch",
        "fetch-pack", "filter-branch", "for-each-ref", "format-patch", "fsck", "gc", "grep", "hash-object",
        "help", "http-fetch", "http-push", "index-pack", "init", "instaweb", "log", "ls-files", "ls-remote",
        "ls-tree", "merge", "merge-base", "merge-file", "mergetool", "mktag", "mktree", "mv", "name-rev",
        "notes", "pack-objects", "pack-refs", "prune", "pull", "push", "range-diff", "read-tree", "rebase",
        "reflog", "remote", "repack", "replace", "request-pull", "rerere", "reset", "restore", "rev-list",
        "rev-parse", "revert", "rm", "send-email", "send-pack", "shortlog", "show", "show-branch", "show-ref",
        "sparse-checkout", "stash", "status", "stripspace", "submodule", "switch", "symbolic-ref", "tag",
        "unpack-objects", "update-index", "update-ref", "upload-pack", "verify-pack", "whatchanged", "worktree",
        "write-tree",
    };

    private static int GitCommand(ShellExec exec, string[] argv, ShellStreams io)
    {
        try
        {
            return GitDispatch(exec, argv, io);
        }
        catch (GitFatalException ex)
        {
            io.Error(ex.Prefix, ex.Message);
            return ex.Code;
        }
        catch (GitFormatException ex)
        {
            io.Error("fatal", ex.Message);
            return 128;
        }
        catch (ConfinementException ex)
        {
            io.Error("fatal", ex.Message);
            return 128;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io.Error("fatal", ex.Message);
            return 128;
        }
    }

    private static int GitDispatch(ShellExec exec, string[] argv, ShellStreams io)
    {
        // --- global options, which come before the subcommand ---------------------------
        string cwd = exec.State.Cwd;
        var oneShotConfig = new List<string>();
        int i = 1;
        for (; i < argv.Length; i++)
        {
            string arg = argv[i];
            if (arg.Length == 0 || arg[0] != '-')
                break;

            switch (arg)
            {
                case "--version":
                    io.Out.WriteLine("git version 0.1.0 (TensorAgent built-in; a subset, no network)");
                    return 0;
                case "--help" or "-h":
                    GitUsage(io.Out);
                    return 0;
                case "--no-pager" or "-P" or "--literal-pathspecs" or "--no-optional-locks" or "--bare-hint":
                    continue;                                       // nothing here pages or locks
                case "-C":
                    if (++i >= argv.Length)
                        throw new GitFatalException("no directory given for -C", 129, "error");
                    cwd = Path.IsPathRooted(argv[i]) ? argv[i] : Path.Combine(cwd, argv[i]);
                    continue;
                case "-c":
                    if (++i >= argv.Length)
                        throw new GitFatalException("no config key given for -c", 129, "error");
                    oneShotConfig.Add(argv[i]);
                    continue;
            }

            if (arg.StartsWith("--git-dir", StringComparison.Ordinal) || arg.StartsWith("--work-tree", StringComparison.Ordinal))
            {
                throw new GitFatalException(
                    arg.Split('=')[0] + " is not implemented by this shell's built-in git; use -C to choose a directory instead");
            }

            if (arg.StartsWith("--exec-path", StringComparison.Ordinal) || arg == "--html-path" || arg == "--man-path" || arg == "--info-path")
                throw new GitFatalException(arg + " is meaningless here: there are no git subprocesses on this host", 129, "error");

            throw new GitFatalException("unknown option `" + arg.TrimStart('-') + "'", 129, "error");
        }

        if (i >= argv.Length)
        {
            GitUsage(io.Err);
            return 1;
        }

        string command = argv[i];
        var rest = new List<string>();
        for (int j = i + 1; j < argv.Length; j++)
            rest.Add(argv[j]);

        // `-C` may name a directory that does not exist yet only for `init`; anything else
        // has to be able to read it, and the resolve is what enforces confinement.
        if (command != "init" || rest.Count == 0)
            cwd = exec.Paths.Resolve(cwd, exec.State.Cwd, PathAccess.Read);

        var gate = new GitFileGate(exec.Paths, cwd);

        if (Array.IndexOf(NetworkGitCommands, command) >= 0)
        {
            throw new GitFatalException(
                "'" + command + "' is not supported by this shell's built-in git: it needs the network, and this host has no "
                + "git transport (no ssh, no smart HTTP, no subprocesses). This workspace is local scratch, not a checkout of a remote.");
        }

        switch (command)
        {
            case "init": return GitInit(exec, gate, cwd, rest, io);
            case "add": return GitAdd(exec, gate, cwd, rest, io);
            case "commit": return GitCommitCommand(exec, gate, cwd, rest, oneShotConfig, io);
            case "status": return GitStatus(exec, gate, cwd, rest, io);
            case "diff": return GitDiffCommand(exec, gate, cwd, rest, io);
            case "log": return GitLog(exec, gate, cwd, rest, io);
            case "show": return GitShow(exec, gate, cwd, rest, io);
            case "checkout": return GitCheckout(exec, gate, cwd, rest, io);
            case "restore": return GitRestore(exec, gate, cwd, rest, io);
            case "rm": return GitRm(exec, gate, cwd, rest, io);
            case "mv": return GitMv(exec, gate, cwd, rest, io);
            case "branch": return GitBranch(exec, gate, cwd, rest, io);
            case "tag": return GitTagCommand(exec, gate, cwd, rest, io);
            case "config": return GitConfigCommand(exec, gate, cwd, rest, io);
            case "rev-parse": return GitRevParse(exec, gate, cwd, rest, io);
            case "cat-file": return GitCatFile(exec, gate, cwd, rest, io);
            case "hash-object": return GitHashObject(exec, gate, cwd, rest, io);
            case "ls-files": return GitLsFiles(exec, gate, cwd, rest, io);
            case "help": GitUsage(io.Out); return 0;
        }

        if (KnownGitCommands.Contains(command))
        {
            throw new GitFatalException(
                "'" + command + "' is a git command, but it is not implemented by this shell's built-in git. Implemented: "
                + string.Join(", ", ImplementedGitCommands) + ".");
        }

        throw new GitFatalException("'" + command + "' is not a git command. See 'git --help'.", 1, "git");
    }

    private static void GitUsage(ShellWriter writer)
    {
        writer.WriteLine("usage: git [-C <path>] [-c <name>=<value>] <command> [<args>]");
        writer.WriteLine(string.Empty);
        writer.WriteLine("This is TensorAgent's built-in git: a pure-C# subset that reads and writes real");
        writer.WriteLine("repositories (loose objects, packfiles, the index, refs) with no network support.");
        writer.WriteLine(string.Empty);
        writer.WriteLine("Implemented: " + string.Join(", ", ImplementedGitCommands));
        writer.WriteLine("Refused (need the network): " + string.Join(", ", NetworkGitCommands.Take(7)));
    }

    // =====================================================================================
    // option parsing
    // =====================================================================================

    /// <summary>
    /// Git-style option parsing with a declared vocabulary.
    ///
    /// <para>
    /// The important property is that the vocabulary is <em>closed</em>: anything not
    /// declared is an error, not an operand and not a shrug. Accepting an unknown flag
    /// silently is how <c>git commit --amend</c> becomes a second commit, or
    /// <c>git add --dry-run</c> actually stages; declaring each flag per subcommand makes
    /// that impossible by construction.
    /// </para>
    /// </summary>
    private sealed class GitOpts
    {
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

        /// <param name="values">
        /// Value-taking options, space separated. A name suffixed with <c>?</c> takes a
        /// value only in the <c>--name=value</c> form and is a plain flag on its own —
        /// which is what <c>--porcelain</c>, <c>--short</c> and <c>--color</c> actually
        /// are in git, and declaring them as ordinary value options makes
        /// <c>git status --porcelain</c> demand an argument it never had.
        /// </param>
        public GitOpts(string command, IReadOnlyList<string> args, string flags, string values, string usage)
        {
            var flagNames = new HashSet<string>(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var valueNames = new HashSet<string>(StringComparer.Ordinal);
            var optionalValueNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string declared in values.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (declared.EndsWith('?'))
                {
                    valueNames.Add(declared[..^1]);
                    optionalValueNames.Add(declared[..^1]);
                }
                else
                {
                    valueNames.Add(declared);
                }
            }

            Usage = usage;
            Command = command;

            bool noMore = false;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (noMore || arg.Length == 0 || arg[0] != '-' || arg == "-")
                {
                    Operands.Add(arg);
                    continue;
                }

                if (arg == "--")
                {
                    noMore = true;
                    SawDoubleDash = true;
                    DoubleDashAt = Operands.Count;
                    continue;
                }

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = arg[2..];
                    int eq = name.IndexOf('=');
                    string? inline = null;
                    if (eq >= 0)
                    {
                        inline = name[(eq + 1)..];
                        name = name[..eq];
                    }

                    if (valueNames.Contains(name))
                    {
                        if (inline == null && optionalValueNames.Contains(name))
                        {
                            _flags.Add(name);
                            continue;
                        }

                        if (inline == null)
                        {
                            if (++i >= args.Count)
                                throw Fail("option `" + name + "' requires a value");
                            inline = args[i];
                        }

                        Add(name, inline);
                        continue;
                    }

                    if (flagNames.Contains(name))
                    {
                        if (inline != null)
                            throw Fail("option `" + name + "' takes no value");
                        _flags.Add(name);
                        continue;
                    }

                    throw Fail("unknown option `" + name + "'");
                }

                for (int c = 1; c < arg.Length; c++)
                {
                    string name = arg[c].ToString();
                    if (valueNames.Contains(name))
                    {
                        string value = c + 1 < arg.Length
                            ? arg[(c + 1)..]
                            : ++i < args.Count ? args[i] : throw Fail("switch `" + name + "' requires a value");
                        Add(name, value);
                        break;
                    }

                    if (!flagNames.Contains(name))
                        throw Fail("unknown switch `" + name + "'");
                    _flags.Add(name);
                }
            }
        }

        public string Command { get; }

        public string Usage { get; }

        public List<string> Operands { get; } = new();

        public bool SawDoubleDash { get; private set; }

        /// <summary>How many operands preceded <c>--</c>; lets <c>checkout &lt;rev&gt; -- &lt;paths&gt;</c> be told apart from <c>checkout -- &lt;paths&gt;</c>.</summary>
        public int DoubleDashAt { get; private set; } = -1;

        /// <summary>A value-carrying option counts as present for <see cref="Has"/> too, so that
        /// <c>--porcelain</c> and <c>--porcelain=v1</c> answer the same question.</summary>
        private void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out List<string>? list))
                _values[name] = list = new List<string>();
            list.Add(value);
            _flags.Add(name);
        }

        private GitFatalException Fail(string message)
            => new(message + "\nusage: git " + Command + " " + Usage, 129, "error");

        public bool Has(params string[] names) => names.Any(_flags.Contains);

        public string? Value(params string[] names)
        {
            foreach (string name in names)
            {
                if (_values.TryGetValue(name, out List<string>? list) && list.Count > 0)
                    return list[^1];
            }

            return null;
        }

        public IReadOnlyList<string> Values(params string[] names)
        {
            var all = new List<string>();
            foreach (string name in names)
            {
                if (_values.TryGetValue(name, out List<string>? list))
                    all.AddRange(list);
            }

            return all;
        }

        public int? Int(params string[] names)
        {
            string? text = Value(names);
            if (text == null)
                return null;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw Fail("option `" + names[0] + "' expects a number, got '" + text + "'");
            return value;
        }

        /// <summary>Refuses a flag that git has but this implementation does not, naming it.</summary>
        public void Reject(string name, string reason)
        {
            if (_flags.Contains(name) || _values.ContainsKey(name))
                throw new GitFatalException("git " + Command + ": --" + name + " is not implemented by this shell's built-in git (" + reason + ")");
        }
    }

    // =====================================================================================
    // repository access and pathspecs
    // =====================================================================================

    private static GitRepository OpenGitRepository(GitFileGate gate, string cwd)
        => GitRepository.Discover(gate, cwd)
           ?? throw new GitFatalException("not a git repository (or any of the parent directories): .git");

    /// <summary>
    /// Turns command-line pathspecs into a predicate over work-tree-relative paths.
    ///
    /// <para>
    /// Three forms are handled, which between them cover what an agent types: a literal
    /// file, a directory (matching everything beneath it, which is what makes
    /// <c>git add .</c> work), and a glob. Globs arrive here only when the shell did not
    /// already expand them — quoted, or matching nothing on disk — which is exactly when
    /// git itself would do the matching.
    /// </para>
    /// <para>
    /// A pathspec that resolves outside the work tree is refused rather than clamped: it
    /// is the confinement boundary restated at the level of the repository, and clamping
    /// would let <c>git add ../../etc/passwd</c> quietly become <c>git add .</c>.
    /// </para>
    /// </summary>
    private static Func<string, bool>? BuildGitPathFilter(GitRepository repo, string cwd, IReadOnlyList<string> pathspecs, out List<string> normalized)
    {
        normalized = new List<string>();
        if (pathspecs.Count == 0)
            return null;

        var matchers = new List<Func<string, bool>>();
        bool matchAll = false;
        foreach (string spec in pathspecs)
        {
            string full = Path.GetFullPath(Path.IsPathRooted(spec) ? spec : Path.Combine(cwd, spec));
            string relative = Path.GetRelativePath(repo.WorkTree, full).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == "." || relative.Length == 0)
            {
                matchAll = true;
                normalized.Add(string.Empty);
                continue;
            }

            if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..")
                throw new GitFatalException("'" + spec + "' is outside repository at '" + repo.WorkTree + "'");

            normalized.Add(relative);
            if (GlobMatcher.HasGlobChars(relative))
            {
                var regex = new Regex("^" + GlobMatcher.ToRegexText(relative, matchSlash: false) + "$", RegexOptions.CultureInvariant);
                matchers.Add(path => regex.IsMatch(path));
            }
            else
            {
                string prefix = relative + "/";
                matchers.Add(path => string.Equals(path, relative, StringComparison.Ordinal)
                                     || path.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        if (matchAll)
            return null;
        return path => matchers.Any(m => m(path));
    }

    private static void StageGitFile(GitFileGate gate, GitRepository repo, GitIndex index, string relative)
    {
        string absolute = Path.Combine(repo.WorkTree, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!GitWorkTree.TryRead(gate, absolute, out GitWorkTree.GitWorkTreeFile file))
            throw new GitFatalException("pathspec '" + relative + "' did not match any files");
        var entry = new GitIndexEntry
        {
            Path = relative,
            Id = repo.Objects.Write(GitObjectType.Blob, file.Content),
            Mode = file.Mode,
        };
        GitWorkTree.StampStat(entry, file.RealPath, file.Content.LongLength);
        index.Stage(entry);
    }

    // =====================================================================================
    // init
    // =====================================================================================

    private static int GitInit(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("init", args, "q quiet bare", "b initial-branch", "[-q] [-b <branch>] [<directory>]");
        if (opts.Has("bare"))
            throw new GitFatalException("git init: --bare is not implemented by this shell's built-in git (every subcommand here assumes a work tree)");
        if (opts.Operands.Count > 1)
            throw new GitFatalException("too many arguments\nusage: git init [-q] [-b <branch>] [<directory>]", 129, "error");

        string directory = opts.Operands.Count == 1
            ? (Path.IsPathRooted(opts.Operands[0]) ? opts.Operands[0] : Path.Combine(cwd, opts.Operands[0]))
            : cwd;

        // Resolving for WRITE is what refuses `git init /etc/x`: the path has to sit under
        // a root the session may write, links resolved.
        string real = gate.Write(directory);
        using GitRepository repo = GitRepository.Init(gate, real, out bool existed);

        string? branch = opts.Value("b", "initial-branch");
        if (branch != null && !existed)
            repo.SetHeadTo("refs/heads/" + branch);

        if (!opts.Has("q", "quiet"))
        {
            io.Out.WriteLine((existed ? "Reinitialized existing" : "Initialized empty")
                             + " Git repository in " + Path.Combine(repo.GitDir, string.Empty).TrimEnd(Path.DirectorySeparatorChar) + "/");
        }

        _ = exec;
        return 0;
    }

    // =====================================================================================
    // add
    // =====================================================================================

    private static int GitAdd(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts(
            "add",
            args,
            "A all n dry-run v verbose f force u update no-all ignore-removal i interactive p patch",
            "chmod",
            "[-A] [-u] [-n] [-f] <pathspec>...");
        opts.Reject("interactive", "there is no terminal to drive it");
        opts.Reject("patch", "there is no terminal to drive it");
        opts.Reject("chmod", "not implemented");
        if (opts.Has("i"))
            throw new GitFatalException("git add: -i is not implemented by this shell's built-in git (there is no terminal to drive it)");
        if (opts.Has("p"))
            throw new GitFatalException("git add: -p is not implemented by this shell's built-in git (there is no terminal to drive it)");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        bool all = opts.Has("A", "all");
        bool updateOnly = opts.Has("u", "update");
        bool dryRun = opts.Has("n", "dry-run");
        bool force = opts.Has("f", "force");
        bool verbose = opts.Has("v", "verbose");

        if (opts.Operands.Count == 0 && !all && !updateOnly)
        {
            io.Error("fatal", "Nothing specified, nothing added.");
            io.Err.WriteLine("hint: Maybe you wanted to say 'git add .'?");
            return 128;
        }

        Func<string, bool>? filter = BuildGitPathFilter(repo, cwd, opts.Operands, out List<string> specs);
        GitIndex index = GitIndex.Read(gate, repo.IndexPath);

        // -f is the user overriding the exclusion, so the listing must stop applying it;
        // filtering anyway would report the named file as "did not match any files".
        List<string> present = GitWorkTree.ListFiles(
            gate,
            repo,
            force ? null : GitIgnore.Create(gate, repo.GitDir, repo.WorkTree),
            exec.Cancellation);

        var matchedFiles = new List<string>();
        foreach (string path in present)
        {
            if (filter == null || filter(path))
                matchedFiles.Add(path);
        }

        // A pathspec naming an ignored file is an error, not a silent skip: the agent asked
        // for that exact file and would otherwise believe it was staged.
        if (!force && !updateOnly)
        {
            var ignored = new List<string>();
            foreach (string spec in specs)
            {
                if (spec.Length == 0 || GlobMatcher.HasGlobChars(spec))
                    continue;
                string absolute = Path.Combine(repo.WorkTree, spec.Replace('/', Path.DirectorySeparatorChar));
                if (!gate.TryRead(absolute, out string real) || Directory.Exists(real))
                    continue;
                if (!present.Contains(spec) && File.Exists(real)
                    && GitIgnore.IsPathIgnored(gate, repo.GitDir, repo.WorkTree, spec, isDirectory: false))
                {
                    ignored.Add(spec);
                }
            }

            if (ignored.Count > 0)
            {
                io.Err.WriteLine("The following paths are ignored by one of your .gitignore files:");
                foreach (string path in ignored)
                    io.Err.WriteLine(path);
                io.Err.WriteLine("hint: Use -f if you really want to add them.");
                return 1;
            }
        }

        // Tracked paths whose file is gone: `git add <path>` has staged deletions since
        // git 2.0, and `-u` stages only these plus modifications.
        var removed = new List<string>();
        foreach (GitIndexEntry entry in index.Entries)
        {
            if (entry.Stage != 0 || (filter != null && !filter(entry.Path)))
                continue;
            string absolute = Path.Combine(repo.WorkTree, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!GitWorkTree.Exists(gate, absolute))
                removed.Add(entry.Path);
        }

        if (updateOnly)
        {
            var tracked = new HashSet<string>(index.Entries.Where(e => e.Stage == 0).Select(e => e.Path), StringComparer.Ordinal);
            matchedFiles = matchedFiles.Where(tracked.Contains).ToList();
        }

        if (matchedFiles.Count == 0 && removed.Count == 0 && opts.Operands.Count > 0)
        {
            string missing = specs.FirstOrDefault(s => s.Length > 0) ?? opts.Operands[0];
            throw new GitFatalException("pathspec '" + missing + "' did not match any files");
        }

        foreach (string path in matchedFiles)
        {
            exec.CheckCancel();
            if (!dryRun)
                StageGitFile(gate, repo, index, path);
            if (verbose || dryRun)
                io.Out.WriteLine("add '" + path + "'");
        }

        foreach (string path in removed)
        {
            if (!dryRun)
                index.Remove(path);
            if (verbose || dryRun)
                io.Out.WriteLine("remove '" + path + "'");
        }

        if (!dryRun)
            index.Write(gate, repo.IndexPath);
        return 0;
    }

    // =====================================================================================
    // commit
    // =====================================================================================

    private static int GitCommitCommand(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, IReadOnlyList<string> oneShotConfig, ShellStreams io)
    {
        var opts = new GitOpts(
            "commit",
            args,
            "a all amend allow-empty allow-empty-message q quiet v verbose n no-verify s signoff dry-run",
            "m message F file author date",
            "[-a] [--amend] -m <msg> [<pathspec>...]");
        foreach (string rejected in new[] { "interactive", "patch", "fixup", "squash", "gpg-sign" })
            opts.Reject(rejected, "not implemented");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        foreach (string setting in oneShotConfig)
        {
            int eq = setting.IndexOf('=');
            if (eq > 0)
                repo.Config.Set(setting[..eq], setting[(eq + 1)..]);
        }

        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        if (index.HasConflicts)
        {
            io.Error("error", "Committing is not possible because you have unmerged files.");
            io.Err.WriteLine("hint: Fix them up in the work tree, and then use 'git add/rm <file>' as appropriate.");
            return 1;
        }

        // Hooks cannot run on this host, and pretending they passed would defeat whatever
        // the repository installed them to prevent. Say so instead of skipping quietly.
        if (!opts.Has("n", "no-verify"))
        {
            foreach (string hook in new[] { "pre-commit", "commit-msg" })
            {
                if (gate.FileExists(Path.Combine(repo.GitDir, "hooks", hook)))
                {
                    throw new GitFatalException(
                        "this repository has a " + hook + " hook, and this host cannot run programs, so it cannot be honoured. "
                        + "Re-run with --no-verify to commit without it.");
                }
            }
        }

        if (opts.Has("a", "all"))
        {
            var ignore = GitIgnore.Create(gate, repo.GitDir, repo.WorkTree);
            List<string> present = GitWorkTree.ListFiles(gate, repo, ignore, exec.Cancellation);
            var tracked = index.Entries.Where(e => e.Stage == 0).Select(e => e.Path).ToList();
            foreach (string path in tracked)
            {
                if (present.Contains(path))
                    StageGitFile(gate, repo, index, path);
                else
                    index.Remove(path);
            }
        }
        else if (opts.Operands.Count > 0)
        {
            throw new GitFatalException(
                "git commit: committing a subset of paths is not implemented by this shell's built-in git; "
                + "use `git add <pathspec>` then `git commit`");
        }

        IReadOnlyList<string> messages = opts.Values("m", "message");
        string? messageFile = opts.Value("F", "file");
        string message;
        if (messageFile != null)
        {
            message = messageFile == "-" ? ShellText.ReadAllText(io.In) : gate.ReadAllText(Path.Combine(cwd, messageFile));
        }
        else if (messages.Count > 0)
        {
            message = string.Join("\n\n", messages);
        }
        else if (opts.Has("amend"))
        {
            string? existing = repo.ResolveHead();
            message = existing == null ? string.Empty : repo.ReadCommit(existing).Message;
        }
        else
        {
            message = string.Empty;
        }

        message = message.Replace("\r\n", "\n").Trim('\n');
        if (message.Length == 0 && !opts.Has("allow-empty-message"))
        {
            throw new GitFatalException(
                "no commit message given. This host has no editor, so -m <msg> or -F <file> is required "
                + "(or --allow-empty-message to record none).");
        }

        message += "\n";

        string? head = repo.ResolveHead();
        List<string> parents;
        if (opts.Has("amend"))
        {
            if (head == null)
                throw new GitFatalException("You have nothing to amend.");
            parents = repo.ReadCommit(head).Parents.ToList();
        }
        else
        {
            parents = head == null ? new List<string>() : new List<string> { head };
        }

        // Refuse the empty commit before anything is written, so that "nothing happened"
        // and "exit 0" never coincide.
        string tree = repo.WriteTreeFromIndex(index);
        string? parentTree = parents.Count > 0 ? repo.ReadCommit(parents[0]).Tree : null;
        GitStatusResult status = GitWorkTree.Compute(gate, repo, index, exec.Cancellation);
        if (tree == parentTree && !opts.Has("allow-empty") && !opts.Has("amend"))
        {
            WriteGitHumanStatus(status, io.Out);
            return 1;
        }

        if (opts.Has("dry-run"))
        {
            WriteGitHumanStatus(status, io.Out);
            return 0;
        }

        GitSignature author = ResolveGitSignature(exec, repo, "AUTHOR", opts.Value("author"), opts.Value("date"));
        GitSignature committer = ResolveGitSignature(exec, repo, "COMMITTER", null, null);
        if (opts.Has("s", "signoff"))
            message = message.TrimEnd('\n') + "\n\nSigned-off-by: " + author.Name + " <" + author.Email + ">\n";

        string commitId = repo.Objects.Write(GitObjectType.Commit, GitCommit.Serialize(tree, parents, author, committer, message));
        string branchRef = repo.HeadRefName() ?? "HEAD";
        string? previous = repo.ReadRef(branchRef);
        if (branchRef == "HEAD")
            gate.WriteAllTextAtomic(Path.Combine(repo.GitDir, "HEAD"), commitId + "\n");
        else
            repo.UpdateRef(branchRef, commitId);

        bool rootCommit = parents.Count == 0;
        string reflog = opts.Has("amend") ? "commit (amend): " : rootCommit ? "commit (initial): " : "commit: ";
        GitCommit written = repo.ReadCommit(commitId);
        repo.AppendReflog(branchRef, previous, commitId, committer, reflog + written.Subject);

        // The summary line git prints, with real counts taken from the diff just recorded.
        GitDiff.Result diff = GitDiff.Render(
            GitDiffSide.FromCommit(repo, parents.Count > 0 ? parents[0] : null),
            GitDiffSide.FromTree(repo, tree),
            include: null);

        if (!opts.Has("q", "quiet"))
        {
            string label = repo.HeadRefName() == null ? "detached HEAD" : repo.CurrentBranchName();
            io.Out.WriteLine("[" + label + (rootCommit ? " (root-commit) " : " ") + GitObjectId.Abbreviate(commitId) + "] " + written.Subject);
            if (diff.FilesChanged > 0)
                io.Out.WriteLine(GitWorkTree.FormatDiffStat(diff.FilesChanged, diff.Insertions, diff.Deletions));
        }

        return 0;
    }

    /// <summary>
    /// The identity for a commit: the environment first, then <c>.git/config</c>, then
    /// <see cref="DefaultGitAuthorName"/>. The global config is not consulted — it is
    /// outside the session's confinement — which is stated in <see cref="GitConfig"/>.
    /// </summary>
    private static GitSignature ResolveGitSignature(ShellExec exec, GitRepository repo, string kind, string? authorSpec, string? dateSpec)
    {
        string name = Env(exec, "GIT_" + kind + "_NAME") ?? repo.Config.Get("user.name") ?? DefaultGitAuthorName;
        string email = Env(exec, "GIT_" + kind + "_EMAIL") ?? repo.Config.Get("user.email") ?? DefaultGitAuthorEmail;

        if (authorSpec != null)
        {
            int open = authorSpec.LastIndexOf('<');
            int close = authorSpec.LastIndexOf('>');
            if (open < 0 || close < open)
                throw new GitFatalException("--author '" + authorSpec + "' is not 'Name <email>'");
            name = authorSpec[..open].Trim();
            email = authorSpec[(open + 1)..close];
        }

        string? date = dateSpec ?? Env(exec, "GIT_" + kind + "_DATE");
        DateTimeOffset when = DateTimeOffset.Now;
        if (date != null)
        {
            if (long.TryParse(date.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix) && date.Contains(' '))
                when = GitSignature.Parse("x <x> " + date).When;
            else if (long.TryParse(date, NumberStyles.Integer, CultureInfo.InvariantCulture, out unix))
                when = DateTimeOffset.FromUnixTimeSeconds(unix);
            else if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
                when = parsed;
            else
                throw new GitFatalException("invalid date format: " + date);
        }

        return new GitSignature(name, email, when);
    }

    private static string? Env(ShellExec exec, string name)
    {
        string value = exec.State.Get(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // =====================================================================================
    // status
    // =====================================================================================

    private static int GitStatus(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        // `-u`, `-uno`, `-uall` are one option with an attached value, which the general
        // short-cluster rule would read as `-u -n -o`. Rewriting to the long spelling keeps
        // the cluster rule simple and still refuses a mode that is not understood.
        var rewritten = new List<string>(args.Count);
        foreach (string arg in args)
        {
            if (arg == "-u")
                rewritten.Add("--untracked-files=all");
            else if (arg.Length > 2 && arg.StartsWith("-u", StringComparison.Ordinal))
                rewritten.Add("--untracked-files=" + arg[2..]);
            else
                rewritten.Add(arg);
        }

        var opts = new GitOpts(
            "status",
            rewritten,
            // --long is the default human format and --no-renames is the only behaviour this
            // git has, so accepting them is honest rather than a no-op.
            "s short b branch long z ignored no-renames",
            "untracked-files? porcelain?",
            "[-s | --porcelain] [-b] [-z] [-u<mode>]");
        opts.Reject("ignored", "not implemented");

        string? porcelainVersion = opts.Value("porcelain");
        if (porcelainVersion is not (null or "1" or "v1"))
            throw new GitFatalException("git status: --porcelain=" + porcelainVersion + " is not implemented by this shell's built-in git (only v1)");

        // `normal` collapses an untracked directory to one entry and `all` lists its files;
        // this always lists files, which is the more useful of the two for an agent about to
        // stage them, and a superset of what `normal` would have shown.
        string untrackedMode = opts.Value("untracked-files") ?? "all";
        if (untrackedMode is not ("all" or "normal" or "no" or "none"))
            throw new GitFatalException("git status: --untracked-files=" + untrackedMode + " is not implemented by this shell's built-in git (all, normal, no)");
        bool showUntracked = untrackedMode is not ("no" or "none");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        GitStatusResult status = GitWorkTree.Compute(gate, repo, index, exec.Cancellation);
        if (!showUntracked)
            status.Untracked.Clear();

        bool porcelain = opts.Has("porcelain") || opts.Has("s", "short");
        if (!porcelain)
        {
            WriteGitHumanStatus(status, io.Out);
            return 0;
        }

        string terminator = opts.Has("z") ? "\0" : "\n";
        if (opts.Has("b", "branch"))
        {
            io.Out.Write("## " + (status.Unborn ? "No commits yet on " + status.Branch : status.Branch) + terminator);
        }

        foreach (GitStatusEntry entry in status.Changes)
            io.Out.Write(GitWorkTree.PorcelainCode(entry.Staged, entry.Unstaged) + " " + entry.Path + terminator);
        foreach (string path in status.Untracked)
            io.Out.Write("?? " + path + terminator);
        return 0;
    }

    private static void WriteGitHumanStatus(GitStatusResult status, ShellWriter output)
    {
        output.WriteLine(status.Detached ? "HEAD detached" : "On branch " + status.Branch);
        if (status.Unborn)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("No commits yet");
        }

        var staged = status.Changes.Where(c => c.Staged != GitChange.None).ToList();
        var unstaged = status.Changes.Where(c => c.Unstaged != GitChange.None).ToList();

        if (staged.Count > 0)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("Changes to be committed:");
            output.WriteLine("  (use \"git restore --staged <file>...\" to unstage)");
            foreach (GitStatusEntry entry in staged)
                output.WriteLine("\t" + GitWorkTree.HumanLabel(entry.Staged) + entry.Path);
        }

        if (unstaged.Count > 0)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("Changes not staged for commit:");
            output.WriteLine("  (use \"git add <file>...\" to update what will be committed)");
            output.WriteLine("  (use \"git restore <file>...\" to discard changes in working directory)");
            foreach (GitStatusEntry entry in unstaged)
                output.WriteLine("\t" + GitWorkTree.HumanLabel(entry.Unstaged) + entry.Path);
        }

        if (status.Untracked.Count > 0)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("Untracked files:");
            output.WriteLine("  (use \"git add <file>...\" to include in what will be committed)");
            foreach (string path in status.Untracked)
                output.WriteLine("\t" + path);
        }

        output.WriteLine(string.Empty);
        if (staged.Count > 0)
            return;
        if (unstaged.Count > 0)
            output.WriteLine("no changes added to commit (use \"git add\" and/or \"git commit -a\")");
        else if (status.Untracked.Count > 0)
            output.WriteLine("nothing added to commit but untracked files present (use \"git add\" to track)");
        else if (status.Unborn)
            output.WriteLine("nothing to commit (create/copy files and use \"git add\" to track)");
        else
            output.WriteLine("nothing to commit, working tree clean");
    }

    // =====================================================================================
    // diff
    // =====================================================================================

    private static int GitDiffCommand(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts(
            "diff",
            args,
            "cached staged stat name-only exit-code no-color quiet patch p binary text M",
            "U unified color? find-renames?",
            "[--cached] [--stat] [--name-only] [<rev>] [--] [<path>...]");
        foreach (string rejected in new[] { "find-renames", "word-diff", "ignore-all-space", "ignore-space-change" })
            opts.Reject(rejected, "rename and whitespace detection are not implemented");
        if (opts.Has("M"))
            throw new GitFatalException("git diff: -M is not implemented by this shell's built-in git (rename detection); a rename shows as a delete plus an add");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        int context = opts.Int("U", "unified") ?? 3;
        bool cached = opts.Has("cached", "staged");

        // Operands before `--` are revisions; everything after is a path. Without `--`,
        // an operand that names an existing file is a path and anything else is a revision,
        // which is git's own disambiguation.
        var revisions = new List<string>();
        var paths = new List<string>();
        for (int i = 0; i < opts.Operands.Count; i++)
        {
            string operand = opts.Operands[i];
            bool afterDashDash = opts.DoubleDashAt >= 0 && i >= opts.DoubleDashAt;
            if (afterDashDash)
            {
                paths.Add(operand);
                continue;
            }

            string candidate = Path.IsPathRooted(operand) ? operand : Path.Combine(cwd, operand);
            if (gate.TryRead(candidate, out string real) && (File.Exists(real) || Directory.Exists(real)))
                paths.Add(operand);
            else
                revisions.Add(operand);
        }

        Func<string, bool>? filter = BuildGitPathFilter(repo, cwd, paths, out _);

        GitDiffSide left;
        GitDiffSide right;
        if (revisions.Count >= 2)
        {
            left = GitDiffSide.FromCommit(repo, repo.PeelToCommit(repo.ResolveRevision(revisions[0]), revisions[0]));
            right = GitDiffSide.FromCommit(repo, repo.PeelToCommit(repo.ResolveRevision(revisions[1]), revisions[1]));
        }
        else if (revisions.Count == 1)
        {
            left = GitDiffSide.FromCommit(repo, repo.PeelToCommit(repo.ResolveRevision(revisions[0]), revisions[0]));
            right = cached
                ? GitDiffSide.FromIndex(repo, index)
                : GitDiffSide.FromWorkTree(gate, repo, WorkTreePathsFor(gate, repo, index, exec));
        }
        else if (cached)
        {
            left = GitDiffSide.FromCommit(repo, repo.ResolveHead());
            right = GitDiffSide.FromIndex(repo, index);
        }
        else
        {
            left = GitDiffSide.FromIndex(repo, index);
            right = GitDiffSide.FromWorkTree(gate, repo, index.Entries.Where(e => e.Stage == 0).Select(e => e.Path));
        }

        GitDiff.Result result = GitDiff.Render(left, right, filter, context, opts.Has("name-only"), opts.Has("stat"));
        if (!opts.Has("quiet"))
            io.Out.Write(result.Text);
        return (opts.Has("exit-code") || opts.Has("quiet")) && result.FilesChanged > 0 ? 1 : 0;
    }

    /// <summary>Tracked paths plus what is on disk, which is the set a rev-to-worktree diff compares.</summary>
    private static List<string> WorkTreePathsFor(GitFileGate gate, GitRepository repo, GitIndex index, ShellExec exec)
    {
        var paths = new HashSet<string>(index.Entries.Where(e => e.Stage == 0).Select(e => e.Path), StringComparer.Ordinal);
        var ignore = GitIgnore.Create(gate, repo.GitDir, repo.WorkTree);
        foreach (string path in GitWorkTree.ListFiles(gate, repo, ignore, exec.Cancellation))
            paths.Add(path);
        return paths.ToList();
    }

    // =====================================================================================
    // log and show
    // =====================================================================================

    private static int GitLog(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts(
            "log",
            args,
            "oneline p patch stat name-only reverse no-merges graph all decorate no-decorate abbrev-commit",
            "n max-count format pretty skip",
            "[--oneline] [-n <count>] [-p] [--stat] [<rev>]");
        opts.Reject("graph", "not implemented");
        opts.Reject("all", "not implemented; name the revision explicitly");

        string? format = opts.Value("format", "pretty");
        bool oneline = opts.Has("oneline") || format is "oneline";
        if (format is not (null or "oneline" or "medium" or "short"))
            throw new GitFatalException("git log: --format=" + format + " is not implemented by this shell's built-in git (only oneline, short, medium)");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        string? start;
        if (opts.Operands.Count > 0)
        {
            start = repo.PeelToCommit(repo.ResolveRevision(opts.Operands[0]), opts.Operands[0]);
        }
        else
        {
            start = repo.ResolveHead();
            if (start == null)
            {
                throw new GitFatalException(
                    "your current branch '" + repo.CurrentBranchName() + "' does not have any commits yet");
            }
        }

        int limit = opts.Int("n", "max-count") ?? int.MaxValue;
        int skip = opts.Int("skip") ?? 0;
        List<GitCommit> history = WalkGitHistory(repo, start, limit + skip, exec);
        if (skip > 0)
            history = history.Skip(skip).ToList();
        if (opts.Has("reverse"))
            history.Reverse();

        foreach (GitCommit commit in history)
        {
            exec.CheckCancel();
            if (oneline)
            {
                io.Out.WriteLine(GitObjectId.Abbreviate(commit.Id) + " " + commit.Subject);
                continue;
            }

            io.Out.WriteLine("commit " + commit.Id);
            if (commit.Parents.Count > 1)
                io.Out.WriteLine("Merge: " + string.Join(" ", commit.Parents.Select(p => GitObjectId.Abbreviate(p))));
            io.Out.WriteLine("Author: " + commit.Author.Name + " <" + commit.Author.Email + ">");
            io.Out.WriteLine("Date:   " + commit.Author.FormatDate());
            io.Out.WriteLine(string.Empty);
            foreach (string line in ShellText.Lines(commit.Message.TrimEnd('\n')))
                io.Out.WriteLine("    " + line);
            io.Out.WriteLine(string.Empty);

            if (opts.Has("p", "patch") || opts.Has("stat") || opts.Has("name-only"))
                io.Out.Write(RenderGitCommitDiff(repo, commit, opts.Has("stat"), opts.Has("name-only")).Text);
        }

        return 0;
    }

    /// <summary>
    /// Walks history newest-first.
    ///
    /// <para>
    /// Git's default ordering is by commit date, not by depth, which matters as soon as
    /// there is a merge: a first-parent-only walk would silently hide an entire merged
    /// branch. The frontier below is therefore ordered by committer date and every parent
    /// is enqueued, with a visited set so a commit reachable two ways is printed once.
    /// </para>
    /// </summary>
    private static List<GitCommit> WalkGitHistory(GitRepository repo, string start, int limit, ShellExec exec)
    {
        var result = new List<GitCommit>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var frontier = new List<GitCommit> { repo.ReadCommit(start) };

        while (frontier.Count > 0 && result.Count < limit)
        {
            exec.CheckCancel();
            int best = 0;
            for (int i = 1; i < frontier.Count; i++)
            {
                if (frontier[i].Committer.When > frontier[best].Committer.When)
                    best = i;
            }

            GitCommit commit = frontier[best];
            frontier.RemoveAt(best);
            result.Add(commit);

            foreach (string parent in commit.Parents)
            {
                if (seen.Add(parent))
                    frontier.Add(repo.ReadCommit(parent));
            }
        }

        return result;
    }

    private static GitDiff.Result RenderGitCommitDiff(GitRepository repo, GitCommit commit, bool stat, bool nameOnly)
        => GitDiff.Render(
            GitDiffSide.FromCommit(repo, commit.Parents.Count > 0 ? commit.Parents[0] : null),
            GitDiffSide.FromTree(repo, commit.Tree),
            include: null,
            nameOnly: nameOnly,
            statOnly: stat);

    private static int GitShow(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("show", args, "stat name-only s no-patch oneline", "format pretty", "[--stat] [-s] <object>");
        if (opts.Value("format", "pretty") is string format && format != "oneline")
            throw new GitFatalException("git show: --format=" + format + " is not implemented by this shell's built-in git");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        string spec = opts.Operands.Count > 0 ? opts.Operands[0] : "HEAD";

        // `<rev>:<path>` names a blob or tree inside a commit, which is how an agent reads
        // the committed version of a file it has since broken.
        int colon = spec.IndexOf(':');
        if (colon > 0)
        {
            string revision = spec[..colon];
            string path = spec[(colon + 1)..].Replace('\\', '/');
            string commitId = repo.PeelToCommit(repo.ResolveRevision(revision), revision);
            Dictionary<string, GitTreeEntry> tree = repo.ReadCommitTree(commitId);
            if (!tree.TryGetValue(path, out GitTreeEntry entry))
            {
                var children = tree.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
                if (children.Count == 0)
                    throw new GitFatalException("path '" + path + "' does not exist in '" + revision + "'");
                io.Out.WriteLine("tree " + spec + ":");
                io.Out.WriteLine(string.Empty);
                foreach (string child in children.OrderBy(c => c, StringComparer.Ordinal))
                    io.Out.WriteLine(child[(path.Length + 1)..]);
                return 0;
            }

            io.Out.Write(repo.Objects.Read(entry.Id, GitObjectType.Blob).Payload);
            return 0;
        }

        string id = repo.ResolveRevision(spec);
        GitObjectData data = repo.Objects.Read(id);
        switch (data.Type)
        {
            case GitObjectType.Blob:
                io.Out.Write(data.Payload);
                return 0;
            case GitObjectType.Tree:
                foreach (GitTreeEntry entry in GitTree.Parse(data.Payload))
                {
                    io.Out.WriteLine(GitFileMode.FormatPadded(entry.Mode) + " " + GitObjectTypes.Name(entry.IsTree ? GitObjectType.Tree : GitObjectType.Blob)
                                     + " " + entry.Id + "\t" + entry.Name);
                }

                return 0;
        }

        GitCommit commit = repo.ReadCommit(repo.PeelToCommit(id, spec));
        if (opts.Has("oneline"))
        {
            io.Out.WriteLine(GitObjectId.Abbreviate(commit.Id) + " " + commit.Subject);
        }
        else
        {
            io.Out.WriteLine("commit " + commit.Id);
            if (commit.Parents.Count > 1)
                io.Out.WriteLine("Merge: " + string.Join(" ", commit.Parents.Select(p => GitObjectId.Abbreviate(p))));
            io.Out.WriteLine("Author: " + commit.Author.Name + " <" + commit.Author.Email + ">");
            io.Out.WriteLine("Date:   " + commit.Author.FormatDate());
            io.Out.WriteLine(string.Empty);
            foreach (string line in ShellText.Lines(commit.Message.TrimEnd('\n')))
                io.Out.WriteLine("    " + line);
            io.Out.WriteLine(string.Empty);
        }

        if (!opts.Has("s", "no-patch"))
            io.Out.Write(RenderGitCommitDiff(repo, commit, opts.Has("stat"), opts.Has("name-only")).Text);
        _ = exec;
        return 0;
    }

    // =====================================================================================
    // checkout / restore
    // =====================================================================================

    private static int GitCheckout(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("checkout", args, "f force q quiet b B detach orphan track", "source", "[<rev>] -- <pathspec>...");
        foreach (string rejected in new[] { "orphan", "track", "detach" })
            opts.Reject(rejected, "branch switching is not implemented");
        if (opts.Has("b") || opts.Has("B"))
            throw new GitFatalException("git checkout: -b is not implemented by this shell's built-in git; use `git branch <name>` (this git cannot switch branches)");

        using GitRepository repo = OpenGitRepository(gate, cwd);

        // Only the path-restoring form is supported. Switching branches means rewriting the
        // working tree — deleting files the agent may not have committed — and none of the
        // three reasons this builtin exists needs it.
        if (!opts.SawDoubleDash)
        {
            string what = opts.Operands.Count > 0 ? "'" + opts.Operands[0] + "'" : "a branch";
            throw new GitFatalException(
                "git checkout: switching to " + what + " is not implemented by this shell's built-in git. "
                + "Only `git checkout [<rev>] -- <pathspec>...` (restoring files) is available.");
        }

        var revisions = opts.Operands.Take(opts.DoubleDashAt).ToList();
        var paths = opts.Operands.Skip(opts.DoubleDashAt).ToList();
        if (paths.Count == 0)
            throw new GitFatalException("you must specify path(s) to restore", 129, "error");
        if (revisions.Count > 1)
            throw new GitFatalException("only one revision may precede -- ", 129, "error");

        string? source = revisions.Count == 1 ? revisions[0] : null;
        return RestoreGitPaths(exec, gate, repo, cwd, paths, source, staged: source != null, worktree: true, io);
    }

    private static int GitRestore(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("restore", args, "S staged W worktree q quiet", "s source", "[--source=<rev>] [--staged] [--worktree] <pathspec>...");
        using GitRepository repo = OpenGitRepository(gate, cwd);
        var paths = opts.Operands.ToList();
        if (paths.Count == 0)
            throw new GitFatalException("you must specify path(s) to restore", 129, "error");

        bool staged = opts.Has("S", "staged");
        bool worktree = opts.Has("W", "worktree") || !staged;
        return RestoreGitPaths(exec, gate, repo, cwd, paths, opts.Value("s", "source"), staged, worktree, io);
    }

    /// <summary>
    /// The "undo my bad edit" path. Content comes from a named revision when one is given
    /// and from the index otherwise, which is why <c>git checkout -- f</c> undoes an edit
    /// but keeps a staged change: the index is the checkpoint.
    /// </summary>
    private static int RestoreGitPaths(ShellExec exec, GitFileGate gate, GitRepository repo, string cwd, IReadOnlyList<string> pathspecs, string? source, bool staged, bool worktree, ShellStreams io)
    {
        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        Func<string, bool>? filter = BuildGitPathFilter(repo, cwd, pathspecs, out List<string> specs);

        Dictionary<string, GitTreeEntry> from;
        if (source != null)
        {
            from = repo.ReadCommitTree(repo.PeelToCommit(repo.ResolveRevision(source), source));
        }
        else if (staged)
        {
            from = repo.ReadCommitTree(repo.ResolveHead());
        }
        else
        {
            from = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
            foreach (GitIndexEntry entry in index.Entries)
            {
                if (entry.Stage == 0)
                    from[entry.Path] = new GitTreeEntry(entry.Mode, entry.Path, entry.Id);
            }
        }

        var matched = from.Keys.Where(p => filter == null || filter(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (matched.Count == 0)
        {
            string missing = specs.FirstOrDefault(s => s.Length > 0) ?? pathspecs[0];
            io.Error("error", "pathspec '" + missing + "' did not match any file(s) known to git");
            return 1;
        }

        foreach (string path in matched)
        {
            exec.CheckCancel();
            GitTreeEntry entry = from[path];
            if (entry.Mode == GitFileMode.GitLink)
                continue;
            byte[] content = repo.Objects.Read(entry.Id, GitObjectType.Blob).Payload;

            if (worktree)
                GitWorkTree.Checkout(gate, repo, path, entry.Mode, content);

            if (staged || source != null)
            {
                var staging = new GitIndexEntry { Path = path, Id = entry.Id, Mode = entry.Mode, Size = content.LongLength };
                string absolute = Path.Combine(repo.WorkTree, path.Replace('/', Path.DirectorySeparatorChar));
                if (worktree && gate.TryResolveLeaf(absolute, PathAccess.Read, out string leaf) && File.Exists(leaf))
                    GitWorkTree.StampStat(staging, leaf, content.LongLength);
                staging.Size = content.LongLength;
                index.Stage(staging);
            }
        }

        if (staged || source != null)
            index.Write(gate, repo.IndexPath);
        return 0;
    }

    // =====================================================================================
    // rm / mv
    // =====================================================================================

    private static int GitRm(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("rm", args, "r cached f force n dry-run q quiet ignore-unmatch", string.Empty, "[-r] [--cached] [-f] <pathspec>...");
        using GitRepository repo = OpenGitRepository(gate, cwd);
        if (opts.Operands.Count == 0)
            throw new GitFatalException("No pathspec was given. Which files should I remove?", 129, "error");

        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        Func<string, bool>? filter = BuildGitPathFilter(repo, cwd, opts.Operands, out List<string> specs);
        var matched = index.Entries
            .Where(e => e.Stage == 0 && (filter == null || filter(e.Path)))
            .Select(e => e.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (matched.Count == 0)
        {
            if (opts.Has("ignore-unmatch"))
                return 0;
            throw new GitFatalException("pathspec '" + (specs.FirstOrDefault(s => s.Length > 0) ?? opts.Operands[0]) + "' did not match any files");
        }

        // Removing a directory's worth of files needs -r, exactly as git requires, so that
        // a mistyped pathspec cannot delete a tree.
        if (!opts.Has("r"))
        {
            foreach (string spec in specs)
            {
                if (spec.Length == 0)
                    continue;
                if (matched.Any(p => p.StartsWith(spec + "/", StringComparison.Ordinal)))
                    throw new GitFatalException("not removing '" + spec + "' recursively without -r");
            }
        }

        // A file with unstaged changes would lose them; git demands -f and so does this.
        if (!opts.Has("f", "force") && !opts.Has("cached"))
        {
            var dirty = new List<string>();
            foreach (string path in matched)
            {
                GitIndexEntry? entry = index.Find(path);
                string absolute = Path.Combine(repo.WorkTree, path.Replace('/', Path.DirectorySeparatorChar));
                if (entry == null || !GitWorkTree.TryRead(gate, absolute, out GitWorkTree.GitWorkTreeFile file))
                    continue;
                if (GitObjectId.Compute(GitObjectType.Blob, file.Content) != entry.Id)
                    dirty.Add(path);
            }

            if (dirty.Count > 0)
            {
                io.Error("error", "the following file has local modifications:");
                foreach (string path in dirty)
                    io.Err.WriteLine("    " + path);
                io.Err.WriteLine("(use --cached to keep the file, or -f to force removal)");
                return 1;
            }
        }

        foreach (string path in matched)
        {
            exec.CheckCancel();
            if (!opts.Has("n", "dry-run"))
            {
                index.Remove(path);
                if (!opts.Has("cached"))
                {
                    string absolute = Path.Combine(repo.WorkTree, path.Replace('/', Path.DirectorySeparatorChar));
                    if (gate.TryResolveLeaf(absolute, PathAccess.Write, out string leaf)
                        && (File.Exists(leaf) || new FileInfo(leaf).LinkTarget != null))
                    {
                        File.Delete(leaf);
                    }
                }
            }

            if (!opts.Has("q", "quiet"))
                io.Out.WriteLine("rm '" + path + "'");
        }

        if (!opts.Has("n", "dry-run"))
            index.Write(gate, repo.IndexPath);
        return 0;
    }

    private static int GitMv(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("mv", args, "f force n dry-run k v verbose", string.Empty, "<source>... <destination>");
        using GitRepository repo = OpenGitRepository(gate, cwd);
        if (opts.Operands.Count < 2)
            throw new GitFatalException("bad source or destination\nusage: git mv <source>... <destination>", 129, "error");

        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        var sources = opts.Operands.Take(opts.Operands.Count - 1).ToList();
        string destinationSpec = opts.Operands[^1];
        string destinationAbsolute = Path.IsPathRooted(destinationSpec) ? destinationSpec : Path.Combine(cwd, destinationSpec);
        bool destinationIsDirectory = gate.TryRead(destinationAbsolute, out string destinationReal) && Directory.Exists(destinationReal);
        if (sources.Count > 1 && !destinationIsDirectory)
            throw new GitFatalException("destination '" + destinationSpec + "' is not a directory");

        foreach (string sourceSpec in sources)
        {
            exec.CheckCancel();
            string sourceAbsolute = Path.IsPathRooted(sourceSpec) ? sourceSpec : Path.Combine(cwd, sourceSpec);
            string sourceRelative = RelativeToGitWorkTree(repo, sourceAbsolute, sourceSpec);
            string targetRelative = destinationIsDirectory
                ? RelativeToGitWorkTree(repo, Path.Combine(destinationAbsolute, Path.GetFileName(sourceAbsolute)), destinationSpec)
                : RelativeToGitWorkTree(repo, destinationAbsolute, destinationSpec);

            GitIndexEntry? entry = index.Find(sourceRelative);
            bool isDirectory = gate.TryRead(sourceAbsolute, out string sourceReal) && Directory.Exists(sourceReal);
            if (entry == null && !isDirectory)
                throw new GitFatalException("bad source, source=" + sourceRelative + ", destination=" + targetRelative + ": not under version control");
            if (index.Find(targetRelative) != null && !opts.Has("f", "force"))
                throw new GitFatalException("destination exists, source=" + sourceRelative + ", destination=" + targetRelative + " (use -f to overwrite)");

            if (opts.Has("n", "dry-run"))
            {
                io.Out.WriteLine("Checking rename of '" + sourceRelative + "' to '" + targetRelative + "'");
                continue;
            }

            // The working tree moves first; the index follows, so a failed move never
            // leaves the index describing a file that is not there.
            string targetAbsolute = Path.Combine(repo.WorkTree, targetRelative.Replace('/', Path.DirectorySeparatorChar));
            string targetPath = gate.Write(targetAbsolute);
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDirectory))
                Directory.CreateDirectory(gate.Write(targetDirectory));

            if (isDirectory)
            {
                Directory.Move(gate.Write(sourceAbsolute), targetPath);
                foreach (string moved in index.RemoveTree(sourceRelative))
                {
                    string tail = moved[(sourceRelative.Length + 1)..];
                    StageGitFile(gate, repo, index, targetRelative + "/" + tail);
                }
            }
            else
            {
                File.Move(gate.Write(sourceAbsolute), targetPath, overwrite: opts.Has("f", "force"));
                index.Remove(sourceRelative);
                StageGitFile(gate, repo, index, targetRelative);
            }

            if (opts.Has("v", "verbose"))
                io.Out.WriteLine("Renaming " + sourceRelative + " to " + targetRelative);
        }

        if (!opts.Has("n", "dry-run"))
            index.Write(gate, repo.IndexPath);
        return 0;
    }

    private static string RelativeToGitWorkTree(GitRepository repo, string absolute, string spelling)
    {
        string relative = Path.GetRelativePath(repo.WorkTree, Path.GetFullPath(absolute)).Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new GitFatalException("'" + spelling + "' is outside repository at '" + repo.WorkTree + "'");
        return relative;
    }

    // =====================================================================================
    // branch / tag / config
    // =====================================================================================

    private static int GitBranch(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("branch", args, "l list d delete D v verbose a all r remotes m M f force q quiet", string.Empty, "[-l] [-d <name>] [<name> [<start-point>]]");
        using GitRepository repo = OpenGitRepository(gate, cwd);
        string? current = repo.HeadRefName();

        if (opts.Has("d", "delete", "D"))
        {
            if (opts.Operands.Count == 0)
                throw new GitFatalException("branch name required", 129, "error");
            foreach (string name in opts.Operands)
            {
                string refName = "refs/heads/" + name;
                string? id = repo.ReadRef(refName);
                if (id == null)
                    throw new GitFatalException("branch '" + name + "' not found.");
                if (refName == current)
                    throw new GitFatalException("Cannot delete branch '" + name + "' checked out at '" + repo.WorkTree + "'");
                gate.Delete(Path.Combine(repo.GitDir, refName.Replace('/', Path.DirectorySeparatorChar)));
                io.Out.WriteLine("Deleted branch " + name + " (was " + GitObjectId.Abbreviate(id) + ").");
            }

            return 0;
        }

        if (opts.Has("m", "M"))
        {
            if (opts.Operands.Count != 1 && opts.Operands.Count != 2)
                throw new GitFatalException("branch rename needs one or two names", 129, "error");
            string from = opts.Operands.Count == 2 ? opts.Operands[0] : repo.CurrentBranchName();
            string to = opts.Operands[^1];
            string? id = repo.ReadRef("refs/heads/" + from) ?? throw new GitFatalException("branch '" + from + "' not found.");
            repo.UpdateRef("refs/heads/" + to, id);
            gate.Delete(Path.Combine(repo.GitDir, "refs", "heads", from.Replace('/', Path.DirectorySeparatorChar)));
            if (current == "refs/heads/" + from)
                repo.SetHeadTo("refs/heads/" + to);
            return 0;
        }

        if (opts.Operands.Count > 0)
        {
            string name = opts.Operands[0];
            string startPoint = opts.Operands.Count > 1 ? opts.Operands[1] : "HEAD";
            if (repo.ReadRef("refs/heads/" + name) != null && !opts.Has("f", "force"))
                throw new GitFatalException("a branch named '" + name + "' already exists");
            string id = repo.PeelToCommit(repo.ResolveRevision(startPoint), startPoint);
            repo.UpdateRef("refs/heads/" + name, id);
            repo.AppendReflog("refs/heads/" + name, null, id, ResolveGitSignature(exec, repo, "COMMITTER", null, null), "branch: Created from " + startPoint);
            return 0;
        }

        List<(string Name, string Id)> branches = repo.ListRefs("refs/heads/");
        if (opts.Has("a", "all", "r", "remotes"))
            branches.AddRange(repo.ListRefs("refs/remotes/"));
        foreach ((string name, string id) in branches)
        {
            string shortName = name["refs/heads/".Length..];
            string marker = name == current ? "* " : "  ";
            if (opts.Has("v", "verbose"))
                io.Out.WriteLine(marker + shortName + " " + GitObjectId.Abbreviate(id) + " " + repo.ReadCommit(id).Subject);
            else
                io.Out.WriteLine(marker + shortName);
        }

        return 0;
    }

    private static int GitTagCommand(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("tag", args, "l list d delete f force a annotate s sign", "m message", "[-l] [-d <name>] [<name> [<commit>]]");
        if (opts.Has("a", "annotate", "s", "sign"))
            throw new GitFatalException("git tag: annotated and signed tags are not implemented by this shell's built-in git (lightweight tags only)");

        using GitRepository repo = OpenGitRepository(gate, cwd);
        if (opts.Has("d", "delete"))
        {
            foreach (string name in opts.Operands)
            {
                string? id = repo.ReadRef("refs/tags/" + name) ?? throw new GitFatalException("tag '" + name + "' not found.");
                gate.Delete(Path.Combine(repo.GitDir, "refs", "tags", name.Replace('/', Path.DirectorySeparatorChar)));
                io.Out.WriteLine("Deleted tag '" + name + "' (was " + GitObjectId.Abbreviate(id) + ")");
            }

            return 0;
        }

        if (opts.Operands.Count > 0 && !opts.Has("l", "list"))
        {
            string name = opts.Operands[0];
            string target = opts.Operands.Count > 1 ? opts.Operands[1] : "HEAD";
            if (repo.ReadRef("refs/tags/" + name) != null && !opts.Has("f", "force"))
                throw new GitFatalException("tag '" + name + "' already exists");
            repo.UpdateRef("refs/tags/" + name, repo.PeelToCommit(repo.ResolveRevision(target), target));
            return 0;
        }

        foreach ((string name, string _) in repo.ListRefs("refs/tags/"))
            io.Out.WriteLine(name["refs/tags/".Length..]);
        _ = exec;
        return 0;
    }

    private static int GitConfigCommand(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("config", args, "l list get unset local global system worktree", string.Empty, "[--list] [--unset] <name> [<value>]");
        if (opts.Has("global", "system"))
        {
            throw new GitFatalException(
                "git config: --global and --system are not available here; those files live outside the session's "
                + "writable roots. Use `git config <name> <value>` to set it in this repository.");
        }

        using GitRepository repo = OpenGitRepository(gate, cwd);
        string configPath = Path.Combine(repo.GitDir, "config");

        if (opts.Has("l", "list"))
        {
            foreach ((string key, string value) in repo.Config.All().OrderBy(kv => kv.Key, StringComparer.Ordinal))
                io.Out.WriteLine(key + "=" + value);
            return 0;
        }

        if (opts.Operands.Count == 0)
            throw new GitFatalException("no config key given\nusage: git config [--list] [--unset] <name> [<value>]", 129, "error");

        string name = opts.Operands[0];
        if (opts.Has("unset"))
        {
            if (!repo.Config.Unset(name))
                return 5;                                       // git's exit code for "key not found"
            gate.WriteAllTextAtomic(configPath, repo.Config.Serialize());
            return 0;
        }

        if (opts.Operands.Count == 1)
        {
            string? value = repo.Config.Get(name);
            if (value == null)
                return 1;
            io.Out.WriteLine(value);
            return 0;
        }

        repo.Config.Set(name, opts.Operands[1]);
        gate.WriteAllTextAtomic(configPath, repo.Config.Serialize());
        _ = exec;
        return 0;
    }

    // =====================================================================================
    // plumbing: rev-parse, cat-file, hash-object, ls-files
    // =====================================================================================

    private static int GitRevParse(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts(
            "rev-parse",
            args,
            "verify quiet q git-dir show-toplevel is-inside-work-tree abbrev-ref symbolic-full-name",
            "short?",
            "[--short] [--verify] <rev>...");
        using GitRepository repo = OpenGitRepository(gate, cwd);

        if (opts.Has("git-dir"))
        {
            io.Out.WriteLine(repo.GitDir);
            return 0;
        }

        if (opts.Has("show-toplevel"))
        {
            io.Out.WriteLine(repo.WorkTree);
            return 0;
        }

        if (opts.Has("is-inside-work-tree"))
        {
            io.Out.WriteLine("true");
            return 0;
        }

        int status = 0;
        foreach (string spec in opts.Operands)
        {
            if (opts.Has("abbrev-ref", "symbolic-full-name"))
            {
                string? name = spec is "HEAD" or "@" ? repo.HeadRefName() : "refs/heads/" + spec;
                if (name == null)
                {
                    io.Out.WriteLine("HEAD");
                    continue;
                }

                io.Out.WriteLine(opts.Has("abbrev-ref") && name.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? name["refs/heads/".Length..]
                    : name);
                continue;
            }

            try
            {
                string id = repo.ResolveRevision(spec);
                int? shortLength = opts.Int("short");
                io.Out.WriteLine(opts.Has("short") ? GitObjectId.Abbreviate(id, shortLength ?? 7) : id);
            }
            catch (GitFatalException) when (opts.Has("quiet", "q"))
            {
                status = 1;
            }
        }

        _ = exec;
        return status;
    }

    private static int GitCatFile(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("cat-file", args, "t s p e", string.Empty, "(-t | -s | -p | -e | <type>) <object>");
        using GitRepository repo = OpenGitRepository(gate, cwd);
        if (opts.Operands.Count == 0)
            throw new GitFatalException("no object given\nusage: git cat-file (-t | -s | -p | -e) <object>", 129, "error");

        string spec = opts.Operands[^1];
        string id = repo.ResolveRevision(spec);
        GitObjectData data = repo.Objects.Read(id);

        if (opts.Has("t"))
        {
            io.Out.WriteLine(GitObjectTypes.Name(data.Type));
            return 0;
        }

        if (opts.Has("s"))
        {
            io.Out.WriteLine(data.Payload.Length.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        if (opts.Has("e"))
            return 0;

        if (data.Type == GitObjectType.Tree)
        {
            foreach (GitTreeEntry entry in GitTree.Parse(data.Payload))
            {
                io.Out.WriteLine(GitFileMode.FormatPadded(entry.Mode) + " " + GitObjectTypes.Name(entry.IsTree ? GitObjectType.Tree : GitObjectType.Blob)
                                 + " " + entry.Id + "\t" + entry.Name);
            }

            return 0;
        }

        io.Out.Write(data.Payload);
        _ = exec;
        return 0;
    }

    private static int GitHashObject(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("hash-object", args, "w stdin", "t", "[-w] [-t <type>] [--stdin] [<file>...]");
        string typeName = opts.Value("t") ?? "blob";
        if (!GitObjectTypes.TryParse(typeName, out GitObjectType type))
            throw new GitFatalException("invalid object type '" + typeName + "'");

        GitRepository? repo = opts.Has("w") ? OpenGitRepository(gate, cwd) : GitRepository.Discover(gate, cwd);
        try
        {
            if (opts.Has("stdin"))
            {
                byte[] content = ShellText.ReadAllBytes(io.In);
                io.Out.WriteLine(opts.Has("w") ? repo!.Objects.Write(type, content) : GitObjectId.Compute(type, content));
            }

            foreach (string file in opts.Operands)
            {
                exec.CheckCancel();
                byte[] content = gate.ReadAllBytes(Path.IsPathRooted(file) ? file : Path.Combine(cwd, file));
                io.Out.WriteLine(opts.Has("w") ? repo!.Objects.Write(type, content) : GitObjectId.Compute(type, content));
            }
        }
        finally
        {
            repo?.Dispose();
        }

        return 0;
    }

    private static int GitLsFiles(ShellExec exec, GitFileGate gate, string cwd, IReadOnlyList<string> args, ShellStreams io)
    {
        var opts = new GitOpts("ls-files", args, "s stage c cached others z m modified d deleted", string.Empty, "[-s] [--others] [-m] [<pathspec>...]");
        using GitRepository repo = OpenGitRepository(gate, cwd);
        GitIndex index = GitIndex.Read(gate, repo.IndexPath);
        Func<string, bool>? filter = BuildGitPathFilter(repo, cwd, opts.Operands, out _);
        string terminator = opts.Has("z") ? "\0" : "\n";

        if (opts.Has("others"))
        {
            GitStatusResult status = GitWorkTree.Compute(gate, repo, index, exec.Cancellation);
            foreach (string path in status.Untracked)
            {
                if (filter == null || filter(path))
                    io.Out.Write(path + terminator);
            }

            return 0;
        }

        if (opts.Has("m", "modified", "d", "deleted"))
        {
            GitStatusResult status = GitWorkTree.Compute(gate, repo, index, exec.Cancellation);
            foreach (GitStatusEntry entry in status.Changes)
            {
                bool wanted = opts.Has("d", "deleted")
                    ? entry.Unstaged == GitChange.Deleted
                    : entry.Unstaged != GitChange.None;
                if (wanted && (filter == null || filter(entry.Path)))
                    io.Out.Write(entry.Path + terminator);
            }

            return 0;
        }

        foreach (GitIndexEntry entry in index.Entries)
        {
            if (filter != null && !filter(entry.Path))
                continue;
            io.Out.Write(opts.Has("s", "stage")
                ? GitFileMode.FormatPadded(entry.Mode) + " " + entry.Id + " " + entry.Stage.ToString(CultureInfo.InvariantCulture) + "\t" + entry.Path + terminator
                : entry.Path + terminator);
        }

        return 0;
    }
}
