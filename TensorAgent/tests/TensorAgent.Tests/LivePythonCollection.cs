// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Tests;

/// <summary>
/// The one at-a-time queue for every test class that starts the embedded CPython.
///
/// <para>
/// There is exactly ONE interpreter per process — CPython cannot be re-initialized
/// the way an app would need, so <c>PythonInterpreter</c> keeps a single instance
/// and refuses a second runtime root — and exactly one sandbox policy inside it,
/// because an audit hook cannot be uninstalled and therefore reads a dict the
/// current run replaces the contents of.
/// </para>
/// <para>
/// xUnit runs test classes in parallel, so without this three classes start it at
/// once: the first wins the root and the others are refused, and worse, a run whose
/// policy has just been overwritten by another class's run is denied its own working
/// directory. That produced twenty-one failures whose messages named the wrong
/// test's temporary directory — a machine that had staged an interpreter looked
/// broken, while a machine that had not passed the whole suite. Naming the same
/// collection puts them in one queue, which is what the interpreter already is.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LivePythonCollection
{
    public const string Name = "live python";
}
