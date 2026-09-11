// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorSharp.Server.Hosting;

namespace TensorAgent.Core.Hosting;

/// <summary>
/// Gives an uploaded part a file name the upload policy can classify.
///
/// <para>
/// The chat service decides what an upload IS from its extension alone, and refuses an
/// extensionless name with a 400. That is right for a browser, which always sends one,
/// and wrong for this app: iOS's photo picker hands MAUI an
/// <c>NSItemProvider.SuggestedName</c>, which is the asset's name with the extension
/// STRIPPED — "IMG_0004", not "IMG_0004.HEIC". Every photo picked from the library
/// therefore arrived as a nameless blob and was refused with "Upload failed (400)",
/// while the camera and the document picker — which do carry an extension — worked.
/// </para>
/// <para>
/// So the transport names the part before the service classifies it, from what it
/// actually has: the declared content type, and failing that the first bytes of the
/// file. Both answers are mapped through <see cref="UploadContentPolicy"/>, so this can
/// only ever produce an extension the serve-side policy already covers — the invariant
/// that keeps <c>/uploads</c> from holding a file it has no content type for. A part it
/// cannot place keeps its name and is refused by the service exactly as before, with the
/// message the user should see.
/// </para>
/// </summary>
internal static class UploadNaming
{
    /// <summary>
    /// The name to hand the chat service for <paramref name="file"/>: unchanged when it
    /// already carries an extension the policy knows, otherwise the same name with one
    /// appended.
    /// </summary>
    public static string ResolveFileName(MultipartFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return ResolveFileName(file.FileName, file.ContentType, file.TempPath);
    }

    /// <summary>The same decision from the three pieces it is actually made of.</summary>
    public static string ResolveFileName(string? fileName, string? contentType, string? path)
    {
        string name = fileName ?? string.Empty;
        if (UploadContentPolicy.Classify(Path.GetExtension(name)) != "unknown")
            return name;

        string? extension = FromContentType(contentType) ?? FromContent(path);
        if (extension is null)
            return name;

        // A name that is empty or nothing but dots would produce ".heic": a hidden file
        // with no stem, and a chip in the page with nothing written on it.
        string stem = name.Trim().TrimEnd('.');
        return (stem.Length == 0 ? "upload" : stem) + extension;
    }

    /// <summary>
    /// The extension for a declared media type, or null when nothing maps.
    ///
    /// <para>
    /// Derived from the policy's own table rather than from a second list, so an
    /// extension added there is understood here the same day. Several extensions share a
    /// content type — every text and code file is <c>text/plain</c>, .jpg and .jpeg are
    /// both <c>image/jpeg</c> — so the ambiguous ones are pinned to the spelling a person
    /// expects to see rather than to whichever the dictionary happens to yield first.
    /// </para>
    /// </summary>
    private static string? FromContentType(string? contentType)
    {
        string type = (contentType ?? string.Empty).Split(';')[0].Trim();
        if (type.Length == 0 || string.Equals(type, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return null;

        if (Preferred.TryGetValue(type, out string? pinned))
            return pinned;

        foreach (KeyValuePair<string, string> entry in UploadContentPolicy.ServeContentTypes)
        {
            if (string.Equals(entry.Value.Split(';')[0].Trim(), type, StringComparison.OrdinalIgnoreCase))
                return entry.Key;
        }
        return null;
    }

    /// <summary>Where several extensions share a content type, the one to write.</summary>
    private static readonly Dictionary<string, string> Preferred = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/heic"] = ".heic",
        ["image/heic-sequence"] = ".heic",
        ["image/heif"] = ".heif",
        ["image/heif-sequence"] = ".heif",
        ["text/plain"] = ".txt",
        ["text/markdown"] = ".md",
        ["text/csv"] = ".csv",
        ["application/json"] = ".json",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["video/quicktime"] = ".mov",
    };

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Matroska = { 0x1A, 0x45, 0xDF, 0xA3 };

    /// <summary>
    /// The extension the file's own first bytes imply, or null.
    ///
    /// <para>
    /// The last resort, and the one that actually rescues an iPhone photo: MAUI derives a
    /// part's content type FROM its extension, so a name without one also arrives without
    /// a usable type and the bytes are all that is left. Only formats the policy already
    /// serves are recognised; anything else stays unknown and is refused upstream, which
    /// is the same answer as before this existed.
    /// </para>
    /// </summary>
    private static string? FromContent(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        byte[] head = new byte[32];
        int read;
        try
        {
            using FileStream stream = File.OpenRead(path);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            return null;
        }
        if (read < 12)
            return null;

        ReadOnlySpan<byte> bytes = head.AsSpan(0, read);

        if (bytes.StartsWith(Jpeg)) return ".jpg";
        if (bytes.StartsWith(Png)) return ".png";
        if (bytes.StartsWith("GIF8"u8)) return ".gif";
        if (bytes.StartsWith("BM"u8)) return ".bmp";
        if (bytes.StartsWith("%PDF-"u8)) return ".pdf";
        if (bytes.StartsWith("OggS"u8)) return ".ogg";
        if (bytes.StartsWith("fLaC"u8)) return ".flac";
        if (bytes.StartsWith("ID3"u8)) return ".mp3";
        if (bytes.StartsWith(Matroska)) return ".mkv";
        if (bytes.StartsWith("RIFF"u8))
        {
            ReadOnlySpan<byte> form = bytes[8..];
            if (form.StartsWith("WEBP"u8)) return ".webp";
            if (form.StartsWith("WAVE"u8)) return ".wav";
            if (form.StartsWith("AVI "u8)) return ".avi";
            return null;
        }

        // ISO base media: a size, then "ftyp", then the brand. The brand is what
        // separates an iPhone photo from an iPhone video, and both arrive through the
        // same picker with the same absent name.
        if (bytes[4..].StartsWith("ftyp"u8))
        {
            return System.Text.Encoding.ASCII.GetString(bytes[8..12]) switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" or "miaf" => ".heic",
                "qt  " => ".mov",
                // AVIF is a HEIF brand this app does not serve; calling it .heif would put
                // a file on disk that /uploads types as something it is not.
                "avif" or "avis" => null,
                _ => ".mp4",
            };
        }

        return null;
    }
}
