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
using System.Globalization;
using System.Text;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// <c>node</c>, as far as this host can honestly provide it: JavaScriptCore — the
/// system framework every Apple platform already ships — plus the Node-shaped
/// globals in <see cref="NodeHost"/>.
///
/// <para>
/// <b>Why this engine.</b> iOS forbids a child process and forbids generating
/// executable code, so a real <c>node</c> binary is out and so is any JIT-based
/// engine bundled as a library. JavaScriptCore is the one exception: it is part of
/// the OS, Apple permits it, and its C API — <c>JSContextGroupCreate</c>,
/// <c>JSEvaluateScript</c>, <c>JSObjectMakeFunctionWithCallback</c> — is plain
/// P/Invoke. The same framework sits at the same path on macOS, so every line of
/// this runs under <c>dotnet test</c> on a developer's machine rather than only on
/// a device, with <see cref="JsCore"/> settling the one VM option that makes the
/// two behave alike.
/// </para>
///
/// <para>
/// <b>What it is not.</b> This is not Node. There is no npm, no
/// <c>node_modules</c>, no <c>http</c> server, no worker threads, no streams. The
/// modules that exist are <c>fs</c>, <c>path</c> and <c>os</c>; everything else
/// fails at the <c>require</c> with a sentence saying so. That is the design, not
/// a gap to be filled quietly: a stub that returns an empty object costs a model
/// several turns to diagnose, while a named refusal costs it one.
/// </para>
///
/// <para>
/// <b>About stopping a script.</b> The documented C API (<c>JSContextRef.h</c>)
/// has no interrupt: nothing in it can stop <c>while (true) {}</c>, and an engine
/// built only on it would have to let a runaway script run until the process
/// ended. JavaScriptCore does export one more function —
/// <c>JSContextGroupSetExecutionTimeLimit</c>, declared in
/// <c>JSContextRefPrivate.h</c> but present in the shipping framework — which arms
/// a watchdog that terminates any single entry into the VM that overruns. This
/// engine probes for that symbol at run time and uses it when it is there, which
/// makes the timeout real; there is a test that proves an empty
/// <c>while (true) {}</c> comes back on the deadline. Whether running code
/// NOTICES the watchdog is a second question with a surprising answer — see
/// <c>JsCore.UsePollingTraps</c>, which is what keeps this working under a JIT.
/// When the symbol is missing the engine says the script is still running and
/// that it cannot preempt it, and does not pretend to have killed anything.
/// </para>
/// </summary>
public sealed class JavaScriptCoreEngine : IJavaScriptRuntime
{
    private static readonly Lazy<(bool Ok, string? Reason)> s_availability = new(Probe, isThreadSafe: true);

    /// <summary>How long past the deadline the caller waits before giving up on the JS thread.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A JavaScript stack can be deep, and JSC checks its own stack bound against
    /// the thread's; the default 1 MB leaves little room for host frames.
    /// </summary>
    private const int StackBytes = 8 * 1024 * 1024;

    public bool IsAvailable => s_availability.Value.Ok;

    public string? UnavailableReason => s_availability.Value.Reason;

    /// <summary>
    /// True when the VM watchdog is available, so a runaway script is genuinely
    /// stopped at the deadline rather than merely reported.
    /// </summary>
    public bool CanInterruptRunawayScripts => IsAvailable && JsCore.HasExecutionTimeLimit;

    /// <summary>Loads the framework once and says plainly why not, when not.</summary>
    private static (bool, string?) Probe()
    {
        try
        {
            JsCore.EnsureInitialized();
            IntPtr group = JsCore.JSContextGroupCreate();
            if (group == IntPtr.Zero)
                return (false, "JavaScriptCore loaded but JSContextGroupCreate returned null");
            JsCore.JSContextGroupRelease(group);
            return (true, null);
        }
        catch (DllNotFoundException)
        {
            return (false, $"JavaScriptCore is not available on this platform (looked for {JsCore.Library})");
        }
        catch (EntryPointNotFoundException ex)
        {
            return (false, "JavaScriptCore is present but is missing an entry point this engine needs: " + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "JavaScriptCore could not be initialised: " + ex.Message);
        }
    }

    public Task<ExecutionResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        ArgumentNullException.ThrowIfNull(context);

        if (Refuse(context, out ExecutionResult? refusal))
            return Task.FromResult(refusal!);

        string filename;
        string source;
        try
        {
            var paths = new ConfinedPaths(context.Policy);
            filename = paths.Resolve(scriptPath, context.WorkingDirectory, PathAccess.Read);
            if (!File.Exists(filename))
                return Task.FromResult(Failed(context, $"node: Cannot find module '{scriptPath}'", 1));
            source = File.ReadAllText(filename, Encoding.UTF8);
        }
        catch (ConfinementException ex)
        {
            return Task.FromResult(Failed(context, "node: " + ex.Message, 1));
        }
        catch (IOException ex)
        {
            return Task.FromResult(Failed(context, "node: " + ex.Message, 1));
        }

        var argv = new List<string> { "node", filename };
        // The shell hands the script's own spelling in as arguments[0]; Node's
        // argv[1] is that same file, already resolved, so it is not repeated.
        int skip = arguments.Count > 0 && SameFile(arguments[0], filename, context.WorkingDirectory) ? 1 : 0;
        for (int i = skip; i < arguments.Count; i++)
            argv.Add(arguments[i]);

        return RunAsync(source, filename, PosixPath.Dirname(filename), argv, context, cancellationToken);
    }

    public Task<ExecutionResult> RunCodeAsync(string source, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        if (Refuse(context, out ExecutionResult? refusal))
            return Task.FromResult(refusal!);

        // Node's `-e` names the program `[eval]` and does not put a file in argv.
        var argv = new List<string> { "node" };
        argv.AddRange(arguments);
        return RunAsync(source, "[eval]", context.WorkingDirectory, argv, context, cancellationToken);
    }

    /// <summary>
    /// Compiles without running, and reports <c>path:line: message</c> — the shape
    /// the shell's syntax check contract asks for and the shape a model can act on
    /// without reading the rest of the output.
    /// </summary>
    public Task<SyntaxCheckResult> CheckSyntaxAsync(string scriptPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        if (!IsAvailable)
            return Task.FromResult(new SyntaxCheckResult(false, UnavailableReason));

        return Task.Run(() =>
        {
            string source;
            try
            {
                source = File.ReadAllText(scriptPath, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                return new SyntaxCheckResult(false, $"{scriptPath}: {ex.Message}");
            }

            using var js = new JsContext();
            // Checked inside the CommonJS wrapper, as Node checks it, so that a
            // top-level `return` in a module is not reported as an error.
            bool ok = js.CheckSyntax(WrapForCheck(source), scriptPath, out JsErrorInfo? error);
            if (ok || error is null)
                return SyntaxCheckResult.Passed;
            int line = error.Line > 0 ? error.Line : 1;
            return new SyntaxCheckResult(false, $"{scriptPath}:{line.ToString(CultureInfo.InvariantCulture)}: {error.Message}");
        }, cancellationToken);
    }

    private static string WrapForCheck(string source)
        => "(function (exports, require, module, __filename, __dirname) {" + source + "\n});";

    // ---- running -----------------------------------------------------------------------

    private bool Refuse(InterpreterContext context, out ExecutionResult? result)
    {
        if (!IsAvailable)
        {
            result = Failed(context, "node: " + UnavailableReason, ExecutionResult.CommandNotFoundExitCode);
            return true;
        }
        if (!context.Policy.AllowScripts)
        {
            result = Failed(context, "node: " + ExecutionPolicy.ScriptsDisabledMessage, 126);
            return true;
        }
        result = null;
        return false;
    }

    private static ExecutionResult Failed(InterpreterContext context, string message, int exitCode)
        => ExecutionResult.Failed(message, context.WorkingDirectory, context.Environment, exitCode);

    private static bool SameFile(string candidate, string resolved, string cwd)
    {
        try
        {
            string full = Path.IsPathRooted(candidate) ? candidate : Path.Combine(cwd, candidate);
            return string.Equals(ConfinedPaths.RealPath(full), resolved, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record Outcome(int ExitCode, bool TimedOut, IReadOnlyDictionary<string, string> Environment);

    /// <summary>
    /// Runs the program on a thread of its own.
    ///
    /// <para>
    /// A dedicated thread, and not the thread pool, for two reasons: a JavaScript
    /// VM belongs to the thread that entered it, and if the deadline passes while
    /// the script is still inside JavaScript this thread is the one that gets
    /// abandoned. It is a background thread, so an abandoned one cannot keep the
    /// process alive.
    /// </para>
    /// </summary>
    private async Task<ExecutionResult> RunAsync(string source, string filename, string directory,
        IReadOnlyList<string> argv, InterpreterContext context, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var stdout = new OutputCapture(context.Policy.MaxOutputBytes, context.OnStdoutLine);
        var stderr = new OutputCapture(context.Policy.MaxOutputBytes, context.OnStderrLine);
        var completion = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Execute(source, filename, directory, argv, context, stdout, stderr));
            }
            catch (Exception ex)
            {
                stderr.Write("node: " + ex.Message + "\n");
                completion.TrySetResult(new Outcome(1, false, context.Environment));
            }
        }, StackBytes)
        {
            IsBackground = true,
            Name = "tensoragent-javascript",
        };
        thread.Start();

        TimeSpan timeout = context.EffectiveTimeout;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task expiry = Task.Delay(timeout + Grace, waiting.Token);
        Task first = await Task.WhenAny(completion.Task, expiry).ConfigureAwait(false);
        waiting.Cancel();

        if (first != completion.Task)
        {
            // A cancelled run is not a timed-out run; the caller asked for it to
            // stop and gets the exception it asked for. The JavaScript thread is a
            // background thread and ends at its own deadline.
            cancellationToken.ThrowIfCancellationRequested();

            // The watchdog should have ended this already. Reaching here means it
            // was unavailable, or the thread is inside a host call rather than
            // inside JavaScript. Either way it has NOT been stopped, and saying so
            // is the only honest report.
            stderr.Write(StillRunningMessage(timeout));
            stdout.Complete();
            stderr.Complete();
            return new ExecutionResult(ExecutionResult.TimeoutExitCode, stdout.Text, stderr.Text, true,
                context.WorkingDirectory, context.Environment, clock.Elapsed);
        }

        Outcome outcome = await completion.Task.ConfigureAwait(false);
        stdout.Complete();
        stderr.Complete();
        return new ExecutionResult(outcome.ExitCode, stdout.Text, stderr.Text, outcome.TimedOut,
            context.WorkingDirectory, outcome.Environment, clock.Elapsed);
    }

    private string StillRunningMessage(TimeSpan timeout)
        => $"node: the script is still running after {Seconds(timeout)} seconds and this engine cannot preempt it — "
           + "JavaScriptCore's public C API has no interrupt, and the VM watchdog is "
           + (JsCore.HasExecutionTimeLimit ? "armed but the thread is inside a host call" : "not available in this build")
           + ". The run was abandoned; it was not killed.\n";

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The whole of a run, on the JavaScript thread.</summary>
    private Outcome Execute(string source, string filename, string directory, IReadOnlyList<string> argv,
        InterpreterContext context, OutputCapture stdout, OutputCapture stderr)
    {
        using var js = new JsContext();
        var loop = new JsEventLoop(js, context.EffectiveTimeout);
        var host = new NodeHost(js, context, new ConfinedPaths(context.Policy), stdout, stderr, loop);
        try
        {
            host.Install(argv);
        }
        catch (Exception ex)
        {
            stderr.Write("node: " + ex.Message + "\n");
            return new Outcome(1, false, context.Environment);
        }

        int exitCode = 0;
        bool timedOut = false;

        js.ArmWatchdog(loop.RemainingMs / 1000.0);
        JsErrorInfo? error = host.RunMain(source, filename, directory);

        if (error is null)
        {
            // Node runs the loop only when the program itself came back cleanly;
            // an uncaught throw or an exit ends the process where it stands.
            loop.Run();
            if (loop.ExitRequested)
                exitCode = loop.ExitCode;
            else if (loop.Failure is not null)
                (exitCode, timedOut) = Report(loop.Failure, stderr, context.EffectiveTimeout);
            else if (loop.TimedOut)
            {
                timedOut = true;
                exitCode = ExecutionResult.TimeoutExitCode;
                stderr.Write($"node: the deadline of {Seconds(context.EffectiveTimeout)} seconds passed with timers or "
                             + "pending work still queued; the event loop was stopped.\n");
            }
        }
        else if (error.ExitCode is int code)
        {
            exitCode = code;
        }
        else
        {
            (exitCode, timedOut) = Report(error, stderr, context.EffectiveTimeout);
        }

        IReadOnlyDictionary<string, string> environment;
        try
        {
            environment = host.ReadEnvironment();
        }
        catch (Exception)
        {
            environment = context.Environment;
        }
        loop.Shutdown();
        return new Outcome(exitCode, timedOut, environment);
    }

    /// <summary>Prints an uncaught error the way Node does, and picks the exit status it implies.</summary>
    private (int ExitCode, bool TimedOut) Report(JsErrorInfo error, OutputCapture stderr, TimeSpan timeout)
    {
        if (error.Terminated)
        {
            stderr.Write($"node: the script ran past its {Seconds(timeout)} second limit and was stopped by "
                         + "JavaScriptCore's execution watchdog.\n");
            return (ExecutionResult.TimeoutExitCode, true);
        }
        stderr.Write(error.ToStderr());
        return (1, false);
    }
}
