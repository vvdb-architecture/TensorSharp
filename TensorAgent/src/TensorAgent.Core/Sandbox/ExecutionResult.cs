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
/// What one launch produced. The shape mirrors a child process — exit code, the two
/// output streams, whether the deadline cut it short — plus the two things a process
/// would have left behind in the wrapper script's state files: where it ended up and
/// what it exported. The caller persists those; the runtimes never write state files.
/// </summary>
public sealed record ExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Elapsed)
{
    /// <summary>The exit status the shell's <c>timeout</c> utility uses for a deadline, so a model reads a familiar number.</summary>
    public const int TimeoutExitCode = 124;

    /// <summary>The exit status for a name the shell could not find.</summary>
    public const int CommandNotFoundExitCode = 127;

    /// <summary>Success without a timeout.</summary>
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>A result for a launch that could not start; the reason goes to stderr as a command would say it.</summary>
    public static ExecutionResult Failed(string message, string workingDirectory, IReadOnlyDictionary<string, string> environment, int exitCode = 1)
        => new(exitCode, string.Empty, message.EndsWith('\n') ? message : message + "\n", false, workingDirectory, environment, TimeSpan.Zero);
}

/// <summary>What a syntax check found. <see cref="Message"/> is phrased for the model: <c>path:line: what</c>.</summary>
public sealed record SyntaxCheckResult(bool Ok, string? Message)
{
    public static readonly SyntaxCheckResult Passed = new(true, null);
}
