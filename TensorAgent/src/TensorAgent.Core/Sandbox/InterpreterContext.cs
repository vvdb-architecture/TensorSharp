// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Core.Sandbox;

/// <summary>
/// Everything an interpreter run inherits from its caller — the same things a child
/// process would have inherited, minus the process.
/// </summary>
/// <param name="WorkingDirectory">Where relative paths resolve; must lie inside the policy's readable roots.</param>
/// <param name="Environment">The complete environment the program sees (<c>PYTHONPATH</c>, <c>NODE_PATH</c>, <c>HOME</c>, <c>TMPDIR</c>...).</param>
/// <param name="Policy">What the program may do.</param>
public sealed record InterpreterContext(
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    ExecutionPolicy Policy)
{
    /// <summary>Overrides <see cref="ExecutionPolicy.DefaultTimeout"/> for this run.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Called with each complete stdout line as it is produced.</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>Called with each complete stderr line as it is produced.</summary>
    public Action<string>? OnStderrLine { get; init; }

    /// <summary>What the program reads from standard input; null means empty.</summary>
    public string? StandardInput { get; init; }

    public TimeSpan EffectiveTimeout => Timeout ?? Policy.DefaultTimeout;

    public string EnvironmentValue(string name)
        => Environment.TryGetValue(name, out string? value) ? value : string.Empty;

    /// <summary>
    /// The entries of a PATH-like variable (<c>PYTHONPATH</c>, <c>NODE_PATH</c>) as
    /// absolute directories, in order, empties dropped.
    /// </summary>
    public IReadOnlyList<string> PathEntries(string variable)
    {
        string value = EnvironmentValue(variable);
        if (value.Length == 0)
            return Array.Empty<string>();
        var entries = new List<string>();
        foreach (string entry in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            entries.Add(Path.IsPathRooted(entry) ? entry : Path.GetFullPath(Path.Combine(WorkingDirectory, entry)));
        return entries;
    }
}

/// <summary>
/// A package installation the host performs on the model's behalf. The shell's
/// <c>pip install</c> hands the names here rather than running anything — the
/// AgentHost rule that installs are read from the command line and performed by the
/// host, kept on a host that has no pip at all.
/// </summary>
/// <param name="Language">"python" or "node".</param>
/// <param name="Packages">Validated names, optionally with <c>==version</c>.</param>
/// <param name="TargetDirectory">Where the packages are unpacked (the session's package root).</param>
/// <param name="Policy">Decides whether the network is available for the download.</param>
public sealed record InstallRequest(
    string Language,
    IReadOnlyList<string> Packages,
    string TargetDirectory,
    ExecutionPolicy Policy)
{
    public Action<string>? OnOutputLine { get; init; }
}

/// <summary>The host's installer, supplied to the shell. Null means installs are refused with a message.</summary>
public interface IInstallHook
{
    /// <summary>False when this host cannot install anything; <see cref="UnavailableReason"/> says why.</summary>
    bool CanInstall { get; }

    string? UnavailableReason { get; }

    Task<ExecutionResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken);
}
