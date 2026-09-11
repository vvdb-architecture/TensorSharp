// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorAgent.Core.Sandbox;

namespace TensorAgent.Core.Python;

/// <summary>
/// A Python interpreter the shell can hand a script to. The shape is what
/// <c>python3 [script|-c code|-m module] args</c> would have been as a process.
/// </summary>
public interface IPythonRuntime
{
    /// <summary>False when the interpreter could not be loaded; <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>The interpreter's version, e.g. <c>3.13.2</c>; empty when unavailable.</summary>
    string Version { get; }

    /// <summary>
    /// The distributions staged into the app at build time, so an installer can answer
    /// for them without a network and <c>pip list</c> can show them. Empty when the
    /// runtime has no staged package directory, which is the default for a runtime
    /// that is not the embedded one.
    /// </summary>
    IReadOnlyList<BundledDistribution> BundledDistributions => Array.Empty<BundledDistribution>();

    Task<ExecutionResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken);

    Task<ExecutionResult> RunCodeAsync(string source, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken);

    Task<ExecutionResult> RunModuleAsync(string module, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken);

    /// <summary>The <c>SyntaxCheck</c> contract: compile without running.</summary>
    Task<SyntaxCheckResult> CheckSyntaxAsync(string scriptPath, CancellationToken cancellationToken);
}
