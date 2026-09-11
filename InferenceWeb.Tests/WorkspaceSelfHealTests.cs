// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System;
using System.IO;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;

namespace InferenceWeb.Tests;

/// <summary>
/// A session workspace's directories are created once, when the workspace is
/// constructed, and every later call assumed they were still there. They are not always:
/// the directory lives beside the running binary, so an operator rebuilding into that
/// output directory, an OS temp reaper, or a stray cleanup removes it out from under a
/// live conversation. Recorded 2026-09-10: every <c>shell</c> call for the rest of a
/// twenty-minute conversation answered "the command could not be prepared: Could not
/// find a part of the path '.../state/cmd-1.sh'", including <c>echo hello</c>, and the
/// model spent the turn theorising about a host bug it could not act on.
/// </summary>
public class WorkspaceSelfHealTests : IDisposable
{
    private readonly string _base;
    private readonly SessionWorkspaceManager _workspaces;

    public WorkspaceSelfHealTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "ts-heal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        _workspaces = new SessionWorkspaceManager(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private CodeExecOptions Options() => new()
    {
        Enabled = true,
        Sandbox = SkillSandboxMode.Off,
        Timeout = TimeSpan.FromSeconds(30),
        ScratchDirectory = _base,
    };

    private static bool HavePosixShell =>
        ShellProgram.TryResolve(null, out ShellProgram? shell, out _) && shell is { Kind: ShellKind.Posix };

    [Fact]
    public void AWorkspaceWhoseDirectoriesWereDeleted_RunsTheNextCommandAnyway()
    {
        if (!HavePosixShell)
            return;

        SessionWorkspace workspace = _workspaces.GetOrCreate("wiped");
        using var runner = new ShellRunner(Options());

        Assert.True(runner.Run(new ShellRequest("echo before"), workspace).Ok);

        // What a rebuild into the host's output directory does.
        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult after = runner.Run(new ShellRequest("echo after"), workspace);

        Assert.True(after.Ok, after.Content);
        Assert.Contains("after", after.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repair is silent to the user and must NOT be silent to the model: everything
    /// it wrote earlier is gone, and without being told it reads a run of unexplained
    /// missing files about files it watched itself create.
    /// </summary>
    [Fact]
    public void TheResultSaysTheWorkingDirectoryWasRecreated_Once()
    {
        if (!HavePosixShell)
            return;

        SessionWorkspace workspace = _workspaces.GetOrCreate("noticed");
        using var runner = new ShellRunner(Options());

        Assert.True(runner.Run(new ShellRequest("echo one"), workspace).Ok);
        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult first = runner.Run(new ShellRequest("echo two"), workspace);
        Assert.True(first.Ok, first.Content);
        Assert.Contains("has been recreated", first.Content, StringComparison.Ordinal);

        // Nothing happened between these two, so saying it again would be a lie about a
        // second disappearance.
        CodeExecResult second = runner.Run(new ShellRequest("echo three"), workspace);
        Assert.True(second.Ok, second.Content);
        Assert.DoesNotContain("has been recreated", second.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// write_file and apply_patch are how a model rebuilds what a wiped workspace lost,
    /// so neither may be the tool that fails because the workspace was wiped.
    /// </summary>
    [Fact]
    public void TheFileToolsRebuildTheWorkspaceRatherThanFailingInIt()
    {
        SessionWorkspace workspace = _workspaces.GetOrCreate("filetools");
        using var runner = new ShellRunner(Options());

        Assert.True(runner.WriteFile(new ShellTools.WriteRequest("a.txt", "one\n"), workspace).Ok);
        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult written = runner.WriteFile(new ShellTools.WriteRequest("a.txt", "two\n"), workspace);
        Assert.True(written.Ok, written.Content);
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(workspace.WorkDirectory, "a.txt")));

        Directory.Delete(workspace.Root, recursive: true);

        CodeExecResult patched = runner.ApplyPatch(
            "*** Begin Patch\n*** Add File: b.txt\n+hello\n*** End Patch\n", workspace);
        Assert.True(patched.Ok, patched.Content);
        Assert.True(File.Exists(Path.Combine(workspace.WorkDirectory, "b.txt")));
    }

    /// <summary>
    /// The manager is the choke point every consumer already passes through, so the
    /// invariant holds for call sites nobody has written yet.
    /// </summary>
    [Fact]
    public void TheManagerHandsBackAWorkspaceWhoseDirectoriesExist()
    {
        SessionWorkspace first = _workspaces.GetOrCreate("choke");
        Directory.Delete(first.Root, recursive: true);

        SessionWorkspace again = _workspaces.GetOrCreate("choke");

        Assert.Same(first, again);
        Assert.True(Directory.Exists(again.WorkDirectory));
        Assert.True(Directory.Exists(again.StateDirectory));
        Assert.True(Directory.Exists(again.ShellStateDirectory));
        Assert.True(Directory.Exists(again.EnvDirectory));
        Assert.True(Directory.Exists(again.TempDirectory));
    }

    /// <summary>
    /// A workspace that was never damaged must not claim it was — the notice is a
    /// statement about the conversation's files, not a per-call decoration.
    /// </summary>
    [Fact]
    public void AnUndamagedWorkspaceNeverClaimsItWasRecreated()
    {
        if (!HavePosixShell)
            return;

        SessionWorkspace workspace = _workspaces.GetOrCreate("intact");
        using var runner = new ShellRunner(Options());

        Assert.False(workspace.EnsureDirectories());
        Assert.False(workspace.ConsumeRebuiltNotice());

        CodeExecResult result = runner.Run(new ShellRequest("echo intact"), workspace);
        Assert.True(result.Ok, result.Content);
        Assert.DoesNotContain("has been recreated", result.Content, StringComparison.Ordinal);
    }
}
