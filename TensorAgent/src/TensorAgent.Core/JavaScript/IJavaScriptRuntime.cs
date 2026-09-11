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

namespace TensorAgent.Core.JavaScript;

/// <summary>
/// A JavaScript engine the shell can hand a script to, shaped like
/// <c>node [script|-e code] args</c>.
/// </summary>
public interface IJavaScriptRuntime
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<ExecutionResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken);

    Task<ExecutionResult> RunCodeAsync(string source, IReadOnlyList<string> arguments, InterpreterContext context, CancellationToken cancellationToken);

    Task<SyntaxCheckResult> CheckSyntaxAsync(string scriptPath, CancellationToken cancellationToken);
}
