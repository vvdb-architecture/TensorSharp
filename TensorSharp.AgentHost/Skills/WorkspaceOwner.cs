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
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TensorSharp.AgentHost.Skills
{
    /// <summary>
    /// Which process a session workspace belongs to, written into the workspace itself.
    ///
    /// <para>
    /// The scratch root is shared. It defaults to a folder beside the binary, so every
    /// host launched from one build writes its sessions into the same directory, and the
    /// startup sweep — whose whole justification is that a restart orphans every session
    /// — was deleting the live working directories of hosts that were still running. A
    /// stamp turns "everything here is finished business" into a question that can
    /// actually be answered.
    /// </para>
    /// <para>
    /// A pid alone is not an answer, because pids are reused: a workspace left by a host
    /// that died last week can name a pid that some unrelated process holds today, and
    /// the sweep would then keep that workspace for as long as the machine stayed up.
    /// The process's START TIME is what makes the pid identify one process rather than a
    /// number, and it is the same pairing the OS itself uses.
    /// </para>
    /// <para>
    /// It sits at the workspace ROOT rather than inside <c>state/</c> because the model's
    /// commands may write inside <c>state/shell</c>, and because a wiped workspace must be
    /// re-stamped by the repair that rebuilds it — which only sees the root.
    /// </para>
    /// </summary>
    internal static class WorkspaceOwner
    {
        /// <summary>Dot-prefixed and outside every directory the sandbox mounts writable.</summary>
        private const string FileName = ".owner";

        /// <summary>Record this process as the owner of <paramref name="workspaceRoot"/>.</summary>
        public static void Stamp(string workspaceRoot)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(workspaceRoot, FileName),
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                        + "\n" + StartTicks(Process.GetCurrentProcess()).ToString(CultureInfo.InvariantCulture)
                        + "\n");
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                // Deliberately everything non-fatal. A stamp is an optimisation — it saves
                // one live workspace from another host's sweep — and this runs from the
                // SessionWorkspace CONSTRUCTOR, so a throw here would not fail the stamp,
                // it would fail code execution outright for the whole conversation. The
                // interesting case is a platform that refuses process introspection (iOS
                // does, and it is the platform this ships to): asking it about the current
                // process must cost nothing. A workspace that could not be stamped is
                // swept like any other orphan, which is the behaviour that existed before
                // stamps did.
            }
        }

        /// <summary>
        /// Whether <paramref name="workspaceRoot"/> is owned by a process that is still
        /// running. False for an unreadable, missing or malformed stamp, so anything a
        /// previous version of this host left behind is still swept.
        /// </summary>
        public static bool IsHeldByALiveProcess(string workspaceRoot)
        {
            string path = Path.Combine(workspaceRoot, FileName);
            string[] lines;
            try
            {
                if (!File.Exists(path))
                    return false;
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                return false;
            }

            if (lines.Length < 2
                || !int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                || !long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
            {
                return false;
            }

            // Nothing here is short-circuited on "that is our own pid". A dead host's
            // workspace can name the pid this process now holds, and answering "alive"
            // from the number alone would keep that workspace for as long as the machine
            // stays up. The start time is what separates the two, and it is no less needed
            // for our own id than for anyone else's — so a stamp without one proves
            // nothing.
            if (ticks == 0)
                return false;

            // EVERY uncertain answer below is "not held", which is the behaviour that
            // existed before stamps did: the sweep deletes it. That direction is the
            // important one. A platform that does not allow process introspection at all
            // — iOS refuses it, and it is also the platform whose scratch space the system
            // reclaims — would otherwise answer "cannot tell, so keep it" for every
            // workspace ever created and accumulate all of them in a cache directory on a
            // device that is short of space. Skipping a sweep is only ever justified by
            // POSITIVE proof that another live process owns the directory, which is
            // exactly the case this exists for and exactly the case where the proof is
            // available.
            try
            {
                using Process owner = Process.GetProcessById(pid);
                if (owner.HasExited)
                    return false;

                long actual = StartTicks(owner);
                if (actual == 0)
                    return false;

                // Whole seconds. The stamp and the later reading come from different
                // sources of the same value and the sub-second part is not stable across
                // them on every platform.
                return Math.Abs(actual - ticks) < TimeSpan.TicksPerSecond;
            }
            catch (ArgumentException)
            {
                // No process with that id: the classic "the owner is gone" answer.
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
            {
                // Exited between the two calls, not inspectable by this user, or not
                // supported by this platform. None of them is proof of a live owner.
                return false;
            }
        }

        private static long StartTicks(Process process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                          or System.ComponentModel.Win32Exception) { return 0; }
        }
    }
}
