// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Diagnostics;
using TensorAgent.Core.JavaScript;
using TensorAgent.Core.Python;
using TensorAgent.Core.Sandbox;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;

namespace TensorAgent.Core.Shell;

/// <summary>
/// The agent host's execution backend for a host that cannot start a process.
///
/// <para>
/// Everything above the <see cref="IShellBackend"/> seam — reading the model's
/// arguments, pulling installs out of the command line, building the environment,
/// the attempt ledger, the artifact capture, every sentence of the result the model
/// reads — is shared with the desktop. This class supplies only the bottom half:
/// where <see cref="ProcessShellBackend"/> writes a wrapper script and execs a real
/// <c>bash</c> under Seatbelt or bubblewrap, this one hands the command to
/// <see cref="InProcessShell"/> and lets it interpret the command itself.
/// </para>
/// <para>
/// The confinement story changes shape but not strength. There is no OS sandbox to
/// wrap anything in, because there is nothing to wrap; instead every path a builtin
/// touches is resolved through <see cref="ConfinedPaths"/> and every network command
/// consults <see cref="ExecutionPolicy.AllowNetwork"/> before it opens a socket. That
/// is reported honestly through <see cref="Sandbox"/>: writes, network and home reads
/// are confined, and the process tree is bounded for the only reason that matters
/// here, which is that there is no process tree at all.
/// </para>
/// <para>
/// One thing genuinely is weaker, and the capability flags must not paper over it. A
/// confined child process can be killed; an in-process interpreter can only be asked
/// to stop at its next checkpoint. A builtin that blocks inside a single long call
/// (a very large file read, a regular expression with a pathological input) runs past
/// its deadline, and the run reports a timeout while the work is still unwinding. The
/// interpreters cannot be preempted either. See <see cref="InProcessShellJob.Kill"/>.
/// </para>
/// </summary>
public sealed class InProcessShellBackend : IShellBackend
{
    private readonly InProcessShell _shell;
    private readonly IPythonRuntime? _python;
    private readonly IJavaScriptRuntime? _javaScript;
    private readonly IInstallHook? _installer;

    private readonly ISkillSandbox _sandbox;

    /// <param name="python">The embedded Python, or null when this build has none.</param>
    /// <param name="javaScript">The embedded JavaScript engine, or null when this build has none.</param>
    /// <param name="installer">The host's package installer, or null to refuse installs with a message.</param>
    public InProcessShellBackend(
        IPythonRuntime? python = null,
        IJavaScriptRuntime? javaScript = null,
        IInstallHook? installer = null,
        IReadOnlyList<string>? networkHosts = null)
    {
        _python = python;
        _javaScript = javaScript;
        _installer = installer;
        NetworkHosts = networkHosts ?? Array.Empty<string>();
        _shell = new InProcessShell();

        // Every one of these is enforced by ConfinedPaths and ExecutionPolicy inside
        // the interpreter rather than by the kernel, which is why the description says
        // so: an operator reading the startup line should know what is holding the
        // line, not merely that something is.
        _sandbox = new InProcessSandbox(
            new SkillSandboxCapabilities(
                ConfinesWrites: true,
                ConfinesNetwork: true,
                ConfinesHomeReads: true,
                BoundsProcessTree: true),
            "in-process interpreter: writes and reads are confined by path resolution, "
            + "the network is refused unless the user allows it, and nothing can spawn a "
            + "process because this platform has none to spawn");
    }

    /// <summary>
    /// The hosts a run may reach when the network is on, or empty for any host.
    ///
    /// <para>
    /// It belongs to the backend rather than to the launch because the agent host's
    /// <c>ShellLaunch</c> has no field for it: the desktop expresses the same idea
    /// with an OS sandbox rule, and there is no sandbox here to express it in. Without
    /// somewhere for it to come from, every runtime's allow-list check reduced to
    /// "any host", which is a check that reads as enforcement and is not.
    /// </para>
    /// <para>
    /// Settable because the user owns it: it is read from the settings file at startup
    /// and again whenever the settings change, so a change takes effect on the next
    /// command rather than on the next launch.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> NetworkHosts { get; set; }

    /// <inheritdoc />
    public string Name => "in-process";

    /// <inheritdoc />
    public ShellProgram? Shell { get; } = ShellProgram.InProcess("sh", ShellKind.Posix);

    /// <inheritdoc />
    public ISkillSandbox? Sandbox => _sandbox;

    /// <inheritdoc />
    public bool CanRun => true;

    /// <inheritdoc />
    public string? UnavailableReason => null;

    /// <summary>
    /// What this backend can and cannot run, for the startup banner and for the
    /// <c>/api/engine</c> probe. The interpreters are optional, and a host that is
    /// missing one has to say which rather than letting a model discover it by
    /// writing a script that fails.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string> { "sh (in-process)" };
        parts.Add(_python is { IsAvailable: true } py ? $"python {py.Version}" : "no python");
        parts.Add(_javaScript is { IsAvailable: true } ? "node (JavaScriptCore)" : "no node");
        parts.Add(_installer is { CanInstall: true } ? "installs enabled" : "no installs");
        return string.Join(", ", parts);
    }

    /// <inheritdoc />
    public bool TryStart(ShellLaunch launch, out IShellJob? job, out ConfinedResult failure)
    {
        ArgumentNullException.ThrowIfNull(launch);

        string? invalid = launch.Validate();
        if (invalid is not null)
        {
            job = null;
            failure = Failed(invalid);
            return false;
        }

        ExecutionPolicy policy = PolicyFor(launch);
        // $TMPDIR has to be a directory that exists before the first command runs;
        // the desktop backend gets one from the session workspace, and here it is a
        // child of the write root so it inherits the same confinement.
        try { Directory.CreateDirectory(policy.TempRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            job = null;
            failure = Failed($"the temporary directory could not be created: {policy.TempRoot}");
            return false;
        }

        string start = launch.CallWorkDirectory ?? launch.WorkingDirectory;
        if (!Directory.Exists(start))
        {
            job = null;
            failure = Failed($"the working directory does not exist: {start}");
            return false;
        }

        job = new InProcessShellJob(_shell, launch, policy, start, _python, _javaScript, _installer);
        failure = default;
        return true;
    }

    /// <summary>
    /// Translate the launch's permissions into the interpreter's policy. The two
    /// describe the same thing in different words, and the mapping is the whole
    /// contract: what the host decided a run may touch is exactly what the
    /// interpreter will let it touch.
    /// </summary>
    private ExecutionPolicy PolicyFor(ShellLaunch launch)
    {
        var readable = new List<string> { launch.ReadOnlyDirectory };
        readable.AddRange(launch.ReadablePaths);

        return new ExecutionPolicy(
            AllowScripts: true,
            AllowNetwork: launch.AllowNetwork,
            WorkRoot: launch.WriteDirectory,
            ReadableRoots: readable,
            TempRoot: Path.Combine(launch.WriteDirectory, ".tmp"))
        {
            WritablePaths = launch.WritablePaths,
            NetworkHosts = NetworkHosts,
            MaxOutputBytes = launch.MaxOutputBytes,
            DefaultTimeout = launch.Timeout,
            AllowLoopbackPort = launch.AllowLoopbackPort,
        };
    }

    private static ConfinedResult Failed(string message)
        => new(false, false, 0, string.Empty, message.EndsWith('\n') ? message : message + "\n",
               TimeSpan.Zero, "in-process", message);
}

/// <summary>
/// One in-process run, started and not yet waited for.
///
/// <para>
/// It is a <see cref="Task"/> rather than a process, so <see cref="ProcessId"/> is
/// -1 and <see cref="Kill"/> cancels rather than signals. The host holds these the
/// same way it holds a background child process, and cancelling on session teardown
/// is what stops a run that would otherwise keep interpreting.
/// </para>
/// </summary>
internal sealed class InProcessShellJob : IShellJob
{
    private readonly Task<ExecutionResult> _run;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ShellLaunch _launch;
    private readonly string _startDirectory;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal InProcessShellJob(
        InProcessShell shell, ShellLaunch launch, ExecutionPolicy policy, string start,
        IPythonRuntime? python, IJavaScriptRuntime? javaScript, IInstallHook? installer)
    {
        _launch = launch;
        _startDirectory = start;

        var context = new ShellContext
        {
            WorkingDirectory = start,
            Policy = policy,
            Environment = Inherit(launch),
            OnStdoutLine = launch.OnOutputLine,
            OnStderrLine = launch.OnOutputLine,
            Python = python,
            JavaScript = javaScript,
            Installer = installer,
        };

        _run = launch.Argv is { Count: > 0 } argv
            ? Task.Run(() => shell.RunArgvAsync(argv, context, _cancel.Token), _cancel.Token)
            : Task.Run(() => shell.RunCommandAsync(launch.Command!, context, _cancel.Token), _cancel.Token);
    }

    /// <summary>
    /// What the run starts with: the session's saved exports, then the variables this
    /// launch sets on top.
    ///
    /// <para>
    /// This is the read half of the persistence the desktop does with a wrapper
    /// script — its first line sources the saved environment file. Without it a
    /// command's <c>export</c> is written at the end of one call and never seen by the
    /// next, so <c>source .venv/bin/activate</c> would appear to succeed and then do
    /// nothing. The launch's own variables win, because those are the ones the host
    /// set deliberately for this call.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> Inherit(ShellLaunch launch)
    {
        if (launch.Session is not { } session || launch.Command is null)
            return launch.Environment;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (KeyValuePair<string, string> saved in session.Load().Environment)
                environment[saved.Key] = saved.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable state file costs this call its inherited exports, which the
            // next export will restore; it must not cost the user the command.
        }
        foreach (KeyValuePair<string, string> set in launch.Environment)
            environment[set.Key] = set.Value;
        return environment;
    }

    public int ProcessId => -1;

    public string SandboxName => "in-process";

    public bool HasExited => _run.IsCompleted;

    public ConfinedResult WaitForExit(TimeSpan timeout)
    {
        bool finished;
        try
        {
            finished = _run.Wait(timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)timeout.TotalMilliseconds);
        }
        catch (AggregateException ex)
        {
            return Faulted(ex.GetBaseException());
        }

        if (!finished)
        {
            // The interpreter checks for cancellation between commands and inside its
            // own loops, so this usually stops it promptly. What it cannot stop is a
            // single builtin call already inside the runtime, which is why the result
            // is reported as a timeout the moment the deadline passes rather than
            // waiting for the run to actually unwind.
            Kill();
            return new ConfinedResult(true, true, ExecutionResult.TimeoutExitCode,
                string.Empty, $"the command was still running after {timeout.TotalSeconds:0.#}s and was stopped\n",
                _clock.Elapsed, "in-process", null);
        }

        ExecutionResult result;
        try
        {
            result = _run.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Faulted(ex);
        }

        Persist(result);

        return new ConfinedResult(
            Started: true,
            TimedOut: result.TimedOut,
            ExitCode: result.ExitCode,
            Stdout: result.Stdout,
            Stderr: result.Stderr,
            Elapsed: result.Elapsed,
            SandboxName: "in-process",
            Error: null);
    }

    /// <summary>
    /// Write back what a wrapper script's EXIT trap would have written: where the
    /// session resumes and what it exported. A per-call working directory is not
    /// persisted, so <c>workdir</c> moves one command and not the conversation.
    /// </summary>
    private void Persist(ExecutionResult result)
    {
        if (_launch.Session is not { } session || _launch.Command is null)
            return;

        // A per-call directory moves this command only, so the session resumes from
        // where it was before the move — which is the launch's own working directory,
        // never the one this call was redirected into.
        string resume = _launch.CallWorkDirectory is null
            ? Respell(result.WorkingDirectory)
            : _launch.WorkingDirectory;
        try
        {
            session.Save(resume, result.Environment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the saved state costs the next command its inherited directory,
            // which is recoverable; failing the command the user just ran because the
            // bookkeeping could not be written is not.
        }
    }

    /// <summary>
    /// Put a resolved path back into the spelling the caller used.
    ///
    /// <para>
    /// The interpreter resolves every path through its links, so on macOS a working
    /// directory under the temp root comes back as <c>/private/var/…</c> while the
    /// session was handed <c>/var/…</c>. A real shell does not do this — bash keeps
    /// the logical <c>$PWD</c> it was given — and the session's own validation checks
    /// the saved directory against the root's spelling, so a resolved path is read
    /// back as "outside the workspace" and silently discarded. Mapping it back is
    /// what makes <c>cd</c> persist.
    /// </para>
    /// </summary>
    private string Respell(string resolved)
    {
        string spelled = _launch.WriteDirectory;
        string real = ConfinedPaths.RealPath(spelled);
        if (string.Equals(resolved, real, StringComparison.Ordinal))
            return spelled;
        string prefix = real.EndsWith(Path.DirectorySeparatorChar) ? real : real + Path.DirectorySeparatorChar;
        return resolved.StartsWith(prefix, StringComparison.Ordinal)
            ? Path.Combine(spelled, resolved[prefix.Length..])
            : resolved;
    }

    private ConfinedResult Faulted(Exception ex)
        => new(true, false, 1, string.Empty, ex.Message.EndsWith('\n') ? ex.Message : ex.Message + "\n",
               _clock.Elapsed, "in-process", ex.Message);

    public void Kill()
    {
        try { _cancel.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Kill();
        _cancel.Dispose();
    }
}
