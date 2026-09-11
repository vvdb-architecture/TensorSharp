// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using Metal;
using TensorSharp.GGML;
using TensorSharp.Runtime;

namespace TensorAgent.Maui;

/// <summary>
/// The engine backend this process will use, decided exactly once.
/// </summary>
/// <param name="Backend">The TensorSharp backend to load models on.</param>
/// <param name="MetalCompiledIn">Whether the linked GgmlOps archive contains ggml-metal at all
/// (false for the simulator slice, which build-ios.sh builds CPU-only).</param>
/// <param name="GpuSupportsApple7">Whether the system Metal device advertises MTLGPUFamilyApple7,
/// the floor for ggml-metal's simdgroup_matrix kernels.</param>
/// <param name="GpuName">The Metal device name, or null when there is no Metal device.</param>
/// <param name="Reason">One sentence saying why this backend was picked, for the status bar and logs.</param>
public sealed record ComputeSelection(
    BackendType Backend,
    bool MetalCompiledIn,
    bool GpuSupportsApple7,
    string? GpuName,
    string Reason);

/// <summary>
/// Picks the GGML backend for this process.
/// </summary>
/// <remarks>
/// GGML latches one backend per process: the first real initialisation
/// (<c>ensure_backend</c> in ggml_ops_core.cpp) creates the singleton and every
/// later request for a different backend fails. So the choice is made up front
/// from side-effect-free probes and never by "try Metal, fall back to CPU":
/// <see cref="GgmlBasicOps.CanInitializeBackend"/> is a compile-flag check that
/// creates no MTLDevice, and the Metal family query goes through UIKit's own
/// device object, which GGML does not see.
/// </remarks>
public static class Compute
{
    private static readonly Lazy<ComputeSelection> s_selection =
        new(Select, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The backend for this process; computed on first access and then fixed.</summary>
    public static ComputeSelection Selection => s_selection.Value;

    private static ComputeSelection Select()
    {
        bool metalCompiledIn = GgmlBasicOps.CanInitializeBackend(GgmlBackendType.Metal);

        // MTLDevice.SystemDefault is the same device ggml-metal would open later,
        // but this handle is independent of GGML's backend slot.
        IMTLDevice? device = MTLDevice.SystemDefault;
        string? gpuName = device?.Name;
        bool apple7 = device is not null && device.SupportsFamily(MTLGpuFamily.Apple7);

        if (metalCompiledIn && apple7)
        {
            return new ComputeSelection(
                BackendType.GgmlMetal,
                MetalCompiledIn: true,
                GpuSupportsApple7: true,
                gpuName,
                $"ggml-metal on {gpuName} (MTLGPUFamilyApple7 present).");
        }

        string reason;
        if (!metalCompiledIn)
        {
            // The simulator slice of GgmlOps.xcframework is built with Metal OFF
            // because the simulator GPU only advertises Apple1/Apple2; on a device
            // this branch means the xcframework was built without the Metal
            // backend, which is a build problem rather than a hardware one.
            reason = "ggml-cpu: the linked GgmlOps archive has no ggml-metal backend (simulator slice or Metal-less build).";
        }
        else if (device is null)
        {
            reason = "ggml-cpu: no Metal device is available on this system.";
        }
        else
        {
            reason = $"ggml-cpu: {gpuName} does not support MTLGPUFamilyApple7, which ggml-metal's simdgroup kernels need.";
        }

        return new ComputeSelection(BackendType.GgmlCpu, metalCompiledIn, apple7, gpuName, reason);
    }
}
