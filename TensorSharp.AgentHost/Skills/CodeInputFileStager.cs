// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

using System;
using System.Collections.Generic;
using System.IO;
using TensorSharp.AgentHost.CodeExec;

namespace TensorSharp.AgentHost.Skills
{
    /// <summary>
    /// Places conversation attachments in a session workspace under the display names
    /// shown to the model.
    /// </summary>
    /// <remarks>
    /// Staging is shared by ordinary code tools and skill scripts. Keeping it here means
    /// a first <c>read_file</c> or <c>skills_run</c> call sees exactly the same inputs as
    /// a first shell call, and lets a host compact an inlined attachment only after the
    /// corresponding file is known to be available in the workspace.
    /// </remarks>
    public static class CodeInputFileStager
    {
        /// <summary>
        /// Stage <paramref name="inputFiles"/> into <paramref name="workspace"/> and
        /// return the sanitized display names that are readable there afterward.
        /// </summary>
        /// <remarks>
        /// A workspace copy that is at least as new as its source is deliberately kept:
        /// it may have been edited by an earlier tool call, and copying the upload over it
        /// would silently undo the model's work. When this method does replace a file, it
        /// also invalidates the read ledger so a later edit must read the new bytes first.
        /// </remarks>
        public static IReadOnlySet<string> Stage(
            IReadOnlyList<CodeInputFile>? inputFiles,
            SessionWorkspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);

            var available = new HashSet<string>(StringComparer.Ordinal);
            if (inputFiles == null || inputFiles.Count == 0)
                return available;

            foreach (CodeInputFile input in inputFiles)
            {
                // The name is what the model was told; flattening keeps a name like
                // "../x" from writing outside the directory the sandbox will confine.
                string name = Path.GetFileName(input.Name ?? string.Empty);
                if (name.Length == 0 || string.IsNullOrEmpty(input.SourcePath))
                    continue;

                // Resolve through the workspace guard rather than trusting lexical
                // containment. A previous tool call can leave `name` as a symlink, and
                // the timestamp and replacement operations below run in the unsandboxed
                // host process. Following that link would turn attachment staging into
                // an arbitrary host read or write.
                if (!workspace.TryResolve(name, out string destination, out _))
                    continue;

                string? temporary = null;
                try
                {
                    // The workspace resolver rejects links that leave the workspace.
                    // Reject the leaf even when it points back inside: staging is meant
                    // to create or reuse a regular workspace file, never to operate
                    // through a model-created reparse point. LinkTarget also detects a
                    // dangling symlink, for which File.Exists deliberately returns false.
                    if (IsLinkOrReparsePoint(destination))
                        continue;

                    var existing = new FileInfo(destination);
                    var source = new FileInfo(input.SourcePath);
                    if (!source.Exists)
                        continue;

                    if (!existing.Exists || existing.LastWriteTimeUtc < source.LastWriteTimeUtc)
                    {
                        // Never open the model-controlled destination for writing. Copy
                        // into a fresh, unpredictable regular file and atomically replace
                        // the directory entry instead. FileMode.CreateNew refuses even a
                        // dangling symlink at the temporary name, and rename/move replaces
                        // a destination symlink itself rather than following its target.
                        string temporaryName = ".tensorsharp-stage-"
                                             + Guid.NewGuid().ToString("N") + ".tmp";
                        if (!workspace.TryResolve(temporaryName, out temporary, out _))
                            continue;

                        using (var sourceStream = new FileStream(
                            input.SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (var temporaryStream = new FileStream(
                            temporary,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None))
                        {
                            sourceStream.CopyTo(temporaryStream);
                            temporaryStream.Flush();
                            File.SetLastWriteTimeUtc(
                                temporaryStream.SafeFileHandle, source.LastWriteTimeUtc);
                        }

                        File.Move(temporary, destination, overwrite: true);
                        temporary = null;

                        // The host replaced a file behind the model's back. It has not
                        // read these bytes, so a subsequent edit starts from scratch.
                        workspace.Reads.Forget(destination);
                    }

                    // Existence alone is not enough for prompt compaction: only report a
                    // name when the host can really open a regular file without following
                    // a leaf link or a concurrently swapped parent directory.
                    if (!ShellSession.CanOpenRegularFileUnderRootNoFollow(
                        workspace.WorkDirectory, destination))
                        continue;

                    available.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort. The tool that asks for an unavailable input reports
                    // the actionable error in the conversation; callers can also see
                    // that its name is absent from the returned set.
                }
                finally
                {
                    if (temporary != null)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Best-effort cleanup of a host-generated scratch name.
                        }
                    }
                }
            }

            return available;
        }

        /// <summary>
        /// Return true for every symbolic link/reparse-point leaf, including a dangling
        /// link. Inspection failures are rejected too: staging must fail closed before
        /// an unsandboxed host file API is allowed to open the destination by name.
        /// </summary>
        private static bool IsLinkOrReparsePoint(string path)
        {
            try
            {
                var info = new FileInfo(path);
                info.Refresh();
                if (info.LinkTarget != null)
                    return true;

                if (!info.Exists && !Directory.Exists(path))
                    return false;

                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException
                                          or PathTooLongException)
            {
                return true;
            }
        }
    }
}
