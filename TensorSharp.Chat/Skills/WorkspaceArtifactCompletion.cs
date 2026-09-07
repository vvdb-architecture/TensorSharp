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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Chat;

namespace TensorSharp.Server.Skills
{
    /// <summary>
    /// Resolves the immutable file actually served by a captured artifact URL. The
    /// resolver is supplied by the host that owns that URL; the completion loop never
    /// guesses where a URL maps on disk.
    /// </summary>
    internal delegate bool CapturedArtifactPathResolver(
        SkillProducedFile artifact,
        out string fullPath,
        out string error);

    internal readonly record struct WorkspaceArtifactBaseline(long Bytes, byte[] Digest);

    internal readonly record struct WorkspaceSkillRunRequirement(
        string SkillId,
        string ResourcePath,
        bool ProducesArtifact,
        IReadOnlyList<string> DefaultArguments,
        bool EnforceArguments,
        string RequiredInputPath);

    /// <summary>
    /// One host-owned deliverable that must exist before the model's answer is final.
    /// Deliberately supports only formats with a structural validator; treating an
    /// arbitrary extension as proof would turn <c>touch report.pptx</c> into success.
    /// </summary>
    internal sealed class WorkspaceArtifactCompletionRequirement
    {
        private const long MaxBaselineFileBytes = 32L * 1024 * 1024;
        private const long MaxBaselineTotalBytes = 64L * 1024 * 1024;
        private const int MaxBaselineFiles = 64;
        private const int MaxRequiredRuns = 16;
        private const int MaxRequiredTerms = 16;
        private const int MaxRequiredSlides = 512;
        private static readonly StringComparer WorkspacePathComparer =
            SkillPathGuard.PathComparison == StringComparison.Ordinal
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;

        private WorkspaceArtifactCompletionRequirement(
            string extension,
            CapturedArtifactPathResolver resolveArtifact,
            IReadOnlyDictionary<string, WorkspaceArtifactBaseline> baselines,
            IReadOnlyList<WorkspaceSkillRunRequirement> requiredRuns,
            int minimumSlides,
            IReadOnlyList<string> requiredVisibleTerms,
            bool requireVisibleHttpUrl,
            SessionWorkspace workspace,
            string citationEvidencePath)
        {
            Extension = extension;
            ResolveArtifact = resolveArtifact;
            Baselines = baselines;
            RequiredRuns = requiredRuns;
            MinimumSlides = minimumSlides;
            RequiredVisibleTerms = requiredVisibleTerms;
            RequireVisibleHttpUrl = requireVisibleHttpUrl;
            Workspace = workspace;
            CitationEvidencePath = citationEvidencePath;
        }

        internal string Extension { get; }

        internal CapturedArtifactPathResolver ResolveArtifact { get; }

        internal IReadOnlyList<WorkspaceSkillRunRequirement> RequiredRuns { get; }

        internal int MinimumSlides { get; }

        internal IReadOnlyList<string> RequiredVisibleTerms { get; }

        internal bool RequireVisibleHttpUrl { get; }

        internal SessionWorkspace Workspace { get; }

        internal string CitationEvidencePath { get; }

        /// <summary>
        /// Content fingerprints of decks that predated this request. Shell capture is
        /// intentionally modification-time based, so <c>touch old.pptx</c> is reported
        /// as output even though it did not make today's report. A bounded, route-only
        /// fingerprint closes that gap without adding hashing to ordinary code runs.
        /// A null digest means the path existed but was outside the fingerprint budget;
        /// a same-size capture of it consequently cannot prove current-turn work.
        /// </summary>
        internal IReadOnlyDictionary<string, WorkspaceArtifactBaseline> Baselines { get; }

        internal static bool TryCreate(
            string extension,
            SessionWorkspace workspace,
            CodeArtifactStore artifactStore,
            string artifactUriPrefix,
            out WorkspaceArtifactCompletionRequirement requirement)
            => TryCreate(
                new WebUiArtifactRequirement(extension),
                workspace,
                artifactStore,
                artifactUriPrefix,
                out requirement);

        internal static bool TryCreate(
            WebUiArtifactRequirement policy,
            SessionWorkspace workspace,
            CodeArtifactStore artifactStore,
            string artifactUriPrefix,
            out WorkspaceArtifactCompletionRequirement requirement)
        {
            requirement = null;
            if (policy == null
                || string.IsNullOrWhiteSpace(policy.Extension)
                || workspace == null
                || artifactStore == null
                || string.IsNullOrWhiteSpace(artifactUriPrefix)
                || policy.MinimumSlides < 1
                || policy.MinimumSlides > MaxRequiredSlides)
            {
                return false;
            }

            string normalized = policy.Extension.Trim();
            if (normalized[0] != '.')
                normalized = "." + normalized;

            if (!string.Equals(normalized, ".pptx", StringComparison.OrdinalIgnoreCase))
                return false;

            string prefix = artifactUriPrefix.TrimEnd('/');
            if (prefix.Length == 0)
                return false;

            IReadOnlyList<WebUiSkillRunRequirement> requestedRuns =
                policy.RequiredRuns ?? Array.Empty<WebUiSkillRunRequirement>();
            if (requestedRuns.Count > MaxRequiredRuns)
                return false;
            var requiredRuns = new List<WorkspaceSkillRunRequirement>(requestedRuns.Count);
            foreach (WebUiSkillRunRequirement run in requestedRuns)
            {
                string skill = run?.SkillId?.Trim();
                string path = run?.ResourcePath?.Replace('\\', '/').Trim();
                if (string.IsNullOrWhiteSpace(skill) || skill.Length > 128
                    || string.IsNullOrWhiteSpace(path) || path.Length > 512
                    || path[0] == '/' || path.Split('/').Any(segment =>
                        segment.Length == 0
                        || string.Equals(segment, ".", StringComparison.Ordinal)
                        || string.Equals(segment, "..", StringComparison.Ordinal)))
                {
                    return false;
                }

                IReadOnlyList<string> requestedArguments =
                    run.DefaultArguments ?? Array.Empty<string>();
                if (requestedArguments.Count > 64
                    || requestedArguments.Any(argument => argument == null || argument.Length > 4096))
                {
                    return false;
                }

                string requiredInputPath = run.RequiredInputPath?.Replace('\\', '/').Trim();
                if (!string.IsNullOrEmpty(requiredInputPath)
                    && (requiredInputPath.Length > 512
                        || Path.IsPathRooted(requiredInputPath)
                        || requiredInputPath.Split('/').Any(segment =>
                            segment.Length == 0
                            || string.Equals(segment, ".", StringComparison.Ordinal)
                            || string.Equals(segment, "..", StringComparison.Ordinal))
                        || !workspace.TryResolve(requiredInputPath, out _, out _)))
                {
                    return false;
                }
                requiredRuns.Add(new WorkspaceSkillRunRequirement(
                    skill,
                    path,
                    run.ProducesArtifact,
                    requestedArguments.ToArray(),
                    run.EnforceArguments,
                    requiredInputPath));
            }

            string citationEvidencePath = policy.CitationEvidencePath?.Replace('\\', '/').Trim();
            if (!string.IsNullOrEmpty(citationEvidencePath)
                && (!workspace.TryResolve(citationEvidencePath, out _, out _)
                    || Path.IsPathRooted(citationEvidencePath)))
            {
                return false;
            }

            IReadOnlyList<string> requestedTerms = policy.RequiredVisibleTerms ?? Array.Empty<string>();
            if (requestedTerms.Count > MaxRequiredTerms)
                return false;
            var requiredTerms = new List<string>(requestedTerms.Count);
            foreach (string requestedTerm in requestedTerms)
            {
                string term = requestedTerm?.Trim();
                if (string.IsNullOrEmpty(term) || term.Length > 128)
                    return false;
                if (!requiredTerms.Contains(term, StringComparer.OrdinalIgnoreCase))
                    requiredTerms.Add(term);
            }

            CapturedArtifactPathResolver resolver =
                delegate (SkillProducedFile artifact, out string fullPath, out string error)
                {
                    return WorkspaceArtifactCompletion.TryResolveCodeArtifact(
                        artifactStore, prefix, artifact, out fullPath, out error);
                };

            requirement = new WorkspaceArtifactCompletionRequirement(
                ".pptx",
                resolver,
                SnapshotExisting(workspace, ".pptx"),
                requiredRuns,
                policy.MinimumSlides,
                requiredTerms,
                policy.RequireVisibleHttpUrl,
                workspace,
                citationEvidencePath);
            return true;
        }

        private static IReadOnlyDictionary<string, WorkspaceArtifactBaseline> SnapshotExisting(
            SessionWorkspace workspace,
            string extension)
        {
            var baselines = new Dictionary<string, WorkspaceArtifactBaseline>(WorkspacePathComparer);
            Dictionary<string, (long Length, DateTime WriteTime)> snapshot;
            using (workspace.BeginOperation())
                snapshot = workspace.SnapshotWorkFiles();

            long hashedBytes = 0;
            int hashedFiles = 0;
            foreach (KeyValuePair<string, (long Length, DateTime WriteTime)> item in
                snapshot.Where(item => HasExtension(item.Key, extension))
                    .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                string relative = item.Key.Replace('\\', '/');
                long bytes = item.Value.Length;
                byte[] digest = null;

                bool withinBudget = bytes >= 0
                    && bytes <= MaxBaselineFileBytes
                    && hashedFiles < MaxBaselineFiles
                    && hashedBytes <= MaxBaselineTotalBytes - bytes;
                if (withinBudget
                    && workspace.TryResolve(relative, out string fullPath, out _)
                    && File.Exists(fullPath))
                {
                    try
                    {
                        using var stream = new FileStream(
                            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 81920, FileOptions.SequentialScan);
                        if (stream.Length == bytes)
                        {
                            digest = SHA256.HashData(stream);
                            hashedBytes += bytes;
                            hashedFiles++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                  or CryptographicException)
                    {
                        // Presence still matters. A same-size future capture of a file
                        // we could not fingerprint is not accepted as newly produced.
                    }
                }

                baselines[relative] = new WorkspaceArtifactBaseline(bytes, digest);
            }

            return baselines;
        }

        private static bool HasExtension(string name, string extension)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(name)
                    && string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }

    /// <summary>The result of checking the current turn's captured artifacts.</summary>
    internal readonly record struct WorkspaceArtifactCompletionResult(
        bool Complete,
        SkillProducedFile? Artifact,
        string Reason);

    /// <summary>
    /// Verifies a deliverable structurally and ties it to this turn's tool trace.
    ///
    /// <para>
    /// A workspace scan alone is intentionally insufficient: session workspaces persist,
    /// so a deck from yesterday must not satisfy today's request. Conversely, trusting a
    /// reported filename or ZIP magic alone accepts the exact failure this guard exists
    /// for — an empty/partial file with a plausible extension. A candidate therefore has
    /// to appear in <see cref="SkillRequestPlan.Invocations"/>, resolve through the exact
    /// durable URL the host captured for it, match that immutable copy's byte count, differ
    /// from a same-path pre-request deck, and pass the format's package checks.
    /// </para>
    /// </summary>
    internal static class WorkspaceArtifactCompletion
    {
        private const int MaxPackageEntries = 4096;
        private const int MaxSlides = 512;
        private const int MaxRelationships = 4096;
        private const long MaxXmlPartBytes = 4L * 1024 * 1024;
        private const long MaxParsedXmlBytes = 32L * 1024 * 1024;
        private const int MaxVisibleTextChars = 4 * 1024 * 1024;
        private const int MaxCitationEvidenceCharacters = 4 * 1024 * 1024;
        private static readonly Regex HttpUrlPattern = new(
            @"https?://[^\s<>""')\]}]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private const string ContentTypesNamespace =
            "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string RelationshipsNamespace =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string PresentationNamespace =
            "http://schemas.openxmlformats.org/presentationml/2006/main";
        private const string DrawingNamespace =
            "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string OfficeRelationshipsNamespace =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string OfficeDocumentRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        private const string SlideRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
        private const string SlideMasterRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster";
        private const string SlideLayoutRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout";
        private const string ThemeRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme";
        private const string PresentationPropertiesRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps";
        private const string ImageRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
        private const string CorePropertiesRelationship =
            "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
        private const string ExtendedPropertiesRelationship =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties";
        private const string RelationshipsContentType =
            "application/vnd.openxmlformats-package.relationships+xml";
        private const string PresentationContentType =
            "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
        private const string SlideContentType =
            "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
        private const string SlideMasterContentType =
            "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml";
        private const string SlideLayoutContentType =
            "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml";

        private static readonly HashSet<string> AllowedRelationshipTypes = new(StringComparer.Ordinal)
        {
            OfficeDocumentRelationship,
            CorePropertiesRelationship,
            ExtendedPropertiesRelationship,
            SlideRelationship,
            SlideMasterRelationship,
            SlideLayoutRelationship,
            ThemeRelationship,
            PresentationPropertiesRelationship,
            ImageRelationship,
        };

        // The routed deck writer emits exactly this passive subset. Macro, OLE,
        // package/control, linked-media and other active content types are intentionally
        // absent: a download is released only after this verifier says it is safe to
        // treat as the writer's ordinary presentation output.
        private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.Ordinal)
        {
            RelationshipsContentType,
            "application/xml",
            PresentationContentType,
            SlideContentType,
            SlideMasterContentType,
            SlideLayoutContentType,
            "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml",
            "application/vnd.openxmlformats-officedocument.theme+xml",
            "application/vnd.openxmlformats-package.core-properties+xml",
            "application/vnd.openxmlformats-officedocument.extended-properties+xml",
            "image/png",
            "image/jpeg",
            "image/gif",
        };

        private readonly record struct PackageRelationship(
            string Id,
            string Type,
            string Target,
            string TargetMode);

        private readonly record struct PptxEvidence(int SlideCount, string VisibleText);

        internal static WorkspaceArtifactCompletionResult Verify(SkillRequestPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);

            WorkspaceArtifactCompletionRequirement requirement = plan.CompletionRequirement;
            if (requirement == null)
                return new WorkspaceArtifactCompletionResult(true, null, string.Empty);

            SkillToolInvocation[] invocations;
            lock (plan.Invocations)
                invocations = plan.Invocations.ToArray();

            var matchedRuns = new List<(WorkspaceSkillRunRequirement Requirement, int Index)>();
            int nextInvocation = 0;
            foreach (WorkspaceSkillRunRequirement requiredRun in requirement.RequiredRuns)
            {
                int matchIndex = -1;
                for (int index = nextInvocation; index < invocations.Length; index++)
                {
                    if (!MatchesRequiredRun(invocations[index], requiredRun))
                        continue;
                    matchIndex = index;
                    break;
                }

                if (matchIndex < 0)
                {
                    return Incomplete(
                        $"The required successful skills_run for "
                        + $"{requiredRun.SkillId}/{requiredRun.ResourcePath} is missing or out of order.");
                }

                matchedRuns.Add((requiredRun, matchIndex));
                nextInvocation = matchIndex + 1;
            }

            (WorkspaceSkillRunRequirement Requirement, int Index)[] producers = matchedRuns
                .Where(run => run.Requirement.ProducesArtifact)
                .ToArray();
            IEnumerable<SkillToolInvocation> artifactInvocations = producers.Length == 0
                ? invocations
                : invocations.Select((invocation, index) => (Invocation: invocation, Index: index))
                    .Where(candidate => producers.Any(producer =>
                        candidate.Index >= producer.Index
                        && MatchesRequiredRun(candidate.Invocation, producer.Requirement)))
                    .Select(candidate => candidate.Invocation);
            SkillProducedFile[] candidates = artifactInvocations
                .SelectMany(invocation => invocation.Files ?? Array.Empty<SkillProducedFile>())
                .Where(file => HasExtension(file.Name, requirement.Extension))
                .ToArray();

            if (candidates.Length == 0)
            {
                return Incomplete(
                    $"No {requirement.Extension} file produced by a tool in this turn was captured.");
            }

            string lastReason = $"No captured {requirement.Extension} file passed validation.";
            for (int index = candidates.Length - 1; index >= 0; index--)
            {
                SkillProducedFile candidate = candidates[index];
                if (TryValidateCandidate(candidate, requirement, out string reason))
                    return new WorkspaceArtifactCompletionResult(true, candidate, string.Empty);
                lastReason = reason;
            }

            return Incomplete(lastReason);
        }

        private static bool MatchesRequiredRun(
            SkillToolInvocation invocation,
            WorkspaceSkillRunRequirement required)
        {
            if (!invocation.Ok
                || !string.Equals(invocation.Tool, SkillTools.RunToolName, StringComparison.Ordinal)
                || !string.Equals(invocation.SkillId, required.SkillId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string path = invocation.ResourcePath?.Replace('\\', '/');
            return string.Equals(path, required.ResourcePath, SkillPathGuard.PathComparison);
        }

        /// <summary>
        /// Resolve only the canonical URL spelling emitted by <see cref="CodeArtifactStore.UrlFor"/>,
        /// and bind its run/path back to the structured artifact name before touching disk.
        /// This means the bytes validated are exactly the bytes the returned link serves.
        /// </summary>
        internal static bool TryResolveCodeArtifact(
            CodeArtifactStore store,
            string artifactUriPrefix,
            SkillProducedFile candidate,
            out string fullPath,
            out string error)
        {
            fullPath = null;
            error = null;
            if (store == null || string.IsNullOrWhiteSpace(artifactUriPrefix))
            {
                error = "The artifact store is unavailable.";
                return false;
            }

            string prefix = artifactUriPrefix.TrimEnd('/');
            string marker = prefix + "/";
            string url = candidate.Url ?? string.Empty;
            if (!url.StartsWith(marker, StringComparison.Ordinal))
            {
                error = "The download URL is not owned by this artifact store.";
                return false;
            }

            string suffix = url.Substring(marker.Length);
            string[] encoded = suffix.Split('/');
            if (encoded.Length < 2 || encoded.Any(segment => segment.Length == 0))
            {
                error = "The download URL does not identify an artifact run and file.";
                return false;
            }

            var decoded = new string[encoded.Length];
            try
            {
                for (int index = 0; index < encoded.Length; index++)
                {
                    decoded[index] = Uri.UnescapeDataString(encoded[index]);
                    if (decoded[index].Length == 0
                        || string.Equals(decoded[index], ".", StringComparison.Ordinal)
                        || string.Equals(decoded[index], "..", StringComparison.Ordinal)
                        || decoded[index].IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
                    {
                        error = "The download URL contains an unsafe path segment.";
                        return false;
                    }
                }
            }
            catch (UriFormatException)
            {
                error = "The download URL contains invalid escaping.";
                return false;
            }

            string runId = decoded[0];
            string relative = string.Join("/", decoded.Skip(1));
            string candidateName = (candidate.Name ?? string.Empty).Replace('\\', '/');
            if (!string.Equals(relative, candidateName, StringComparison.Ordinal))
            {
                error = "The download URL does not identify the captured file name.";
                return false;
            }

            // A round trip rejects ambiguous spellings such as encoded separators,
            // duplicate slashes, raw fragments and non-canonical percent escapes.
            if (!string.Equals(
                CodeArtifactStore.UrlFor(prefix, runId, relative), url, StringComparison.Ordinal))
            {
                error = "The download URL is not the canonical captured-artifact URL.";
                return false;
            }

            if (!store.TryResolve(runId, relative, out string resolved, out string resolveError))
            {
                error = "The captured artifact is unavailable: " + (resolveError ?? "not found");
                return false;
            }

            fullPath = resolved;
            return true;
        }

        private static bool TryValidateCandidate(
            SkillProducedFile candidate,
            WorkspaceArtifactCompletionRequirement requirement,
            out string reason)
        {
            reason = string.Empty;
            string displayName = string.IsNullOrWhiteSpace(candidate.Name)
                ? "the captured file"
                : $"'{candidate.Name}'";

            if (string.IsNullOrWhiteSpace(candidate.Url))
            {
                reason = $"{displayName} has no downloadable artifact URL.";
                return false;
            }
            if (candidate.Bytes <= 0)
            {
                reason = $"{displayName} is empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(candidate.Name) || Path.IsPathRooted(candidate.Name))
            {
                reason = "The captured artifact path is not relative to the shared workspace.";
                return false;
            }
            if (!requirement.ResolveArtifact(candidate, out string fullPath, out string resolveError))
            {
                reason = $"{displayName} cannot be verified: {resolveError}";
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, FileOptions.SequentialScan);
                if (stream.Length != candidate.Bytes)
                {
                    reason = $"{displayName}'s captured download changed after it was reported; run the writer again.";
                    return false;
                }

                string relative = candidate.Name.Replace('\\', '/');
                if (requirement.Baselines.TryGetValue(relative, out WorkspaceArtifactBaseline baseline)
                    && stream.Length == baseline.Bytes)
                {
                    if (baseline.Digest == null)
                    {
                        reason = $"{displayName} already existed before this request and its same-size capture "
                            + "cannot prove that the report was updated; write the report to a new path.";
                        return false;
                    }

                    byte[] currentDigest = SHA256.HashData(stream);
                    stream.Position = 0;
                    if (CryptographicOperations.FixedTimeEquals(currentDigest, baseline.Digest))
                    {
                        reason = $"{displayName} is an unchanged deck from before this request; update the deck "
                            + "with this request's research and run the writer again.";
                        return false;
                    }
                }

                if (string.Equals(requirement.Extension, ".pptx", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryValidatePptx(stream, displayName, out PptxEvidence evidence, out reason))
                        return false;
                    if (evidence.SlideCount < requirement.MinimumSlides)
                    {
                        reason = $"{displayName} has {evidence.SlideCount} slide(s), but this report requires "
                            + $"at least {requirement.MinimumSlides}.";
                        return false;
                    }
                    foreach (string term in requirement.RequiredVisibleTerms)
                    {
                        if (evidence.VisibleText.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            reason = $"{displayName} does not visibly contain required report term '{term}'.";
                            return false;
                        }
                    }
                    if (requirement.RequireVisibleHttpUrl
                        && evidence.VisibleText.IndexOf("http://", StringComparison.OrdinalIgnoreCase) < 0
                        && evidence.VisibleText.IndexOf("https://", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        reason = $"{displayName} does not visibly contain an http:// or https:// source URL.";
                        return false;
                    }
                    if (!string.IsNullOrEmpty(requirement.CitationEvidencePath)
                        && !HasCitationFromEvidence(
                            evidence.VisibleText, requirement, out string citationError))
                    {
                        reason = $"{displayName} {citationError}";
                        return false;
                    }
                    return true;
                }

                reason = $"There is no structural validator for '{requirement.Extension}'.";
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or InvalidDataException or XmlException
                                          or CryptographicException)
            {
                reason = $"{displayName} could not be verified: {ex.Message}";
                return false;
            }
        }

        private static bool HasCitationFromEvidence(
            string visibleDeckText,
            WorkspaceArtifactCompletionRequirement requirement,
            out string error)
        {
            error = string.Empty;
            string evidenceText;
            using (requirement.Workspace.BeginOperation())
            {
                if (!requirement.Workspace.TryReadFile(
                        requirement.CitationEvidencePath, out evidenceText, out string readError))
                {
                    error = $"cannot verify its citations because {readError}";
                    return false;
                }
            }

            if (evidenceText.Length > MaxCitationEvidenceCharacters)
            {
                error = $"cannot verify citations because {requirement.CitationEvidencePath} exceeds the "
                    + $"{MaxCitationEvidenceCharacters}-character evidence limit.";
                return false;
            }

            HashSet<string> evidenceUrls = ExtractHttpUrls(evidenceText);
            if (evidenceUrls.Count == 0)
            {
                error = $"cannot be research-backed because {requirement.CitationEvidencePath} contains no source URL.";
                return false;
            }

            // Compare complete extracted URLs. A substring check accepts a fabricated
            // extension of a researched URL (for example, source/path-malicious) or an
            // unrelated URL that embeds the source in its query string.
            HashSet<string> visibleDeckUrls = ExtractHttpUrls(visibleDeckText);
            if (visibleDeckUrls.Overlaps(evidenceUrls))
                return true;

            error = $"does not visibly cite any URL from {requirement.CitationEvidencePath}; "
                + "copy at least one researched source URL into the deck and run the writer again.";
            return false;
        }

        private static HashSet<string> ExtractHttpUrls(string text) =>
            HttpUrlPattern.Matches(text ?? string.Empty)
                .Select(match => match.Value.TrimEnd('.', ',', ';', ':'))
                .Where(url => url.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static bool TryValidatePptx(
            Stream stream,
            string displayName,
            out PptxEvidence evidence,
            out string reason)
        {
            evidence = default;
            reason = string.Empty;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaxPackageEntries)
            {
                reason = $"{displayName} has an invalid or unreasonably large PowerPoint part index.";
                return false;
            }

            if (!TrySingleEntry(archive, "[Content_Types].xml", out ZipArchiveEntry contentTypesEntry)
                || !TrySingleEntry(archive, "_rels/.rels", out ZipArchiveEntry rootRelationshipsEntry)
                || !TrySingleEntry(archive, "ppt/presentation.xml", out ZipArchiveEntry presentationEntry)
                || !TrySingleEntry(
                    archive, "ppt/_rels/presentation.xml.rels", out ZipArchiveEntry presentationRelationshipsEntry))
            {
                reason = $"{displayName} is not a complete PowerPoint package (a required package part is missing or duplicated).";
                return false;
            }

            long parsedXmlBytes = 0;
            if (!TryValidatePassiveRelationships(archive, ref parsedXmlBytes))
            {
                reason = $"{displayName} contains an external or unsupported PowerPoint relationship.";
                return false;
            }

            if (!TryLoadXml(contentTypesEntry, ref parsedXmlBytes, out XDocument contentTypes)
                || !HasRoot(contentTypes, "Types", ContentTypesNamespace)
                || !TryLoadXml(rootRelationshipsEntry, ref parsedXmlBytes, out XDocument rootRelationships)
                || !TryReadRelationships(rootRelationships, out IReadOnlyDictionary<string, PackageRelationship> rootRels)
                || !TryLoadXml(presentationEntry, ref parsedXmlBytes, out XDocument presentation)
                || !HasRoot(presentation, "presentation", PresentationNamespace)
                || !TryLoadXml(
                    presentationRelationshipsEntry, ref parsedXmlBytes, out XDocument presentationRelationships)
                || !TryReadRelationships(
                    presentationRelationships, out IReadOnlyDictionary<string, PackageRelationship> presentationRels))
            {
                reason = $"{displayName} contains invalid or empty PowerPoint XML parts.";
                return false;
            }

            if (!TryReadContentTypes(
                contentTypes,
                out IReadOnlyDictionary<string, string> defaults,
                out IReadOnlyDictionary<string, string> overrides)
                || !defaults.TryGetValue("rels", out string relsType)
                || !string.Equals(relsType, RelationshipsContentType, StringComparison.Ordinal)
                || !overrides.TryGetValue("/ppt/presentation.xml", out string presentationType)
                || !string.Equals(presentationType, PresentationContentType, StringComparison.Ordinal)
                || defaults.Values.Any(type => !AllowedContentTypes.Contains(type))
                || overrides.Values.Any(type => !AllowedContentTypes.Contains(type)))
            {
                reason = $"{displayName} does not declare only the supported passive PowerPoint content types.";
                return false;
            }

            PackageRelationship[] officeDocuments = rootRels.Values
                .Where(relationship => string.Equals(
                    relationship.Type, OfficeDocumentRelationship, StringComparison.Ordinal))
                .ToArray();
            if (officeDocuments.Length != 1
                || IsExternal(officeDocuments[0])
                || !TryResolveRelationshipTarget(
                    string.Empty, officeDocuments[0].Target, out string officeDocumentTarget)
                || !string.Equals(officeDocumentTarget, "ppt/presentation.xml", StringComparison.Ordinal))
            {
                reason = $"{displayName}'s package root does not point to ppt/presentation.xml.";
                return false;
            }

            XNamespace p = PresentationNamespace;
            XNamespace r = OfficeRelationshipsNamespace;
            XElement presentationRoot = presentation.Root;
            XElement masterIds = presentationRoot.Element(p + "sldMasterIdLst");
            XElement slideIds = presentationRoot.Element(p + "sldIdLst");
            XElement slideSize = presentationRoot.Element(p + "sldSz");
            if (masterIds == null || slideIds == null || slideSize == null
                || !HasPositiveLong(slideSize, "cx") || !HasPositiveLong(slideSize, "cy"))
            {
                reason = $"{displayName}'s presentation is missing its master list, slide list, or slide size.";
                return false;
            }

            XElement[] citedMasters = masterIds.Elements(p + "sldMasterId").ToArray();
            if (citedMasters.Length == 0 || citedMasters.Length > MaxSlides)
            {
                reason = $"{displayName}'s presentation does not cite a usable slide master.";
                return false;
            }

            var masterNumericIds = new HashSet<uint>();
            var masterRelationshipIds = new HashSet<string>(StringComparer.Ordinal);
            var masterTargets = new HashSet<string>(StringComparer.Ordinal);
            var masterLayoutOwners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (XElement masterId in citedMasters)
            {
                string numericText = (string)masterId.Attribute("id");
                string relationshipId = (string)masterId.Attribute(r + "id");
                if (!uint.TryParse(numericText, NumberStyles.None, CultureInfo.InvariantCulture, out uint numericId)
                    || !masterNumericIds.Add(numericId)
                    || string.IsNullOrWhiteSpace(relationshipId)
                    || !masterRelationshipIds.Add(relationshipId)
                    || !presentationRels.TryGetValue(relationshipId, out PackageRelationship relationship)
                    || IsExternal(relationship)
                    || !string.Equals(relationship.Type, SlideMasterRelationship, StringComparison.Ordinal)
                    || !TryResolveRelationshipTarget(
                        "ppt/presentation.xml", relationship.Target, out string masterTarget)
                    || !IsSlideMasterPart(masterTarget)
                    || !masterTargets.Add(masterTarget)
                    || !TrySingleEntry(archive, masterTarget, out ZipArchiveEntry masterEntry)
                    || !overrides.TryGetValue("/" + masterTarget, out string masterType)
                    || !string.Equals(masterType, SlideMasterContentType, StringComparison.Ordinal)
                    || !TryLoadXml(masterEntry, ref parsedXmlBytes, out XDocument master)
                    || !HasPartShapeTree(master, "sldMaster"))
                {
                    reason = $"{displayName} has an invalid slide-master ID, relationship, content type, or shape tree.";
                    return false;
                }

                string masterRelationshipsPart = RelationshipsPartFor(masterTarget);
                XElement layoutIdList = master.Root.Element(p + "sldLayoutIdLst");
                if (layoutIdList == null
                    || !TrySingleEntry(
                        archive, masterRelationshipsPart, out ZipArchiveEntry masterRelationshipsEntry)
                    || !TryLoadXml(
                        masterRelationshipsEntry, ref parsedXmlBytes, out XDocument masterRelationships)
                    || !TryReadRelationships(
                        masterRelationships,
                        out IReadOnlyDictionary<string, PackageRelationship> masterRels))
                {
                    reason = $"{displayName}'s cited slide master is missing its layout list or relationships part.";
                    return false;
                }

                XElement[] citedLayouts = layoutIdList.Elements(p + "sldLayoutId").ToArray();
                var layoutNumericIds = new HashSet<uint>();
                var layoutRelationshipIds = new HashSet<string>(StringComparer.Ordinal);
                if (citedLayouts.Length == 0 || citedLayouts.Length > MaxSlides)
                {
                    reason = $"{displayName}'s cited slide master does not cite a usable slide layout.";
                    return false;
                }

                foreach (XElement layoutId in citedLayouts)
                {
                    string layoutNumericText = (string)layoutId.Attribute("id");
                    string layoutRelationshipId = (string)layoutId.Attribute(r + "id");
                    if (!uint.TryParse(
                            layoutNumericText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out uint layoutNumericId)
                        || !layoutNumericIds.Add(layoutNumericId)
                        || string.IsNullOrWhiteSpace(layoutRelationshipId)
                        || !layoutRelationshipIds.Add(layoutRelationshipId)
                        || !masterRels.TryGetValue(
                            layoutRelationshipId, out PackageRelationship layoutRelationship)
                        || IsExternal(layoutRelationship)
                        || !string.Equals(
                            layoutRelationship.Type, SlideLayoutRelationship, StringComparison.Ordinal)
                        || !TryResolveRelationshipTarget(
                            masterTarget, layoutRelationship.Target, out string layoutTarget)
                        || !IsSlideLayoutPart(layoutTarget)
                        || !TrySingleEntry(archive, layoutTarget, out _)
                        || !overrides.TryGetValue("/" + layoutTarget, out string layoutType)
                        || !string.Equals(layoutType, SlideLayoutContentType, StringComparison.Ordinal))
                    {
                        reason = $"{displayName}'s cited slide master has an invalid slide-layout relationship.";
                        return false;
                    }

                    if (!masterLayoutOwners.TryGetValue(layoutTarget, out HashSet<string> owners))
                    {
                        owners = new HashSet<string>(StringComparer.Ordinal);
                        masterLayoutOwners.Add(layoutTarget, owners);
                    }
                    owners.Add(masterTarget);
                }
            }

            XElement[] citedSlides = slideIds.Elements(p + "sldId").ToArray();
            if (citedSlides.Length == 0 || citedSlides.Length > MaxSlides)
            {
                reason = citedSlides.Length == 0
                    ? $"{displayName} contains no cited PowerPoint slides."
                    : $"{displayName} cites more than {MaxSlides} slides.";
                return false;
            }

            var numericIds = new HashSet<uint>();
            var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
            var slideTargets = new HashSet<string>(StringComparer.Ordinal);
            var validatedLayouts = new HashSet<string>(StringComparer.Ordinal);
            var visibleText = new StringBuilder();
            XNamespace drawing = DrawingNamespace;
            foreach (XElement slideId in citedSlides)
            {
                string numericText = (string)slideId.Attribute("id");
                string relationshipId = (string)slideId.Attribute(r + "id");
                if (!uint.TryParse(numericText, NumberStyles.None, CultureInfo.InvariantCulture, out uint numericId)
                    || numericId < 256
                    || !numericIds.Add(numericId)
                    || string.IsNullOrWhiteSpace(relationshipId)
                    || !relationshipIds.Add(relationshipId)
                    || !presentationRels.TryGetValue(relationshipId, out PackageRelationship relationship)
                    || IsExternal(relationship)
                    || !string.Equals(relationship.Type, SlideRelationship, StringComparison.Ordinal)
                    || !TryResolveRelationshipTarget(
                        "ppt/presentation.xml", relationship.Target, out string slideTarget)
                    || !IsSlidePart(slideTarget)
                    || !slideTargets.Add(slideTarget)
                    || !TrySingleEntry(archive, slideTarget, out ZipArchiveEntry slideEntry)
                    || !overrides.TryGetValue("/" + slideTarget, out string slideType)
                    || !string.Equals(slideType, SlideContentType, StringComparison.Ordinal)
                    || !TryLoadXml(slideEntry, ref parsedXmlBytes, out XDocument slide)
                    || !HasPartShapeTree(slide, "sld"))
                {
                    reason = $"{displayName} has a slide ID whose relationship, content type, or shape tree is invalid.";
                    return false;
                }

                foreach (XElement text in slide.Descendants(drawing + "t"))
                {
                    string value = text.Value;
                    if (visibleText.Length > MaxVisibleTextChars - value.Length - 1)
                    {
                        reason = $"{displayName} contains too much visible slide text to verify safely.";
                        return false;
                    }
                    visibleText.Append(value).Append('\n');
                }

                string slideRelationshipsPart = RelationshipsPartFor(slideTarget);
                if (!TrySingleEntry(archive, slideRelationshipsPart, out ZipArchiveEntry slideRelationshipsEntry)
                    || !TryLoadXml(
                        slideRelationshipsEntry, ref parsedXmlBytes, out XDocument slideRelationships)
                    || !TryReadRelationships(
                        slideRelationships, out IReadOnlyDictionary<string, PackageRelationship> slideRels))
                {
                    reason = $"{displayName}'s cited slide is missing a valid relationships part.";
                    return false;
                }

                PackageRelationship[] layoutRelationships = slideRels.Values
                    .Where(item => string.Equals(
                        item.Type, SlideLayoutRelationship, StringComparison.Ordinal))
                    .ToArray();
                if (layoutRelationships.Length != 1
                    || IsExternal(layoutRelationships[0])
                    || !TryResolveRelationshipTarget(
                        slideTarget, layoutRelationships[0].Target, out string layoutTarget)
                    || !IsSlideLayoutPart(layoutTarget)
                    || !TrySingleEntry(archive, layoutTarget, out ZipArchiveEntry layoutEntry)
                    || !overrides.TryGetValue("/" + layoutTarget, out string layoutType)
                    || !string.Equals(layoutType, SlideLayoutContentType, StringComparison.Ordinal))
                {
                    reason = $"{displayName}'s cited slide does not resolve one internal slide layout with the correct content type.";
                    return false;
                }

                if (validatedLayouts.Add(layoutTarget)
                    && (!TryLoadXml(layoutEntry, ref parsedXmlBytes, out XDocument layout)
                        || !HasPartShapeTree(layout, "sldLayout")
                        || !TrySingleEntry(
                            archive,
                            RelationshipsPartFor(layoutTarget),
                            out ZipArchiveEntry layoutRelationshipsEntry)
                        || !TryLoadXml(
                            layoutRelationshipsEntry,
                            ref parsedXmlBytes,
                            out XDocument layoutRelationshipsXml)
                        || !TryReadRelationships(
                            layoutRelationshipsXml,
                            out IReadOnlyDictionary<string, PackageRelationship> layoutRels)
                        || !TryResolveLayoutMaster(
                            layoutTarget,
                            layoutRels,
                            masterTargets,
                            masterLayoutOwners)))
                {
                    reason = $"{displayName}'s cited slide layout has an invalid root, shape tree, or reciprocal slide-master relationship.";
                    return false;
                }
            }

            evidence = new PptxEvidence(citedSlides.Length, visibleText.ToString());
            return true;
        }

        private static bool TryResolveLayoutMaster(
            string layoutTarget,
            IReadOnlyDictionary<string, PackageRelationship> relationships,
            IReadOnlySet<string> masterTargets,
            IReadOnlyDictionary<string, HashSet<string>> masterLayoutOwners)
        {
            PackageRelationship[] masters = relationships.Values
                .Where(item => string.Equals(
                    item.Type, SlideMasterRelationship, StringComparison.Ordinal))
                .ToArray();
            return masters.Length == 1
                && !IsExternal(masters[0])
                && TryResolveRelationshipTarget(
                    layoutTarget, masters[0].Target, out string masterTarget)
                && masterTargets.Contains(masterTarget)
                && masterLayoutOwners.TryGetValue(layoutTarget, out HashSet<string> owners)
                && owners.Contains(masterTarget);
        }

        private static bool TryReadContentTypes(
            XDocument document,
            out IReadOnlyDictionary<string, string> defaults,
            out IReadOnlyDictionary<string, string> overrides)
        {
            var byExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byPart = new Dictionary<string, string>(StringComparer.Ordinal);
            defaults = byExtension;
            overrides = byPart;
            if (!HasRoot(document, "Types", ContentTypesNamespace))
                return false;

            XNamespace ct = ContentTypesNamespace;
            foreach (XElement item in document.Root.Elements())
            {
                if (item.Name == ct + "Default")
                {
                    string extension = (string)item.Attribute("Extension");
                    string type = (string)item.Attribute("ContentType");
                    if (string.IsNullOrWhiteSpace(extension)
                        || string.IsNullOrWhiteSpace(type)
                        || !byExtension.TryAdd(extension, type))
                    {
                        return false;
                    }
                }
                else if (item.Name == ct + "Override")
                {
                    string part = (string)item.Attribute("PartName");
                    string type = (string)item.Attribute("ContentType");
                    if (string.IsNullOrWhiteSpace(part)
                        || part[0] != '/'
                        || string.IsNullOrWhiteSpace(type)
                        || !byPart.TryAdd(part, type))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadRelationships(
            XDocument document,
            out IReadOnlyDictionary<string, PackageRelationship> relationships)
        {
            var found = new Dictionary<string, PackageRelationship>(StringComparer.Ordinal);
            relationships = found;
            if (!HasRoot(document, "Relationships", RelationshipsNamespace))
                return false;

            XNamespace rel = RelationshipsNamespace;
            XElement[] elements = document.Root.Elements().ToArray();
            if (elements.Length > MaxRelationships)
                return false;

            foreach (XElement item in elements)
            {
                if (item.Name != rel + "Relationship")
                    return false;

                string id = (string)item.Attribute("Id");
                string type = (string)item.Attribute("Type");
                string target = (string)item.Attribute("Target");
                string targetMode = (string)item.Attribute("TargetMode");
                if (string.IsNullOrWhiteSpace(id)
                    || string.IsNullOrWhiteSpace(type)
                    || string.IsNullOrWhiteSpace(target)
                    || !found.TryAdd(id, new PackageRelationship(id, type, target, targetMode)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidatePassiveRelationships(
            ZipArchive archive,
            ref long parsedXmlBytes)
        {
            ZipArchiveEntry[] relationshipParts = archive.Entries
                .Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (relationshipParts.Length == 0 || relationshipParts.Length > MaxRelationships)
                return false;

            foreach (ZipArchiveEntry entry in relationshipParts)
            {
                if (!TryLoadXml(entry, ref parsedXmlBytes, out XDocument document)
                    || !TryReadRelationships(
                        document,
                        out IReadOnlyDictionary<string, PackageRelationship> relationships)
                    || relationships.Values.Any(relationship =>
                        IsExternal(relationship)
                        || !AllowedRelationshipTypes.Contains(relationship.Type)))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsExternal(PackageRelationship relationship) =>
            !string.IsNullOrEmpty(relationship.TargetMode)
            && !string.Equals(relationship.TargetMode, "Internal", StringComparison.OrdinalIgnoreCase);

        private static bool TryResolveRelationshipTarget(
            string sourcePart,
            string target,
            out string resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(target)
                || target[0] == '/'
                || target.Contains('\\')
                || target.Contains('?')
                || target.Contains('#'))
            {
                return false;
            }

            var segments = new List<string>();
            int sourceSlash = sourcePart.LastIndexOf('/');
            if (sourceSlash >= 0)
                segments.AddRange(sourcePart.Substring(0, sourceSlash).Split('/'));

            foreach (string encodedSegment in target.Split('/'))
            {
                if (encodedSegment.Length == 0)
                    return false;

                string segment;
                try { segment = Uri.UnescapeDataString(encodedSegment); }
                catch (UriFormatException) { return false; }
                if (segment.Length == 0 || segment.Contains('/') || segment.Contains('\\'))
                    return false;
                if (string.Equals(segment, ".", StringComparison.Ordinal))
                    continue;
                if (string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    if (segments.Count == 0)
                        return false;
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                segments.Add(segment);
            }

            if (segments.Count == 0)
                return false;
            resolved = string.Join("/", segments);
            return true;
        }

        private static bool HasPartShapeTree(XDocument document, string rootLocalName)
        {
            if (!HasRoot(document, rootLocalName, PresentationNamespace))
                return false;

            XNamespace p = PresentationNamespace;
            XElement shapeTree = document.Root.Element(p + "cSld")?.Element(p + "spTree");
            XElement nonVisual = shapeTree?.Element(p + "nvGrpSpPr");
            return nonVisual != null
                && nonVisual.Element(p + "cNvPr") != null
                && nonVisual.Element(p + "cNvGrpSpPr") != null
                && nonVisual.Element(p + "nvPr") != null
                && shapeTree.Element(p + "grpSpPr") != null;
        }

        private static bool HasPositiveLong(XElement element, string attribute) =>
            long.TryParse(
                (string)element.Attribute(attribute), NumberStyles.None,
                CultureInfo.InvariantCulture, out long value)
            && value > 0;

        private static bool HasRoot(XDocument document, string localName, string xmlNamespace) =>
            document?.Root != null
            && string.Equals(document.Root.Name.LocalName, localName, StringComparison.Ordinal)
            && string.Equals(document.Root.Name.NamespaceName, xmlNamespace, StringComparison.Ordinal);

        private static bool TryLoadXml(
            ZipArchiveEntry entry,
            ref long parsedXmlBytes,
            out XDocument document)
        {
            document = null;
            if (entry == null
                || entry.Length <= 0
                || entry.Length > MaxXmlPartBytes
                || parsedXmlBytes > MaxParsedXmlBytes - entry.Length)
            {
                return false;
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaxXmlPartBytes,
                XmlResolver = null,
            };
            using Stream part = entry.Open();
            using XmlReader reader = XmlReader.Create(part, settings);
            document = XDocument.Load(reader, LoadOptions.None);
            parsedXmlBytes += entry.Length;
            return document.Root != null;
        }

        private static bool TrySingleEntry(
            ZipArchive archive,
            string name,
            out ZipArchiveEntry entry)
        {
            ZipArchiveEntry[] matches = archive.Entries
                .Where(candidate => string.Equals(candidate.FullName, name, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            entry = matches.Length == 1 ? matches[0] : null;
            return entry != null;
        }

        private static bool IsSlidePart(string name)
            => IsNumberedPart(name, "ppt/slides/slide", ".xml");

        private static bool IsSlideMasterPart(string name)
            => IsNumberedPart(name, "ppt/slideMasters/slideMaster", ".xml");

        private static bool IsSlideLayoutPart(string name)
            => IsNumberedPart(name, "ppt/slideLayouts/slideLayout", ".xml");

        private static bool IsNumberedPart(string name, string prefix, string suffix)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            string number = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int slide)
                && slide > 0;
        }

        private static string RelationshipsPartFor(string sourcePart)
        {
            int slash = sourcePart.LastIndexOf('/');
            string directory = slash < 0 ? string.Empty : sourcePart.Substring(0, slash + 1);
            string file = slash < 0 ? sourcePart : sourcePart.Substring(slash + 1);
            return directory + "_rels/" + file + ".rels";
        }

        private static bool HasExtension(string name, string extension)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(name)
                    && string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private static WorkspaceArtifactCompletionResult Incomplete(string reason) =>
            new(false, null, reason);
    }
}
