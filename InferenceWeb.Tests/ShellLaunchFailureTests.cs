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
/// What the model is told when the shell never STARTS.
///
/// <para>
/// A failing command and a shell that will not start reach the model as the same thing —
/// a refusal — and its correct answer to the first, rewrite the command, is the worst
/// possible answer to the second. Recorded 2026-09-10: eight launches failed before
/// executing anything, the model worked its way down to <c>echo hello</c>, and it spent
/// twenty minutes and every remaining round theorising about a host it could not see,
/// from an OS message quoting an absolute path to a file it had never written.
/// </para>
/// </summary>
public sealed class ShellLaunchFailureTests : IDisposable
{
    private readonly string _base;
    private readonly SessionWorkspaceManager _workspaces;

    public ShellLaunchFailureTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "ts-launchfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
        _workspaces = new SessionWorkspaceManager(Path.Combine(_base, "sessions"));
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
        Sandbox = SkillSandboxMode.Required,
        Timeout = TimeSpan.FromSeconds(30),
        ScratchDirectory = _base,
    };

    private static FakeShellBackend NeverStarts(string error) => new()
    {
        Sandbox = new InProcessSandbox(new SkillSandboxCapabilities(true, true, true, true)),
        Answer = _ => new ConfinedResult(
            false, false, -1, string.Empty, string.Empty, TimeSpan.Zero, "none", error),
    };

    [Fact]
    public void TheSecondLaunchThatNeverRan_SaysTheHostIsTheProblem_AndNamesWhatStillWorks()
    {
        FakeShellBackend backend = NeverStarts("the command could not be prepared: boom");
        using var runner = new ShellRunner(Options(), backend: backend);
        SessionWorkspace workspace = _workspaces.GetOrCreate("launchfail");

        CodeExecResult first = runner.Run(new ShellRequest("echo one"), workspace);
        Assert.False(first.Ok);
        Assert.DoesNotContain("failed before running anything", first.Content, StringComparison.Ordinal);

        CodeExecResult second = runner.Run(new ShellRequest("echo two"), workspace);
        Assert.False(second.Ok);
        Assert.Contains("failed before running anything", second.Content, StringComparison.Ordinal);

        // Naming what still works is the point: a result that only describes the problem
        // produces another round of the same command.
        Assert.Contains("write_file", second.Content, StringComparison.Ordinal);
        Assert.Contains("skills_run", second.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaunchThatStarts_ClearsTheRun()
    {
        var backend = new FakeShellBackend
        {
            Sandbox = new InProcessSandbox(new SkillSandboxCapabilities(true, true, true, true)),
        };
        bool fail = true;
        backend.Answer = launch => fail
            ? new ConfinedResult(false, false, -1, string.Empty, string.Empty, TimeSpan.Zero, "none", "boom")
            : FakeShellBackend.Ok("ran\n");

        using var runner = new ShellRunner(Options(), backend: backend);
        SessionWorkspace workspace = _workspaces.GetOrCreate("recovers");

        Assert.False(runner.Run(new ShellRequest("echo one"), workspace).Ok);

        fail = false;
        Assert.True(runner.Run(new ShellRequest("echo two"), workspace).Ok);

        // One failure after a recovery is a first failure again, not the second of a run.
        fail = true;
        CodeExecResult afterRecovery = runner.Run(new ShellRequest("echo three"), workspace);
        Assert.False(afterRecovery.Ok);
        Assert.DoesNotContain("failed before running anything", afterRecovery.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The absolute path is the part a model fixates on, and it names a file inside the
    /// host's own scratch that the model never wrote and cannot reach.
    /// </summary>
    [Fact]
    public void TheHostsOwnPathsAreScrubbedOutOfALaunchFailure()
    {
        SessionWorkspace workspace = _workspaces.GetOrCreate("scrubbed");
        string hostPath = Path.Combine(workspace.StateDirectory, "cmd-1.sh");

        FakeShellBackend backend = NeverStarts(
            $"the command could not be prepared: Could not find a part of the path '{hostPath}'.");
        using var runner = new ShellRunner(Options(), backend: backend);

        CodeExecResult result = runner.Run(new ShellRequest("echo one"), workspace);

        Assert.False(result.Ok);
        Assert.DoesNotContain(workspace.Root, result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.StateDirectory, result.Content, StringComparison.Ordinal);
    }
}
