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
using System.Collections.Generic;
using TensorSharp.AgentHost.CodeExec;

namespace TensorSharp.AgentHost.Skills
{
    /// <summary>
    /// The confinement a host that runs code INSIDE its own process presents to the
    /// rest of the agent host.
    ///
    /// <para>
    /// It wraps nothing and attaches to nothing — there is no child process to wrap.
    /// What it carries is the answer to the one question everything above the launch
    /// seam asks: what is actually enforced. <see cref="ShellRunner.CanRun"/>, the
    /// declaration's network promise, the result's "Not confined on this host" line and
    /// the skill runner's refusal under <see cref="SkillSandboxMode.Required"/> all read
    /// <see cref="Capabilities"/>, and an in-process backend that enforces workspace
    /// containment on every path and has disabled sockets in its interpreters is
    /// trusted exactly as far as the capabilities it hands this constructor.
    /// </para>
    /// <para>
    /// That is the whole design: the host is not asked to run with sandboxing off — an
    /// operator statement that means "I accept unconfined commands on my machine" — it
    /// is asked to say, truthfully, what its runtime confines. A host that claims
    /// <c>ConfinesNetwork</c> without having closed the network is the one lie this
    /// object cannot detect, which is why the capabilities are the caller's to assert
    /// and the constructor does not default them.
    /// </para>
    /// </summary>
    public sealed class InProcessSandbox : ISkillSandbox
    {
        private readonly SkillSandboxCapabilities _capabilities;
        private readonly string _description;

        /// <param name="capabilities">What the in-process runtime actually enforces. Asserted by the host, never assumed.</param>
        /// <param name="description">
        /// One line saying HOW, for the startup log and <c>--list-skills</c>. Null gets a
        /// generic line naming the mechanism.
        /// </param>
        public InProcessSandbox(SkillSandboxCapabilities capabilities, string? description = null)
        {
            _capabilities = capabilities;
            _description = description
                ?? "runs code inside the host's own process; what is confined is what the host's "
                 + "runtime enforces on its own paths and sockets, as its capabilities state";
        }

        /// <inheritdoc/>
        public string Name => "in-process";

        /// <inheritdoc/>
        public bool IsAvailable => true;

        /// <inheritdoc/>
        public SkillSandboxCapabilities Capabilities => _capabilities;

        /// <inheritdoc/>
        public string Describe() => _description;

        /// <summary>Nothing to attach to: the run is this process.</summary>
        public bool TryAttach(SpawnedProcess process, out string error)
        {
            error = null!;
            return true;
        }

        /// <summary>
        /// Nothing to rewrite: the request is handed back unchanged, the way the Windows
        /// job object does. An in-process backend never launches what comes out of this
        /// anyway; the object exists to be asked what it confines.
        /// </summary>
        public bool TryWrap(
            SkillSandboxRequest request,
            out string fileName,
            out IReadOnlyList<string> arguments,
            out IDisposable cleanup,
            out string error)
        {
            fileName = request.Interpreter;
            arguments = request.Arguments;
            cleanup = null!;
            error = null!;
            return true;
        }
    }
}
