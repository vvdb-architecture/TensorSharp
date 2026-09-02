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
/// The one at-a-time queue for every test class that loads real weights.
///
/// <para>
/// xUnit runs test classes in parallel by default, which for these means two hosts
/// each loading eight gigabytes onto the same GPU at the same time. Beyond being
/// slower than running them in turn, it makes the device-allocation measurements in
/// <see cref="MetalLifetimeTests"/> meaningless: that number is per process, so a
/// model another class loaded in between two of its readings looks exactly like a
/// leak. Naming the same collection puts them in one queue.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveModelCollection
{
    public const string Name = "live model";
}
