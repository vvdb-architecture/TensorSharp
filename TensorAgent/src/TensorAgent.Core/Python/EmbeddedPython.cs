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
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Python;

/// <summary>
/// CPython 3.13, in this process, for a host that has no processes.
///
/// <para>
/// Everything the shell's <c>python3</c> would have delegated to a child happens
/// here instead: <c>sys.argv</c>, <c>__name__ == "__main__"</c>, a working
/// directory, a traceback on stderr and an exit code. What cannot be borrowed
/// from the operating system is rebuilt — the confinement is an audit hook
/// rather than a sandbox profile, the deadline is an interrupt rather than a
/// signal, and the output is captured inside Python rather than off a pipe.
/// Each of those has a cost, and each cost is stated where it is paid.
/// </para>
/// <para>
/// One interpreter serves the process and one run at a time uses it. That is
/// CPython's rule, not a simplification: the GIL makes concurrent runs a
/// fiction, and separate sub-interpreters would each need their own copy of the
/// bundled standard library's extension modules, which iOS will not load twice.
/// </para>
/// </summary>
public sealed class EmbeddedPython : IPythonRuntime
{
    /// <summary>
    /// How long a run gets to notice its interrupt before the caller stops
    /// waiting. Generous, because the alternative to waiting is reporting a
    /// timeout while the interpreter is still writing to the capture.
    /// </summary>
    private static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(5);

    private static readonly object s_gate = new();
    private static string? s_configuredRoot;
    private static IReadOnlyList<string> s_configuredPaths = [];

    private readonly SemaphoreSlim _runs = new(1, 1);
    private readonly string? _root;
    private readonly object _initGate = new();
    private PythonInterpreter? _interpreter;
    private PythonRuntimeLayout? _layout;
    private IReadOnlyList<BundledDistribution>? _bundled;
    private string? _reason;
    private bool _tried;
    private long _runId;

    /// <summary>
    /// Uses the process-wide root from <see cref="Configure"/>, or the one named
    /// here. An explicit root is what tests use to ask about a runtime that is
    /// deliberately absent.
    /// </summary>
    public EmbeddedPython(string? runtimeRoot = null)
    {
        _root = runtimeRoot;
        // Constructing with a root is the same promise as Configure: this is where
        // the app's own signed extension modules live, and the only tree the audit
        // hook will allow a dlopen from.
        if (!string.IsNullOrEmpty(runtimeRoot))
            PythonBootstrap.BundleRoot ??= runtimeRoot;
    }

    /// <summary>
    /// Names the staged runtime — the directory holding <c>python/</c> — before
    /// anything runs. The app calls this at startup with its bundle path.
    ///
    /// <para>
    /// It does not start the interpreter: that happens on first use, so an app
    /// that never runs a script never pays for CPython's startup. It does not
    /// validate either, for the same reason; <see cref="IsAvailable"/> is where
    /// the answer is, and it is an honest one.
    /// </para>
    /// </summary>
    /// <param name="runtimeRoot">The staged slice from <c>prepare-python.sh</c>.</param>
    /// <param name="extraModulePaths">
    /// Directories to put on <c>sys.path</c> at initialization — the session's
    /// package root, when the app already knows it. A package root that only
    /// exists later joins <c>sys.path</c> per run instead.
    /// </param>
    public static void Configure(string runtimeRoot, IEnumerable<string>? extraModulePaths = null)
    {
        lock (s_gate)
        {
            s_configuredRoot = runtimeRoot;
            s_configuredPaths = extraModulePaths?.ToArray() ?? [];
            // The audit hook lets a run dlopen only from inside this tree, because
            // every compiled extension module on iOS is a signed framework the
            // import machinery loads that way. Without it, `import numpy` is refused.
            PythonBootstrap.BundleRoot = runtimeRoot;
        }
    }

    /// <summary>The configured root, or null when the app never named one.</summary>
    public static string? ConfiguredRoot
    {
        get { lock (s_gate) return s_configuredRoot; }
    }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _interpreter is not null;
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            EnsureInitialized();
            return _reason;
        }
    }

    /// <inheritdoc />
    public string Version
    {
        get
        {
            EnsureInitialized();
            return _interpreter?.Version ?? string.Empty;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read from the staged package directory's <c>dist-info</c> entries, and read
    /// WITHOUT starting the interpreter: the answer is a directory listing, and asking
    /// for it must not be what pays CPython's startup on a launch that never runs a
    /// script. Computed once; the bundle does not change while the app runs.
    /// </remarks>
    public IReadOnlyList<BundledDistribution> BundledDistributions
    {
        get
        {
            IReadOnlyList<BundledDistribution>? bundled = Volatile.Read(ref _bundled);
            if (bundled is not null)
                return bundled;

            string? root = _root;
            lock (s_gate)
                root ??= s_configuredRoot;
            bundled = !string.IsNullOrWhiteSpace(root)
                && PythonRuntimeLayout.TryDiscover(root, out PythonRuntimeLayout? layout, out _)
                && layout is not null
                    ? BundledPackages.Scan(layout.Packages)
                    : Array.Empty<BundledDistribution>();
            Volatile.Write(ref _bundled, bundled);
            return bundled;
        }
    }

    /// <inheritdoc />
    public Task<ExecutionResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        ArgumentNullException.ThrowIfNull(context);
        // argv[0] is the script as it was typed, which is what the shell already
        // put at the front; sys.path[0] is the script's own directory, the way
        // `python script.py` resolves a sibling import.
        IReadOnlyList<string> argv = arguments is { Count: > 0 } ? arguments : [scriptPath];
        string directory = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? context.WorkingDirectory;
        return RunAsync("script", scriptPath, argv, [directory], context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionResult> RunCodeAsync(string source, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        var argv = new List<string> { "-c" };
        argv.AddRange(arguments ?? []);
        return RunAsync("code", source, argv, [context.WorkingDirectory], context, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionResult> RunModuleAsync(string module, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(context);
        // runpy replaces argv[0] with the module's file, exactly as `python -m`
        // does; the module name is what it starts from.
        var argv = new List<string> { module };
        argv.AddRange(arguments ?? []);
        return RunAsync("module", module, argv, [context.WorkingDirectory], context, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SyntaxCheckResult> CheckSyntaxAsync(string scriptPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        EnsureInitialized();
        if (_interpreter is null)
            return new SyntaxCheckResult(false, $"{scriptPath}: {_reason}");

        string source;
        try
        {
            source = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SyntaxCheckResult(false, $"{scriptPath}: {ex.Message}");
        }

        await _runs.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? message = await _interpreter.CheckSyntaxAsync(source, scriptPath).ConfigureAwait(false);
            return message is null ? SyntaxCheckResult.Passed : new SyntaxCheckResult(false, message);
        }
        finally
        {
            _runs.Release();
        }
    }

    private async Task<ExecutionResult> RunAsync(
        string mode,
        string target,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> pathFront,
        InterpreterContext context,
        CancellationToken cancellationToken)
    {
        ExecutionPolicy policy = context.Policy;
        if (!policy.AllowScripts)
            return ExecutionResult.Failed(ExecutionPolicy.ScriptsDisabledMessage, context.WorkingDirectory, context.Environment, 126);

        EnsureInitialized();
        if (_interpreter is null || _layout is null)
        {
            return ExecutionResult.Failed(
                _reason ?? "no Python interpreter is embedded in this build",
                context.WorkingDirectory,
                context.Environment,
                ExecutionResult.CommandNotFoundExitCode);
        }

        var confined = new ConfinedPaths(policy, _layout.ImportRoots());
        if (mode == "script" && !PythonBootstrap.IsPathAllowed(confined, target, context.WorkingDirectory, forWrite: false))
        {
            return ExecutionResult.Failed(
                PythonBootstrap.DeniedMessage(target, forWrite: false, confined.ReadableRoots),
                context.WorkingDirectory,
                context.Environment,
                2);
        }

        var stdout = new OutputCapture(policy.MaxOutputBytes, context.OnStdoutLine);
        var stderr = new OutputCapture(policy.MaxOutputBytes, context.OnStderrLine);
        var elapsed = Stopwatch.StartNew();
        // A deadline that has already passed still has to be a deadline the
        // timer accepts; interrupting at once is what a caller asking for zero
        // seconds meant.
        TimeSpan timeout = context.EffectiveTimeout > TimeSpan.Zero ? context.EffectiveTimeout : TimeSpan.FromMilliseconds(1);
        // CPython cannot observe the managed interrupt while blocked inside a socket
        // syscall. Bound one connect/read stall even when the overall tool is allowed to
        // do longer work; downloads that keep making progress are unaffected.
        TimeSpan networkTimeout = timeout < TimeSpan.FromSeconds(30)
            ? timeout
            : TimeSpan.FromSeconds(30);

        // Serialized here rather than inside the interpreter so a queued run
        // waits with its caller's cancellation token, and so a run that outlived
        // its deadline still owns the interpreter until it really ends.
        await _runs.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool released = false;
        try
        {
            IReadOnlyList<string> pathBack = ReadableImportPaths(context, confined);

            string payload = PythonInterpreter.CreatePayload(
                mode, target, argv, context.WorkingDirectory, context.Environment,
                pathFront, pathBack, _layout.Packages, networkTimeout, context.StandardInput);
            string policySource = PythonBootstrap.CreatePolicySource(confined, policy.AllowNetwork, policy.NetworkHosts);

            long runId = Interlocked.Increment(ref _runId);
            Task<int> run = _interpreter.RunAsync(runId, policySource, payload, stdout, stderr);

            bool interrupted = false;
            using var deadline = new Timer(
                _ =>
                {
                    interrupted = true;
                    _interpreter.Interrupt(runId);
                },
                null,
                timeout,
                Timeout.InfiniteTimeSpan);
            using CancellationTokenRegistration cancellation = cancellationToken.Register(() => _interpreter.Interrupt(runId));

            using var giveUp = new CancellationTokenSource();
            Task waited = await Task.WhenAny(run, Task.Delay(timeout + InterruptGrace, giveUp.Token)).ConfigureAwait(false);
            giveUp.Cancel();

            if (waited != run)
            {
                // The interrupt was queued and not taken: the run is inside
                // something that does not check for it. Nothing can end it, so
                // the semaphore stays with it and the next run queues behind.
                stderr.Write($"python: the run exceeded {timeout.TotalSeconds:0.#}s and did not stop when interrupted; "
                    + "it is still holding the interpreter\n");
                stdout.Complete();
                stderr.Complete();
                _ = run.ContinueWith(_ => _runs.Release(), TaskScheduler.Default);
                released = true;
                return new ExecutionResult(
                    ExecutionResult.TimeoutExitCode, stdout.Text, stderr.Text, true,
                    context.WorkingDirectory, context.Environment, elapsed.Elapsed);
            }

            int code = await run.ConfigureAwait(false);
            stdout.Complete();
            stderr.Complete();
            bool timedOut = interrupted && !cancellationToken.IsCancellationRequested;
            return new ExecutionResult(
                timedOut ? ExecutionResult.TimeoutExitCode : code,
                stdout.Text,
                stderr.Text,
                timedOut,
                context.WorkingDirectory,
                context.Environment,
                elapsed.Elapsed);
        }
        finally
        {
            if (!released)
                _runs.Release();
        }
    }

    /// <summary>
    /// Resolve the caller's Python import path through the same confinement used for
    /// file reads. CPython is initialized in isolated mode, so it intentionally ignores
    /// the process's real <c>PYTHONPATH</c>; the per-launch environment still has to be
    /// applied explicitly or packages unpacked into a session can never be imported.
    /// </summary>
    internal static IReadOnlyList<string> ReadableImportPaths(
        InterpreterContext context, ConfinedPaths confined)
    {
        var paths = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)
                || !confined.TryResolve(
                    path, context.WorkingDirectory, PathAccess.Read,
                    out string resolved, out _)
                || paths.Contains(resolved, StringComparer.Ordinal))
            {
                return;
            }
            paths.Add(resolved);
        }

        // PYTHONPATH is ordered. ShellRunner puts the session package directory first
        // and a skill runner may append the skill's own root. Empty entries are dropped
        // rather than interpreted as an extra current-directory grant.
        string pythonPath = context.EnvironmentValue("PYTHONPATH");
        foreach (string entry in pythonPath.Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(entry);
        }

        // Direct runtime callers predate the environment handoff and identify their
        // package tree on the policy. Keep that route as a fallback and deduplicate it
        // when ShellRunner supplied both forms.
        Add(context.Policy.PackageRoot);
        return paths;
    }

    /// <summary>
    /// Starts the interpreter once, and remembers exactly why it could not be
    /// started when it could not. There is no third answer: this never reports
    /// availability it has not established, because a shell that believes it has
    /// Python and does not is worse than one that says it has none.
    ///
    /// <para>
    /// The flag is published AFTER the work, not before it, and that ordering is the
    /// whole point. Setting it first made the fast path outside the lock a window into
    /// a half-started interpreter: a second caller arriving while <c>Py_Initialize</c>
    /// was still running saw "already tried" with no interpreter and no reason, and
    /// answered <see cref="IsAvailable"/> with false. On this app that window opens on
    /// EVERY launch — the page fetches <c>/api/agent/engine</c> as it loads, which asks
    /// for the version and starts CPython, while the startup self-test runs `python3` on
    /// its own thread — and it is not academic: it is a model being told
    /// "no Python interpreter is embedded in this build" by a build that has one, on the
    /// first command of a session, after which it stops reaching for the shell at all.
    /// Late callers now block on the lock and see the finished answer.
    /// </para>
    /// </summary>
    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _tried))
            return;
        lock (_initGate)
        {
            if (_tried)
                return;
            try
            {
                Initialize();
            }
            finally
            {
                // In a finally so a throwing initialization is still only attempted
                // once: an interpreter that failed to start does not start later, and
                // retrying it per command would spend seconds of a phone's battery
                // re-reaching the same answer.
                Volatile.Write(ref _tried, true);
            }
        }
    }

    private void Initialize()
    {
        string? root = _root;
        IReadOnlyList<string> extra;
        lock (s_gate)
        {
            root ??= s_configuredRoot;
            extra = s_configuredPaths;
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            _reason = "no embedded Python: EmbeddedPython.Configure(<staged runtime root>) was never called, "
                + "and this build ships no default";
            return;
        }

        if (!PythonRuntimeLayout.TryDiscover(root, out PythonRuntimeLayout? layout, out string? error) || layout is null)
        {
            _reason = error ?? $"no embedded Python under {root}";
            return;
        }

        PythonInterpreter? interpreter = PythonInterpreter.Acquire(layout, extra, out string? failure);
        if (interpreter is null)
        {
            _reason = failure ?? $"CPython under {layout.Root} could not be initialized";
            return;
        }

        _layout = layout;
        _interpreter = interpreter;
        _reason = null;
    }
}
