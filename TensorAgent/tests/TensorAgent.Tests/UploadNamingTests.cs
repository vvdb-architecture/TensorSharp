// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorAgent.Core.Hosting;

namespace TensorAgent.Tests;

/// <summary>
/// Naming an upload that arrives without a name worth having.
///
/// <para>
/// The bug these are written against is the whole of "Upload failed (400)": iOS's
/// photo picker hands MAUI <c>NSItemProvider.SuggestedName</c>, which is the asset's
/// name with the extension stripped — "IMG_0004", not "IMG_0004.HEIC" — and the chat
/// service classifies an upload by its extension alone, so every photo picked from the
/// library was refused before a byte of it was looked at. The camera and the document
/// picker, which do carry an extension, worked; that is why it read as "images are
/// broken" rather than "uploads are broken".
/// </para>
/// <para>
/// The rule this keeps: whatever is inferred, it can only ever be an extension
/// <c>UploadContentPolicy</c> already serves. A file on disk that <c>/uploads</c> has
/// no content type for is the invariant the strictness was protecting, and it still
/// holds — a part that cannot be placed keeps its name and is refused exactly as
/// before.
/// </para>
/// </summary>
public sealed class UploadNamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-name-" + Guid.NewGuid().ToString("N"));

    public UploadNamingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { /* scratch */ }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The first bytes of a HEIC, which is what an iPhone photo actually is.</summary>
    private static byte[] HeicHeader()
    {
        var bytes = new byte[64];
        bytes[3] = 24;                                        // box size
        "ftypheic"u8.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    private static byte[] Mp4Header()
    {
        var bytes = new byte[64];
        bytes[3] = 24;
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    [Fact]
    public void ANameThatAlreadyCarriesAKnownExtensionIsLeftExactlyAsItIs()
    {
        string path = Write("photo.png", MediaFixtures.RedCircleOnWhitePng(16));
        Assert.Equal("IMG_0004.HEIC", UploadNaming.ResolveFileName("IMG_0004.HEIC", "image/heic", path));
        Assert.Equal("notes.md", UploadNaming.ResolveFileName("notes.md", null, path));
    }

    [Fact]
    public void APhotoWithNoExtensionIsNamedFromItsOwnBytes()
    {
        // Exactly what PHPicker produces: a stem, no suffix, and MAUI derives the
        // content type FROM the suffix, so there is not one of those either.
        string heic = Write("blob1", HeicHeader());
        Assert.Equal("IMG_0004.heic", UploadNaming.ResolveFileName("IMG_0004", null, heic));

        string png = Write("blob2", MediaFixtures.RedCircleOnWhitePng(16));
        Assert.Equal("IMG_0005.png", UploadNaming.ResolveFileName("IMG_0005", "application/octet-stream", png));

        string jpeg = Write("blob3", MediaFixtures.Jpeg(new byte[8 * 8 * 3], 8, 8));
        Assert.Equal("IMG_0006.jpg", UploadNaming.ResolveFileName("IMG_0006", string.Empty, jpeg));
    }

    [Fact]
    public void TheDeclaredContentTypeIsPreferredToTheBytesWhenThereIsOne()
    {
        // A browser sends a real type; the bytes are the fallback, not the first word.
        string path = Write("blob4", MediaFixtures.RedCircleOnWhitePng(16));
        Assert.Equal("shot.png", UploadNaming.ResolveFileName("shot", "image/png", path));
        Assert.Equal("clip.mov", UploadNaming.ResolveFileName("clip", "video/quicktime; codecs=hvc1", path));
        Assert.Equal("note.txt", UploadNaming.ResolveFileName("note", "text/plain; charset=utf-8", path));
    }

    [Fact]
    public void AVideoAndAPhotoAreToldApartByTheirBrandAndNotByTheirContainer()
    {
        // Both are ISO base media with an ftyp box, both come out of the same picker,
        // and one of them must not be saved as the other: /uploads serves an extension,
        // and the chat pipeline extracts frames from a video and not from an image.
        Assert.Equal("a.heic", UploadNaming.ResolveFileName("a", null, Write("b1", HeicHeader())));
        Assert.Equal("b.mp4", UploadNaming.ResolveFileName("b", null, Write("b2", Mp4Header())));
    }

    [Fact]
    public void SomethingUnrecognisableKeepsItsNameSoTheServiceStillRefusesIt()
    {
        // The strictness is the point: an extension nobody serves must not be invented,
        // because /uploads answers 404 for one it has no content type for and the file
        // would be on disk with no way to read it back.
        string path = Write("blob5", new byte[64]);
        Assert.Equal("mystery", UploadNaming.ResolveFileName("mystery", "application/x-thing", path));
        Assert.Equal("archive.7z", UploadNaming.ResolveFileName("archive.7z", null, path));
    }

    [Fact]
    public void ANamelessPartStillEndsUpWithAStem()
    {
        // ".heic" would be a hidden file with no name, and a chip in the page with
        // nothing written on it.
        string path = Write("blob6", HeicHeader());
        Assert.Equal("upload.heic", UploadNaming.ResolveFileName(string.Empty, null, path));
        Assert.Equal("upload.heic", UploadNaming.ResolveFileName("   ", null, path));
        Assert.Equal("IMG_1.heic", UploadNaming.ResolveFileName("IMG_1.", null, path));
    }

    [Fact]
    public void AMissingFileIsNotAnExceptionButAnUnchangedName()
    {
        Assert.Equal("gone", UploadNaming.ResolveFileName("gone", null, Path.Combine(_root, "does-not-exist")));
        Assert.Equal("gone", UploadNaming.ResolveFileName("gone", null, null));
    }
}
