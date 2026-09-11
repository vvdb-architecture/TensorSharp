using TensorAgent.Core.Shell;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;

namespace TensorAgent.Tests;

/// <summary>
/// What happens to a live conversation when its working directory disappears.
///
/// <para>
/// On this platform that is not an accident, it is the documented behaviour of the
/// directory the workspace lives in: <c>AgentPaths.ScratchDirectory</c> is under
/// <c>Library/Caches</c>, which iOS purges under storage pressure, including while the app
/// is suspended between two turns of a conversation. The app already treats a purged MODEL
/// as "download it again" rather than as an error; the session workspace had no such
/// answer. Every later command in the conversation failed at this backend's own
/// precondition — <c>the working directory does not exist: …/scratch/ts-session-…/work</c>
/// — which is the same defect the desktop hit through a different message, and which
/// nothing in the conversation could recover from.
/// </para>
/// </summary>
public sealed class WorkspacePurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tensoragent-purge-" + Guid.NewGuid().ToString("N"));

    public WorkspacePurgeTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private CodeExecOptions Options() => new()
    {
        Enabled = true,
        // The phone has no OS sandbox to require: the backend confines by path
        // resolution and says so. This is how AgentAppHost builds it.
        Sandbox = SkillSandboxMode.Off,
        Timeout = TimeSpan.FromSeconds(30),
        ScratchDirectory = Path.Combine(_root, "scratch"),
    };

    private SessionWorkspaceManager Workspaces() =>
        new(Path.Combine(_root, "scratch"));

    /// <summary>
    /// What a purge leaves behind, with no repair in play. The backend creates its own
    /// temp root under the write directory, which recreates the WORK directory as a side
    /// effect — so the one directory that heals by accident is the one the model's files
    /// were in, while the shell's persisted state and the session's package environment
    /// stay gone. Recorded because it is the reason the desktop's symptom and this
    /// platform's are different sentences with the same cause.
    /// </summary>
    [Fact]
    public void APurgeTakesTheShellsStateAndPackagesEvenWhereTheWorkDirectoryHealsByAccident()
    {
        SessionWorkspaceManager manager = Workspaces();
        SessionWorkspace workspace = manager.GetOrCreate("purged");
        var session = new ShellSession(workspace, ShellProgram.InProcess());
        IShellBackend backend = new InProcessShellBackend();

        var launch = new ShellLaunch
        {
            Command = "echo hi",
            Session = session,
            WorkingDirectory = workspace.WorkDirectory,
            WriteDirectory = workspace.WorkDirectory,
            ReadOnlyDirectory = workspace.Root,
            Timeout = TimeSpan.FromSeconds(20),
        };

        Assert.True(backend.TryStart(launch, out IShellJob? started, out _));
        started?.Dispose();

        // What iOS does to Library/Caches when the device is short of space.
        Directory.Delete(workspace.Root, recursive: true);

        Assert.True(backend.TryStart(launch, out IShellJob? job, out _));
        job?.Dispose();

        Assert.True(Directory.Exists(workspace.WorkDirectory));
        Assert.False(Directory.Exists(workspace.ShellStateDirectory));
        Assert.False(Directory.Exists(workspace.EnvDirectory));
        Assert.False(Directory.Exists(workspace.TempDirectory));
    }

    /// <summary>
    /// And the same purge, through the runner the app actually uses: the layout is
    /// re-asserted before the backend is ever asked, so the conversation keeps working.
    /// </summary>
    [Fact]
    public void TheRunnerRebuildsAPurgedWorkspaceAndKeepsGoing()
    {
        SessionWorkspaceManager manager = Workspaces();
        SessionWorkspace workspace = manager.GetOrCreate("purged-runner");
        using var runner = new ShellRunner(Options(), backend: new InProcessShellBackend());

        CodeExecResult before = runner.Run(new ShellRequest("echo before"), workspace);
        Assert.True(before.Ok, before.Content);

        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult after = runner.Run(new ShellRequest("echo after"), workspace);

        Assert.True(after.Ok, after.Content);
        Assert.Contains("after", after.Content, StringComparison.Ordinal);

        // The WHOLE layout, which is the part the accidental healing above does not give.
        // TempDirectory is the one that stayed missing: it is a sibling of the work
        // directory, nothing on this path recreates it, and it is where TMPDIR points —
        // so a script asking Python for a temporary file was the next thing to break.
        Assert.True(Directory.Exists(workspace.WorkDirectory));
        Assert.True(Directory.Exists(workspace.EnvDirectory));
        Assert.True(Directory.Exists(workspace.StateDirectory));
        Assert.True(Directory.Exists(workspace.ShellStateDirectory));
        Assert.True(Directory.Exists(workspace.TempDirectory));
    }

    /// <summary>
    /// The files the conversation wrote are gone, and the model has to be told so once —
    /// otherwise it reads a run of unexplained missing files about files it watched itself
    /// create, which on a phone is a whole conversation's budget.
    /// </summary>
    [Fact]
    public void TheModelIsToldOnceThatItsFilesAreGone()
    {
        SessionWorkspaceManager manager = Workspaces();
        SessionWorkspace workspace = manager.GetOrCreate("purged-note");
        using var runner = new ShellRunner(Options(), backend: new InProcessShellBackend());

        Assert.True(runner.Run(new ShellRequest("echo one"), workspace).Ok);
        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult first = runner.Run(new ShellRequest("echo two"), workspace);
        Assert.True(first.Ok, first.Content);
        Assert.Contains("has been recreated", first.Content, StringComparison.Ordinal);

        CodeExecResult second = runner.Run(new ShellRequest("echo three"), workspace);
        Assert.True(second.Ok, second.Content);
        Assert.DoesNotContain("has been recreated", second.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell's own memory lives in the workspace too. A purge takes
    /// <c>state/shell</c>, and without it a <c>cd</c> stops surviving to the next call —
    /// so every relative path the model has been using silently starts meaning something
    /// else. It is the quietest of these failures and the hardest for a model to diagnose,
    /// because each command still succeeds.
    /// </summary>
    [Fact]
    public void ACdStillSurvivesToTheNextCallAfterAPurge()
    {
        SessionWorkspaceManager manager = Workspaces();
        SessionWorkspace workspace = manager.GetOrCreate("purged-cd");
        using var runner = new ShellRunner(Options(), backend: new InProcessShellBackend());

        Assert.True(runner.Run(new ShellRequest("mkdir -p deck && cd deck"), workspace).Ok);
        Assert.Contains("deck", runner.Run(new ShellRequest("pwd"), workspace).Content, StringComparison.Ordinal);

        Directory.Delete(workspace.Root, recursive: true);

        // The conversation carries on: make the directory again, step into it, and the
        // next call has to still be in it.
        Assert.True(runner.Run(new ShellRequest("mkdir -p deck && cd deck"), workspace).Ok);
        CodeExecResult where = runner.Run(new ShellRequest("pwd"), workspace);

        Assert.True(where.Ok, where.Content);
        Assert.Contains("deck", where.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// write_file and apply_patch are how a model rebuilds what a purge took, so neither
    /// may be the tool that fails because of the purge.
    /// </summary>
    [Fact]
    public void TheFileToolsSurviveAPurgeToo()
    {
        SessionWorkspaceManager manager = Workspaces();
        SessionWorkspace workspace = manager.GetOrCreate("purged-files");
        using var runner = new ShellRunner(Options(), backend: new InProcessShellBackend());

        Assert.True(runner.WriteFile(new ShellTools.WriteRequest("notes.md", "one\n"), workspace).Ok);
        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult written = runner.WriteFile(new ShellTools.WriteRequest("notes.md", "two\n"), workspace);
        Assert.True(written.Ok, written.Content);
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(workspace.WorkDirectory, "notes.md")));

        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult patched = runner.ApplyPatch(
            "*** Begin Patch\n*** Add File: deck.md\n+slide one\n*** End Patch\n", workspace);
        Assert.True(patched.Ok, patched.Content);
        Assert.True(File.Exists(Path.Combine(workspace.WorkDirectory, "deck.md")));
    }
}
