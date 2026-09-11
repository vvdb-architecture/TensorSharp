// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TensorSharp.AgentHost.CodeExec;

namespace TensorSharp.AgentHost.Skills
{
    /// <summary>
    /// Places conversation attachments in a session workspace under the display names
    /// shown to the model. Delimited-text inputs are made UTF-8-readable in that private
    /// execution copy, while the durable upload remains byte-for-byte unchanged.
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
                bool replacementAttempted = false;
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
                        replacementAttempted = true;
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
                            CopyForExecution(sourceStream, temporaryStream, name);
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or DecoderFallbackException)
                {
                    if (replacementAttempted)
                        RemoveStaleDestination(destination, workspace);

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
        /// A failed refresh must not leave an older attachment readable under the new
        /// upload's advertised name. Later shell/skill dispatches stage defensively too,
        /// and some do not consume the returned availability set; without removing the
        /// superseded regular file they could analyze yesterday's bytes as today's file.
        /// </summary>
        private static void RemoveStaleDestination(string destination, SessionWorkspace workspace)
        {
            try
            {
                // A leaf link was never a valid staged input and must not be followed by
                // this unsandboxed host cleanup. It also fails the no-follow availability
                // check, so leaving its directory entry cannot masquerade as this upload.
                if (IsLinkOrReparsePoint(destination))
                    return;

                File.Delete(destination);
                workspace.Reads.Forget(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort, matching temporary-file cleanup below. Under the normal
                // host-owned workspace this succeeds; an inaccessible path is not added
                // to the availability set in any case.
            }
        }

        /// <summary>
        /// Copy one attachment into the execution workspace. CSV and TSV exports are
        /// text interchange formats, but in practice spreadsheet and election systems
        /// still emit Windows-1252 (and Excel commonly emits BOM-marked UTF-16). Python's
        /// <c>open()</c>, pandas, Node, and the bundled document scripts all reasonably
        /// default to UTF-8, so leaving those source bytes untouched makes otherwise
        /// valid generated code fail halfway through the table with a decode exception.
        /// </summary>
        /// <remarks>
        /// Only the workspace copy is normalised. The upload remains the user's original
        /// file, and every non-delimited attachment plus an already-valid BOM-less UTF-8
        /// table is still copied byte-for-byte. A model-edited workspace file is also
        /// retained by the timestamp rule above, exactly as before.
        /// </remarks>
        private static void CopyForExecution(Stream source, Stream destination, string name)
        {
            string extension = Path.GetExtension(name);
            if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase))
            {
                source.CopyTo(destination);
                return;
            }

            byte[] head = new byte[4];
            int headLength = source.Read(head, 0, head.Length);
            source.Position = 0;

            Encoding? bomEncoding = BomEncoding(head, headLength, out int bomLength);
            if (bomEncoding != null)
            {
                // We already identified the exact preamble. Skip it ourselves and
                // disable StreamReader's BOM detection below: for a UTF-8 BOM that
                // detector otherwise substitutes Encoding.UTF8's replacement decoder
                // for our strict one, silently turning malformed bytes into U+FFFD.
                source.Position = bomLength;
                TranscodeToUtf8(source, destination, bomEncoding);
                return;
            }

            // Decode and re-encode in one pass instead of validating and then copying
            // the mutable source a second time. For valid UTF-8 this is byte-identical;
            // for a legacy export, discard the partial temp output and retry from byte
            // zero with the single-byte mapping below.
            try
            {
                TranscodeToUtf8(source, destination, StrictUtf8);
                return;
            }
            catch (DecoderFallbackException)
            {
                source.Position = 0;
                destination.Position = 0;
                destination.SetLength(0);
            }

            // There is no charset field in CSV itself. Windows-1252 is the compatible
            // superset used by the common legacy exporters this fallback is for; unlike
            // Encoding.Latin1 it also preserves smart quotes, dashes and the euro sign.
            // The mapping is local so mobile builds do not need the large code-pages
            // provider merely to make a table readable.
            TranscodeWindows1252ToUtf8(source, destination);
        }

        private static Encoding? BomEncoding(byte[] head, int length, out int bomLength)
        {
            bomLength = 0;
            // UTF-32 LE begins with the UTF-16 LE marker, so test both 4-byte forms first.
            if (length >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0)
            {
                bomLength = 4;
                return new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
            }
            if (length >= 4 && head[0] == 0 && head[1] == 0 && head[2] == 0xFE && head[3] == 0xFF)
            {
                bomLength = 4;
                return new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
            }
            if (length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                bomLength = 3;
                return StrictUtf8;
            }
            if (length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                bomLength = 2;
                return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            }
            if (length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            {
                bomLength = 2;
                return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
            }
            return null;
        }

        private static void TranscodeToUtf8(Stream source, Stream destination, Encoding encoding)
        {
            using var reader = new StreamReader(
                source, encoding, detectEncodingFromByteOrderMarks: false,
                bufferSize: 16 * 1024, leaveOpen: true);
            using var writer = new StreamWriter(
                destination, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true);
            char[] buffer = new char[16 * 1024];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                writer.Write(buffer, 0, read);
            writer.Flush();
        }

        private static void TranscodeWindows1252ToUtf8(Stream source, Stream destination)
        {
            using var writer = new StreamWriter(
                destination, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true);
            byte[] bytes = new byte[16 * 1024];
            char[] chars = new char[bytes.Length];
            int read;
            while ((read = source.Read(bytes, 0, bytes.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    chars[i] = DecodeWindows1252(bytes[i]);
                writer.Write(chars, 0, read);
            }
            writer.Flush();
        }

        private static char DecodeWindows1252(byte value)
        {
            if (value < 0x80 || value >= 0xA0)
                return (char)value;
            return Windows1252Controls[value - 0x80];
        }

        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly UTF8Encoding Utf8NoBom = new(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Undefined Windows-1252 bytes retain their corresponding C1 control instead
        // of becoming U+FFFD, so this conversion never silently loses an input byte.
        private const string Windows1252Controls =
            "\u20AC\u0081\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u008D\u017D\u008F" +
            "\u0090\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u009D\u017E\u0178";

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
