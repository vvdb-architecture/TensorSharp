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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TensorAgent.Core.JavaScript;
using TensorAgent.Core.Python;
using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Shell;

/// <summary>
/// Everything one shell launch needs from its caller. The same shape the AgentHost
/// seam will carry — cwd, environment, policy, output taps — plus the in-process
/// runtimes the shell dispatches <c>python</c>, <c>node</c> and <c>pip</c> to.
/// </summary>
public sealed class ShellContext
{
    public required string WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public required ExecutionPolicy Policy { get; init; }

    /// <summary>Overrides <see cref="ExecutionPolicy.DefaultTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    public Action<string>? OnStdoutLine { get; init; }

    public Action<string>? OnStderrLine { get; init; }

    /// <summary>Text on the shell's standard input; null means empty.</summary>
    public string? StandardInput { get; init; }

    /// <summary>Null means `python` reports that no interpreter is available on this host.</summary>
    public IPythonRuntime? Python { get; init; }

    /// <summary>Null means `node` reports that no engine is available on this host.</summary>
    public IJavaScriptRuntime? JavaScript { get; init; }

    /// <summary>Null means `pip install` is refused with the host-installs wording.</summary>
    public IInstallHook? Installer { get; init; }

    public TimeSpan EffectiveTimeout => Timeout ?? Policy.DefaultTimeout;
}

/// <summary>
/// A POSIX-style shell that runs entirely in this process.
///
/// <para>
/// On iOS there is no <c>/bin/sh</c> to hand a script to and no way to start one if
/// there were. What the model writes — <c>grep -rn foo . | head</c>,
/// <c>python3 x.py &amp;&amp; cat out.txt</c>, a here-document into a file — is
/// nonetheless a shell command line, and the cheapest faithful answer is a shell:
/// the same grammar (quotes, expansions, pipelines, redirections, here-documents,
/// <c>if</c>/<c>for</c>/<c>while</c>/<c>case</c>, functions) over builtin
/// implementations of the utilities the tool declaration teaches, with
/// <c>python</c>/<c>node</c> dispatched to the embedded interpreters.
/// </para>
/// <para>
/// Confinement is the policy's, applied on every path the shell touches: writes
/// only under the work and temp roots, reads under those plus the readable roots,
/// symlinks followed component by component before deciding. There is no other
/// sandbox on the host, so there is no other place this can live.
/// </para>
/// </summary>
public sealed class InProcessShell
{
    private readonly ILogger _logger;

    public InProcessShell(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The dialect name, as a `ShellProgram` would report it.</summary>
    public string DialectName => "sh";

    /// <summary>Every command the shell answers itself, for a declaration's "on this host" line.</summary>
    public static IReadOnlyCollection<string> BuiltinNames => ShellBuiltins.Names;

    /// <summary>Run a command line as <c>sh -c</c> would.</summary>
    public Task<ExecutionResult> RunCommandAsync(string command, ShellContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.Run(() => Run(command, null, context, cancellationToken), CancellationToken.None);
    }

    /// <summary>Run one command from an argument vector: no parsing, no expansion.</summary>
    public Task<ExecutionResult> RunArgvAsync(IReadOnlyList<string> argv, ShellContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(context);
        if (argv.Count == 0)
            throw new ArgumentException("argv must name a command", nameof(argv));
        return Task.Run(() => Run(null, argv, context, cancellationToken), CancellationToken.None);
    }

    private ExecutionResult Run(string? command, IReadOnlyList<string>? argv, ShellContext context, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        ExecutionPolicy policy = context.Policy;
        var paths = new ConfinedPaths(policy);

        var stdout = new OutputCapture(policy.MaxOutputBytes, context.OnStdoutLine);
        var stderr = new OutputCapture(policy.MaxOutputBytes, context.OnStderrLine);
        var state = ShellState.From(context);

        if (!paths.TryResolve(context.WorkingDirectory, policy.WorkRoot, PathAccess.Read, out string cwd, out _) || !Directory.Exists(cwd))
        {
            return ExecutionResult.Failed(
                $"sh: the working directory '{context.WorkingDirectory}' is not inside this session's workspace",
                context.WorkingDirectory, state.Snapshot());
        }
        state.Cwd = cwd;
        state.Set("PWD", cwd);

        TimeSpan timeout = context.EffectiveTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var streams = new ShellStreams(
            context.StandardInput == null ? Stream.Null : ShellText.FromString(context.StandardInput),
            new CaptureWriter(stdout),
            new CaptureWriter(stderr));

        var exec = new ShellExec(this, context, state, paths, cts.Token, _logger);
        int code;
        bool timedOut = false;
        try
        {
            if (argv != null)
            {
                code = exec.RunArgv(argv, streams);
            }
            else
            {
                CommandList list;
                try
                {
                    list = ShellParser.Parse(command ?? string.Empty);
                }
                catch (ShellSyntaxException ex)
                {
                    streams.Error("sh", ex.Message);
                    list = new CommandList();
                    code = 2;
                    goto done;
                }
                code = exec.RunList(list, streams);
            }
        }
        catch (ShellExitException ex)
        {
            code = ex.Code;
        }
        catch (ShellReturnException ex)
        {
            code = ex.Code;
        }
        catch (ShellBreakException)
        {
            code = 0;
        }
        catch (ShellContinueException)
        {
            code = 0;
        }
        catch (ShellFatalException ex)
        {
            streams.Error("sh", ex.Message);
            code = ex.Code;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            code = ExecutionResult.TimeoutExitCode;
            streams.Error("sh", $"the command timed out after {timeout.TotalSeconds:0.#} seconds");
        }
        catch (OperationCanceledException)
        {
            code = 130;
            streams.Error("sh", "the command was cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A filesystem failure that no builtin caught: report it the way a
            // utility would, rather than as a host exception.
            streams.Error("sh", ex.Message);
            code = 1;
        }

    done:
        stdout.Complete();
        stderr.Complete();
        return new ExecutionResult(code, stdout.Text, stderr.Text, timedOut, state.Cwd, state.Snapshot(), clock.Elapsed);
    }
}

// --- control flow ----------------------------------------------------------------------

internal sealed class ShellExitException : Exception
{
    public ShellExitException(int code) : base("exit") => Code = code;
    public int Code { get; }
}

internal sealed class ShellReturnException : Exception
{
    public ShellReturnException(int code) : base("return") => Code = code;
    public int Code { get; }
}

internal sealed class ShellBreakException : Exception
{
    public ShellBreakException(int levels) : base("break") => Levels = levels;
    public int Levels { get; set; }
}

internal sealed class ShellContinueException : Exception
{
    public ShellContinueException(int levels) : base("continue") => Levels = levels;
    public int Levels { get; set; }
}

/// <summary>An error that ends the whole script (a non-interactive shell exits on it).</summary>
internal sealed class ShellFatalException : Exception
{
    public ShellFatalException(string message, int code = 1) : base(message) => Code = code;
    public int Code { get; }
}

/// <summary>An expansion or redirection failed; the command it belonged to is not run.</summary>
internal sealed class ShellCommandException : Exception
{
    public ShellCommandException(string message, int code = 1) : base(message) => Code = code;
    public int Code { get; }
}

// --- state --------------------------------------------------------------------------------

/// <summary>Variables, functions, positional parameters, options and the working directory.</summary>
internal sealed class ShellState
{
    public string Cwd { get; set; } = "/";
    public Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Exported { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Arrays { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Command> Functions { get; } = new(StringComparer.Ordinal);
    public List<string> Positional { get; set; } = new();
    public string Arg0 { get; set; } = "sh";
    public int LastStatus { get; set; }
    public bool ErrExit { get; set; }
    public bool XTrace { get; set; }
    public bool NoUnset { get; set; }
    public bool PipeFail { get; set; }
    public bool NoGlob { get; set; }
    public Stack<Dictionary<string, (bool Existed, string? Value)>> LocalScopes { get; } = new();
    public DateTime Started { get; init; } = DateTime.UtcNow;

    /// <summary>Variables the host set for this launch; they are what the environment returns to it.</summary>
    private readonly HashSet<string> _hostVariables = new(StringComparer.Ordinal);

    public static ShellState From(ShellContext context)
    {
        var state = new ShellState { Cwd = context.WorkingDirectory };
        foreach (KeyValuePair<string, string> entry in context.Environment)
        {
            if (entry.Key.Length == 0)
                continue;
            state.Vars[entry.Key] = entry.Value;
            state.Exported.Add(entry.Key);
            state._hostVariables.Add(entry.Key);
        }
        if (!state.Vars.ContainsKey("HOME"))
        {
            state.Vars["HOME"] = context.Policy.WorkRoot;
            state.Exported.Add("HOME");
        }
        if (!state.Vars.ContainsKey("TMPDIR"))
        {
            state.Vars["TMPDIR"] = context.Policy.TempRoot;
            state.Exported.Add("TMPDIR");
        }
        if (!state.Vars.ContainsKey("PATH"))
        {
            state.Vars["PATH"] = "/usr/bin:/bin";
            state.Exported.Add("PATH");
        }
        state.Vars["IFS"] = " \t\n";
        state.Vars["PWD"] = context.WorkingDirectory;
        return state;
    }

    public string Get(string name)
        => Vars.TryGetValue(name, out string? v) ? v : string.Empty;

    public bool IsSet(string name) => Vars.ContainsKey(name);

    public void Set(string name, string value)
    {
        if (LocalScopes.Count > 0 && LocalScopes.Peek().ContainsKey(name))
        {
            Vars[name] = value;
            return;
        }
        Vars[name] = value;
    }

    public void Unset(string name)
    {
        Vars.Remove(name);
        Exported.Remove(name);
        Arrays.Remove(name);
    }

    /// <summary>Everything the script may have changed, for the caller to persist. Specials never travel.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in Vars)
        {
            if (entry.Key is "IFS" or "OLDPWD" or "_" or "RANDOM" or "SECONDS" or "LINENO")
                continue;
            if (Exported.Contains(entry.Key) || !_hostVariables.Contains(entry.Key))
                result[entry.Key] = entry.Value;
        }
        return result;
    }

    /// <summary>What an interpreter or a nested shell inherits: exported variables only.</summary>
    public Dictionary<string, string> ExportedEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in Exported)
        {
            if (Vars.TryGetValue(name, out string? value))
                env[name] = value;
        }
        env["PWD"] = Cwd;
        return env;
    }

    public ShellState Clone()
    {
        var copy = new ShellState { Cwd = Cwd, Arg0 = Arg0, LastStatus = LastStatus, ErrExit = ErrExit, XTrace = XTrace, NoUnset = NoUnset, PipeFail = PipeFail, NoGlob = NoGlob, Started = Started };
        foreach (KeyValuePair<string, string> v in Vars)
            copy.Vars[v.Key] = v.Value;
        foreach (string e in Exported)
            copy.Exported.Add(e);
        foreach (KeyValuePair<string, List<string>> a in Arrays)
            copy.Arrays[a.Key] = new List<string>(a.Value);
        foreach (KeyValuePair<string, Command> f in Functions)
            copy.Functions[f.Key] = f.Value;
        copy.Positional = new List<string>(Positional);
        foreach (string h in _hostVariables)
            copy._hostVariables.Add(h);
        return copy;
    }
}
