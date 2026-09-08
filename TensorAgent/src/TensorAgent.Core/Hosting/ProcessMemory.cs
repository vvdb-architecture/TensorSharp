// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System;
using System.Runtime.InteropServices;

namespace TensorAgent.Core.Hosting;

/// <summary>
/// The memory numbers a jetsam kill is decided on, read the way the kernel reads them.
///
/// <para>
/// Two numbers, and the app used to watch neither. <c>phys_footprint</c> is what this
/// process is charged: its anonymous memory, its compressed pages, its share of
/// device buffers. It does NOT include the model weights: on Apple platforms the
/// weights are a file mapping that Metal wires while the model is loaded, and wired
/// file pages are charged to the machine, not to the process. So a 5 GB model shows
/// up as ~0 in the footprint and as +5 GB in the system's wired total, which is why
/// every jetsam report from the phone showed TensorAgent at 3 GB and the device with
/// 8-10 GB of its 12 GB wired. Measured on the Mac, loading Qwen3.5-9B IQ4_XS: the
/// footprint rose to 1.9 GB and the wired total by 6.8 GB.
/// </para>
///
/// <para>
/// Both are read here, from the same calls on iOS and macOS, so a log line from a
/// bench on the Mac and one from the phone say the same things in the same units.
/// </para>
/// </summary>
public static class ProcessMemory
{
    /// <summary>One reading. Sizes in bytes; a field is -1 where the platform has no answer.</summary>
    public readonly record struct Snapshot(
        long Footprint,
        long SystemWired,
        long SystemFree,
        long SystemCompressor,
        long ProcessAvailable)
    {
        private static string Mb(long bytes) => bytes < 0 ? "n/a" : (bytes / (1024 * 1024)).ToString() + " MB";

        /// <summary>The line the log carries.</summary>
        public override string ToString() =>
            $"footprint {Mb(Footprint)}; system wired {Mb(SystemWired)}, free {Mb(SystemFree)}, compressor {Mb(SystemCompressor)}"
            + (ProcessAvailable >= 0 ? $"; this process may still take {Mb(ProcessAvailable)}" : string.Empty);
    }

    // task_info(TASK_VM_INFO): phys_footprint sits at byte 144 of task_vm_info_data_t and
    // is the LAST field of the REV1 layout, 152 bytes / 38 words. Asking for the REV0
    // count (36 words) stops one field short of it and reads back zero -- checked
    // against the SDK headers with offsetof(); the kernel fills up to the count asked
    // for, and every supported system has REV1.
    private const int TaskVmInfo = 22;
    private const int TaskVmInfoRev1Bytes = 152;
    private const int PhysFootprintOffset = 144;

    // host_statistics64(HOST_VM_INFO64): vm_statistics64 has free_count at 0, active at 4,
    // inactive at 8, wire_count at 12 (all natural_t) and compressor_page_count at 128.
    // The full struct is 248 bytes; asking for 152 (38 words) is a valid partial count
    // -- the kernel fills what fits, and compressor_page_count is within it. Checked
    // against the SDK header with offsetof() and a live call.
    private const int HostVmInfo64 = 4;
    private const int HostVmInfo64Bytes = 152;
    private const int FreeCountOffset = 0;
    private const int WireCountOffset = 12;
    private const int CompressorPageCountOffset = 128;

    [DllImport("libSystem.dylib")]
    private static extern int task_info(uint target, int flavor, byte[] info, ref int count);

    [DllImport("libSystem.dylib")]
    private static extern uint mach_task_self();

    [DllImport("libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, byte[] info, ref int count);

    // iOS 13+: the per-process jetsam budget minus the footprint. Absent (or 0) on the
    // simulator and on macOS; either reads as "unknown", never as "none left".
    [DllImport("libSystem.dylib")]
    private static extern nint os_proc_available_memory();

    /// <summary>Whether this platform can answer at all (Apple systems only).</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

    /// <summary>Read the numbers now. Never throws: a probe that cannot answer returns -1 fields.</summary>
    public static Snapshot Read()
    {
        if (!IsSupported)
            return new Snapshot(-1, -1, -1, -1, -1);

        long footprint = -1, wired = -1, free = -1, compressor = -1, available = -1;
        try
        {
            var vm = new byte[TaskVmInfoRev1Bytes];
            int count = vm.Length / 4;
            if (task_info(mach_task_self(), TaskVmInfo, vm, ref count) == 0)
                footprint = BitConverter.ToInt64(vm, PhysFootprintOffset);
        }
        catch (Exception) { /* a missing export answers "unknown" */ }

        try
        {
            var st = new byte[HostVmInfo64Bytes];
            int count = st.Length / 4;
            if (host_statistics64(mach_host_self(), HostVmInfo64, st, ref count) == 0)
            {
                long page = Environment.SystemPageSize;
                free = BitConverter.ToUInt32(st, FreeCountOffset) * page;
                wired = BitConverter.ToUInt32(st, WireCountOffset) * page;
                compressor = BitConverter.ToUInt32(st, CompressorPageCountOffset) * page;
            }
        }
        catch (Exception) { /* likewise */ }

        try
        {
            long reported = os_proc_available_memory();
            if (reported > 0)
                available = reported;
        }
        catch (Exception) { /* not every system exports it */ }

        return new Snapshot(footprint, wired, free, compressor, available);
    }

    /// <summary>The one-line form of <see cref="Read"/>, for a log.</summary>
    public static string Describe() => Read().ToString();
}
