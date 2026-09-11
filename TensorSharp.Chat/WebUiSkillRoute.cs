// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.Generic;

namespace TensorSharp.Chat
{
    /// <summary>One skill-script execution a routed artifact must be backed by.</summary>
    /// <param name="SkillId">The exact selected skill id.</param>
    /// <param name="ResourcePath">The exact bundled script path.</param>
    /// <param name="ProducesArtifact">
    /// True when candidate artifacts must come from this invocation, rather than from
    /// an unrelated shell or file tool in the same turn.
    /// </param>
    /// <param name="DefaultArguments">
    /// A host-owned argument vector to supply only when the model selected this exact
    /// script but omitted <c>args</c>. This turns a recoverable formatting omission into
    /// the intended call without overriding arguments the model actually chose.
    /// </param>
    /// <param name="EnforceArguments">
    /// True when the host owns this narrow workflow's complete command contract. In
    /// that case a model-supplied argument vector is replaced with
    /// <paramref name="DefaultArguments"/>. This is intentionally opt-in: ordinary
    /// skill routes must continue to preserve arguments chosen by the model or user.
    /// </param>
    /// <param name="RequiredInputPath">
    /// Optional workspace-relative file that must already exist and contain at least one
    /// byte before this exact routed script may run. Null preserves ordinary skill-run
    /// behaviour. This is a route-owned sequencing guard, not a general script policy.
    /// </param>
    public sealed record WebUiSkillRunRequirement(
        string SkillId,
        string ResourcePath,
        bool ProducesArtifact = false,
        IReadOnlyList<string> DefaultArguments = null,
        bool EnforceArguments = false,
        string RequiredInputPath = null);

    /// <summary>Observable evidence a host-routed artifact must contain.</summary>
    /// <param name="CitationEvidencePath">
    /// Optional workspace-relative text file whose researched URLs must overlap a URL
    /// visible in the deliverable. This binds a claimed research-backed artifact to
    /// this turn's evidence instead of accepting any unrelated citation.
    /// </param>
    public sealed record WebUiArtifactRequirement(
        string Extension,
        IReadOnlyList<WebUiSkillRunRequirement> RequiredRuns = null,
        int MinimumSlides = 1,
        IReadOnlyList<string> RequiredVisibleTerms = null,
        bool RequireVisibleHttpUrl = false,
        string CitationEvidencePath = null);

    /// <summary>
    /// A host's deterministic refinement of an otherwise unselected Web UI skill request.
    /// </summary>
    /// <param name="Skills">The skill ids to pass to the ordinary request planner.</param>
    /// <param name="Instructions">
    /// A compact, request-specific activation directive. Skill bodies remain behind
    /// <c>skills_read</c>; this is only enough text to ensure that a small model opens
    /// the right ones before acting.
    /// </param>
    /// <param name="ArtifactRequirement">
    /// Optional evidence contract for hosts that own an end-to-end route. The Web UI
    /// attaches it only when it can verify files in a persistent workspace; ordinary
    /// inferred skill routes leave it null and retain the normal streaming loop.
    /// </param>
    /// <param name="RequiresNetwork">
    /// True when the routed workflow cannot succeed with the skill-script network
    /// sandbox closed. This is a preflight requirement, never permission to open the
    /// network: the host must still have enabled network access explicitly.
    /// </param>
    public sealed record WebUiSkillRoute(
        IReadOnlyList<string> Skills,
        string Instructions,
        WebUiArtifactRequirement ArtifactRequirement = null,
        bool RequiresNetwork = false);
}
