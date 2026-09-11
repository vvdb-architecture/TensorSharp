// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorSharp.Models.Media;
using TensorSharp.Models.Media.Desktop;
using TensorSharp.Models.QwenImage;
using TensorSharp.Models.Video;

namespace InferenceWeb.Tests;

/// <summary>
/// One set of expectations, run against every <see cref="MediaCodecs"/> provider.
///
/// <para><b>Why an abstract class rather than more cases in
/// <see cref="MediaProviderContractTests"/>:</b> the iOS provider cannot be executed by this
/// test host — <c>TensorSharp.Models</c>'s <c>Media/Apple/</c> sources compile only for the
/// <c>net10.0-ios</c> target framework, and a net10.0 xunit runner cannot load an iOS
/// assembly. What CAN be pinned here is the shape of the agreement: every assertion below is
/// written against <see cref="IImageCodec"/> / <see cref="IVideoDecoder"/> /
/// <see cref="IVideoEncoder"/> / <see cref="IAudioDecoder"/> and nothing else, so the Apple
/// subclass at the bottom of this file — compiled in only when this project is built for
/// iOS — inherits the identical checks the two desktop-runnable providers pass today. Model
/// quality depends on the three providers producing the same numbers, and "the same" needs a
/// single definition, not three test files that drifted.</para>
///
/// <para>Deliberately not covered here: anything only one provider can do. Those live with
/// the provider (<see cref="MediaProviderContractTests"/> for the managed/Magick specifics)
/// or, for the Apple decoder's arithmetic, in <c>AppleProviderMathTests</c>,
/// which tests the parts that were kept platform-neutral precisely so they could be run on a
/// desktop CI.</para>
/// </summary>
public abstract class MediaProviderParityTests : IDisposable
{
    private readonly string _dir;

    protected MediaProviderParityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ts-media-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- what a subclass supplies ----------------------------------------------------

    /// <summary>The image codec under test.</summary>
    protected abstract IImageCodec Image { get; }

    /// <summary>The audio decoder this provider puts on <see cref="MediaCodecs.Audio"/>.</summary>
    protected abstract IAudioDecoder Audio { get; }

    /// <summary>The video decoder and MP4 encoder, or <c>null</c> when the provider has none —
    /// in which case the tests assert the actionable refusal instead, which is the contract for
    /// an unfilled slot.</summary>
    protected abstract IVideoDecoder? Video { get; }

    protected abstract IVideoEncoder? Encoder { get; }

    /// <summary>Whether this provider decodes HEIC/HEIF. The whole reason a platform image
    /// codec exists on a phone: it is what the camera writes, and the managed decoder cannot
    /// read it.</summary>
    protected abstract bool DecodesHeic { get; }

    /// <summary>Per-channel slack allowed on a decoded image that carried real alpha. Zero for
    /// a decoder that reads straight alpha out of the file; the Apple provider has to recover
    /// it from a premultiplied Core Graphics context, which costs a level or two in the
    /// semi-transparent range (documented on <c>AppleMediaProvider.DrawStraightRgba</c>).</summary>
    protected virtual int AlphaTolerance => 0;

    /// <summary>Per-channel slack against the Magick.NET reference for a lossy decode (JPEG,
    /// HEIC). Different IDCT and chroma-upsampling arithmetic is codec noise, not a contract
    /// violation.</summary>
    protected virtual int LossyTolerance => 6;

    // ---- images: the exact contract --------------------------------------------------

    [Fact]
    public void Png_RoundTripsThroughTheProviderExactly()
    {
        const int w = 7, h = 5;
        byte[] rgba = Gradient(w, h, channels: 4, opaque: true);

        byte[] png = Image.EncodePng(rgba, w, h, 4);
        Assert.True(Image.CanDecode(png), "a provider must admit to decoding a PNG it just wrote");

        byte[] back = Image.DecodeRgba(png, out int bw, out int bh);
        Assert.Equal((w, h), (bw, bh));
        Assert.Equal(rgba, back);
        Assert.Equal((w, h), Image.ReadDimensions(png));

        // No row padding, ever: the vision processors index straight into this buffer.
        Assert.Equal(w * h * 4, back.Length);
    }

    [Fact]
    public void Png_RgbSourceDecodesBackOpaque()
    {
        const int w = 9, h = 4;
        byte[] rgb = Gradient(w, h, channels: 3, opaque: true);
        byte[] png = Image.EncodePng(rgb, w, h, 3);

        byte[] rgba = Image.DecodeRgba(png, out int bw, out int bh);
        Assert.Equal((w, h), (bw, bh));
        for (int i = 0; i < w * h; i++)
        {
            Assert.Equal(rgb[i * 3], rgba[i * 4]);
            Assert.Equal(rgb[i * 3 + 1], rgba[i * 4 + 1]);
            Assert.Equal(rgb[i * 3 + 2], rgba[i * 4 + 2]);
            Assert.Equal(255, rgba[i * 4 + 3]);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Image.EncodePng(rgb, w, h, 2));
        Assert.Throws<ArgumentException>(() => Image.EncodePng(rgb, w + 1, h, 3));
    }

    /// <summary>
    /// Alpha comes back STRAIGHT, not premultiplied.
    ///
    /// <para>The one place the three providers are allowed to differ, and the reason the
    /// slack is a property rather than a constant: Core Graphics has no straight-alpha 8-bit
    /// RGBA bitmap layout, so the Apple decoder recovers it arithmetically. If a provider ever
    /// returned premultiplied pixels this fails loudly instead of quietly darkening every
    /// semi-transparent image a model is shown — a mid-grey at 50% alpha would come back at
    /// half its value, which no downstream check would notice.</para>
    /// </summary>
    [Fact]
    public void Decode_ReturnsStraightAlphaNotPremultiplied()
    {
        // Alphas kept at or above 64: below that the premultiply itself throws away enough
        // bits that no decoder could recover the colour, so it would be testing rounding noise.
        byte[] alphas = { 255, 192, 128, 64 };
        int w = alphas.Length, h = 2;
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                rgba[p] = 200;
                rgba[p + 1] = 128;
                rgba[p + 2] = 64;
                rgba[p + 3] = alphas[x];
            }

        byte[] png = Image.EncodePng(rgba, w, h, 4);
        byte[] back = Image.DecodeRgba(png, out int bw, out int bh);
        Assert.Equal((w, h), (bw, bh));

        for (int i = 0; i < w * h; i++)
        {
            Assert.Equal(rgba[i * 4 + 3], back[i * 4 + 3]);   // alpha itself is never lossy
            for (int c = 0; c < 3; c++)
            {
                int expected = rgba[i * 4 + c];
                int actual = back[i * 4 + c];
                Assert.True(
                    Math.Abs(expected - actual) <= AlphaTolerance,
                    $"pixel {i} channel {c}: expected {expected} (straight), got {actual}. " +
                    $"A premultiplied decoder would return about {expected * rgba[i * 4 + 3] / 255}.");
            }
        }
    }

    /// <summary>
    /// All eight EXIF orientations, applied on decode.
    ///
    /// <para>The pixels encode their own source coordinates, so each transform is checked
    /// against the EXIF definition directly rather than against another decoder. This is the
    /// check that matters most on a phone: every photo from the camera app is stored on its
    /// side with an orientation tag, and a provider that ignores it rotates the user's entire
    /// photo library with nothing in the logs.</para>
    /// </summary>
    [Fact]
    public void Decode_AppliesEveryExifOrientation()
    {
        const int w = 4, h = 2;
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                rgba[p] = (byte)x; rgba[p + 1] = (byte)y; rgba[p + 2] = 0; rgba[p + 3] = 255;
            }
        byte[] plain = Image.EncodePng(rgba, w, h, 4);

        for (int orientation = 1; orientation <= 8; orientation++)
        {
            byte[] png = orientation == 1 ? plain : InjectPngOrientation(plain, orientation);
            byte[] outp = Image.DecodeRgba(png, out int ow, out int oh);

            bool transposed = orientation >= 5;
            Assert.Equal(transposed ? (h, w) : (w, h), (ow, oh));

            // ReadDimensions reports what is STORED, orientation not applied.
            Assert.Equal((w, h), Image.ReadDimensions(png));

            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    (int sx, int sy) = orientation switch
                    {
                        1 => (x, y),
                        2 => (w - 1 - x, y),
                        3 => (w - 1 - x, h - 1 - y),
                        4 => (x, h - 1 - y),
                        5 => (y, x),
                        6 => (y, h - 1 - x),
                        7 => (w - 1 - y, h - 1 - x),
                        _ => (w - 1 - y, x),
                    };
                    int p = (y * ow + x) * 4;
                    Assert.True(
                        outp[p] == sx && outp[p + 1] == sy,
                        $"orientation {orientation}: output ({x},{y}) came from ({outp[p]},{outp[p + 1]}), expected ({sx},{sy})");
                }
        }
    }

    /// <summary>The same, through a JPEG's APP1 Exif segment: a different container carrying
    /// the tag, which is the one a phone actually produces.</summary>
    [Fact]
    public void Decode_AppliesJpegExifOrientation()
    {
        // 8x4, left half white and right half black, stored with orientation 6 (rotate 90 CW
        // to view), so upright it is 4x8 with the white half on top.
        byte[] jpeg = Convert.FromBase64String(OrientedJpegBase64);
        Assert.True(Image.CanDecode(jpeg));

        byte[] pixels = Image.DecodeRgba(jpeg, out int w, out int h);
        Assert.Equal((4, 8), (w, h));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 4; x++)
            {
                int p = (y * 4 + x) * 4;
                if (y < 4)
                    Assert.True(pixels[p] > 200 && pixels[p + 1] > 200 && pixels[p + 2] > 200, $"({x},{y}) should be white");
                else
                    Assert.True(pixels[p] < 55 && pixels[p + 1] < 55 && pixels[p + 2] < 55, $"({x},{y}) should be black");
                Assert.Equal(255, pixels[p + 3]);
            }

        Assert.Equal((8, 4), Image.ReadDimensions(jpeg));
    }

    /// <summary>
    /// HEIC: decoded, or refused by name.
    ///
    /// <para>A provider that says <see cref="IImageCodec.CanDecode"/> is true has promised
    /// <see cref="IImageCodec.DecodeRgba"/> will not throw <see cref="NotSupportedException"/>,
    /// so this asserts one or the other and never lets a provider claim HEIC without reading
    /// it. The fixture is a real HEVC-in-HEIF file (what an iPhone camera writes, produced by
    /// macOS's own encoder) with four flat quadrants, so a wrong colour order or an upside-down
    /// blit is visible rather than lost in codec noise.</para>
    /// </summary>
    [Fact]
    public void Heic_IsDecodedOrRefusedByName()
    {
        byte[] heic = Convert.FromBase64String(HeicBase64);
        Assert.Equal(DecodesHeic, Image.CanDecode(heic));

        if (!DecodesHeic)
        {
            var ex = Assert.Throws<NotSupportedException>(() => Image.DecodeRgba(heic, out _, out _));
            Assert.Contains("ImageIO", ex.Message);
            Assert.Contains("MediaCodecs.Image", ex.Message);
            Assert.Throws<NotSupportedException>(() => Image.ReadDimensions(heic));
            return;
        }

        Assert.Equal((HeicWidth, HeicHeight), Image.ReadDimensions(heic));

        byte[] pixels = Image.DecodeRgba(heic, out int w, out int h);
        Assert.Equal((HeicWidth, HeicHeight), (w, h));
        Assert.Equal(w * h * 4, pixels.Length);

        for (int q = 0; q < 4; q++)
        {
            // Sample the middle of each quadrant, away from the block boundaries where a lossy
            // codec spends its error budget.
            int x = (q % 2) * (w / 2) + w / 4;
            int y = (q / 2) * (h / 2) + h / 4;
            int p = (y * w + x) * 4;
            (byte r, byte g, byte b) = HeicQuadrants[q];
            Assert.True(
                Math.Abs(pixels[p] - r) <= LossyTolerance &&
                Math.Abs(pixels[p + 1] - g) <= LossyTolerance &&
                Math.Abs(pixels[p + 2] - b) <= LossyTolerance,
                $"quadrant {q} at ({x},{y}) decoded as ({pixels[p]},{pixels[p + 1]},{pixels[p + 2]}), expected ({r},{g},{b})");
            Assert.Equal(255, pixels[p + 3]);
        }
    }

    [Fact]
    public void UnrecognisedBytes_AreRefusedNotGuessedAt()
    {
        byte[] junk = Enumerable.Range(0, 64).Select(i => (byte)(i * 37)).ToArray();
        Assert.False(Image.CanDecode(junk));
        Assert.Throws<NotSupportedException>(() => Image.DecodeRgba(junk, out _, out _));
        // ReadDimensions is allowed to report the failure as either "no codec for this" or
        // "this is not a readable header" — the providers word it differently — but never as
        // a size a caller would then allocate against.
        Assert.ThrowsAny<Exception>(() => Image.ReadDimensions(junk));

        Assert.Throws<ArgumentNullException>(() => Image.DecodeRgba(null!, out _, out _));
        Assert.Throws<ArgumentNullException>(() => Image.ReadDimensions(null!));
    }

    /// <summary>
    /// Resize is the managed Pillow Lanczos-3 on every provider, and bilinear is managed
    /// everywhere by design.
    ///
    /// <para>The generative pipelines (Qwen-Image edit, Wan, MiniMax-H3) were validated
    /// against ImageMagick's Lanczos-3, which the managed port matches to within a few levels
    /// on a smooth gradient. Nothing Apple ships is that filter — <c>kCGInterpolationHigh</c>
    /// is an unspecified quality tier and vImage's scaler is its own kernel — so a provider
    /// that "upgraded" to a platform resampler would move every resized pixel a model sees.
    /// This is the guard against that happening quietly.</para>
    /// </summary>
    [Fact]
    public void Resize_IsTheSameFilterOnEveryProvider()
    {
        const int sw = 64, sh = 48, dw = 32, dh = 24;
        byte[] rgb = Gradient(sw, sh, channels: 3, opaque: true);

        byte[] lanczos = Image.ResizeRgb8(rgb, sw, sh, dw, dh, ResizeFilter.Lanczos);
        Assert.Equal(dw * dh * 3, lanczos.Length);
        AssertClose(ManagedResampler.ResizeLanczosPillow(rgb, sw, sh, dw, dh), lanczos, tolerance: 8);

        byte[] bilinear = Image.ResizeRgb8(rgb, sw, sh, dw, dh, ResizeFilter.Bilinear);
        Assert.Equal(ManagedResampler.ResizeBilinearRgb8(rgb, sw, sh, dw, dh), bilinear);

        // Identity hands the input straight back, and a flat colour survives untouched.
        Assert.Same(rgb, Image.ResizeRgb8(rgb, sw, sh, sw, sh, ResizeFilter.Lanczos));
        byte[] flat = Enumerable.Repeat((byte)137, sw * sh * 3).ToArray();
        Assert.All(Image.ResizeRgb8(flat, sw, sh, dw, dh, ResizeFilter.Lanczos), b => Assert.Equal(137, b));

        Assert.Throws<ArgumentException>(() => Image.ResizeRgb8(rgb, sw + 1, sh, dw, dh, ResizeFilter.Lanczos));
        Assert.Throws<ArgumentNullException>(() => Image.ResizeRgb8(null!, sw, sh, dw, dh, ResizeFilter.Lanczos));
    }

    // ---- audio -----------------------------------------------------------------------

    /// <summary>
    /// The audio decoder returns the file's OWN rate and channel count, planar and unfolded.
    ///
    /// <para>Not 16 kHz mono: every audio model owns its resampler and its outputs are pinned
    /// by fixtures, so a provider that conformed here would silently change all of them. WAV
    /// is the format every provider can read, which makes it the one that can compare them.</para>
    /// </summary>
    [Fact]
    public void Audio_DecodesAtTheFilesOwnRateAndChannelCount()
    {
        const int rate = 22050, frames = 2205;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = (float)Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5f;
            right[i] = (float)Math.Sin(2 * Math.PI * 660 * i / rate) * 0.25f;
        }

        string wav = Path.Combine(_dir, "tone.wav");
        WavWriter.Write(wav, new[] { left, right }, rate);

        DecodedAudio decoded = Audio.Decode(wav);
        Assert.Equal(rate, decoded.SampleRate);
        Assert.Equal(2, decoded.ChannelCount);
        Assert.Equal(frames, decoded.SampleCount);

        // WavWriter stores 16-bit PCM, so the round trip is exact to within one LSB.
        for (int i = 0; i < frames; i++)
        {
            Assert.True(Math.Abs(decoded.Channels[0][i] - left[i]) < 2e-4f, $"left[{i}]");
            Assert.True(Math.Abs(decoded.Channels[1][i] - right[i]) < 2e-4f, $"right[{i}]");
        }

        Assert.Throws<FileNotFoundException>(() => Audio.Decode(Path.Combine(_dir, "absent.wav")));
        Assert.Throws<ArgumentNullException>(() => Audio.Decode(null!));
    }

    // ---- video -----------------------------------------------------------------------

    /// <summary>An unfilled video slot must say which slot to fill and what fills it, because
    /// that message is all a phone user's bug report will contain.</summary>
    [Fact]
    public void Video_SlotIsFilledOrRefusesWithTheSlotToFill()
    {
        string clip = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(clip, new byte[16]);

        if (Video == null)
        {
            var probe = Assert.Throws<NotSupportedException>(() => ManagedMediaProvider.Instance.Probe(clip));
            Assert.Contains("MediaCodecs.Video", probe.Message);
            Assert.Contains("AVFoundation", probe.Message);
            return;
        }

        Assert.Throws<FileNotFoundException>(() => Video.Probe(Path.Combine(_dir, "absent.mp4")));
        Assert.Throws<ArgumentNullException>(() => Video.Probe(null!));

        // 16 zero bytes are not a container. The contract allows either an exception or an
        // unusable (<= 0) rate/count that the caller then rejects — what it does not allow is
        // a plausible-looking geometry the caller would go on to sample frames from.
        try
        {
            VideoInfo junk = Video.Probe(clip);
            Assert.True(junk.Fps <= 0 || junk.FrameCount <= 0,
                $"a 16-byte non-container probed as {junk.FrameCount} frames at {junk.Fps} fps");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Saying so outright is the other permitted answer.
        }
    }

    /// <summary>
    /// Encode a clip with the provider's own encoder, then read the frames back with its own
    /// decoder and check each one is the frame that was asked for.
    ///
    /// <para>Frames are identified by the position of a white block, not by a colour level:
    /// position survives any amount of codec noise, so an off-by-one in the stepping or the
    /// seek path is unambiguous instead of hiding inside a tolerance. Both access paths are
    /// exercised — a dense request (which <see cref="MediaHelper.PrefersSequentialStepping"/>
    /// answers with stepping) and a sparse one plus a repeated index (which it answers with
    /// seeking, and which is how a slow clip is held onto a faster timeline).</para>
    /// </summary>
    [VideoFact]
    public void Video_EncodesAndReadsBackTheFramesItWasAskedFor()
    {
        const int width = 64, height = 48, count = 30, fps = 15;
        string path = Path.Combine(_dir, "roundtrip.mp4");

        if (Encoder == null || Video == null)
        {
            // The managed provider fills neither slot; the refusal is the contract.
            var ex = Assert.Throws<NotSupportedException>(
                () => ManagedMediaProvider.Instance.SaveMp4(path, new[] { new RgbImage(2, 2, new float[12]) }, fps));
            Assert.Contains("MediaCodecs.VideoEncoder", ex.Message);
            Assert.False(File.Exists(path));
            return;
        }

        var frames = new RgbImage[count];
        for (int i = 0; i < count; i++)
            frames[i] = MarkerFrame(i, width, height);

        string codec = Encoder.SaveMp4(path, frames, fps);
        Assert.Equal("h264", codec);
        Assert.True(new FileInfo(path).Length > 0);

        VideoInfo info = Video.Probe(path);
        Assert.Equal((width, height), (info.Width, info.Height));
        Assert.True(Math.Abs(info.Fps - fps) <= 0.5, $"fps {info.Fps} should be about {fps}");
        Assert.True(Math.Abs(info.FrameCount - count) <= 2, $"frame count {info.FrameCount} should be about {count}");

        // Dense: stepping.
        int[] dense = { 0, 1, 2, 3, 4 };
        Assert.True(MediaHelper.PrefersSequentialStepping(dense));
        AssertFramesAre(path, dense, width, height);

        // Sparse, and with a repeat: seeking, and the same frame delivered twice.
        int[] sparse = { 0, 14, 14, 29 };
        Assert.False(MediaHelper.PrefersSequentialStepping(sparse));
        AssertFramesAre(path, sparse, width, height);
    }

    /// <summary>Read the requested frames and require each delivered one to carry the marker of
    /// the index it was asked for.</summary>
    private void AssertFramesAre(string path, int[] requested, int width, int height)
    {
        var seen = new List<(int requestedIndex, int markerIndex)>();
        Video!.ReadFrames(path, requested, (index, pixels, w, h, stride, layout) =>
        {
            Assert.Equal((width, height), (w, h));
            Assert.True(stride >= w * BytesPerPixel(layout), $"stride {stride} is shorter than a row");
            seen.Add((index, ReadMarker(pixels, w, h, stride, layout)));
        });

        // A short read is how a clip ends, not an error — but this clip is long enough that
        // every requested frame must arrive.
        Assert.Equal(requested.Length, seen.Count);
        for (int i = 0; i < requested.Length; i++)
        {
            Assert.Equal(requested[i], seen[i].requestedIndex);
            Assert.Equal(requested[i], seen[i].markerIndex);
        }
    }

    // ---- fixtures and helpers --------------------------------------------------------

    /// <summary>Frame <paramref name="index"/> of the synthetic clip: a dark field with one
    /// white 8x8 block whose cell position spells the index in base 8.</summary>
    private static RgbImage MarkerFrame(int index, int width, int height)
    {
        const int cell = 8;
        int columns = width / cell;
        int bx = (index % columns) * cell;
        int by = (index / columns) * cell;

        var pixels = new float[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool marked = x >= bx && x < bx + cell && y >= by && y < by + cell;
                int p = (y * width + x) * 3;
                // Mid-grey rather than black so the frame is not degenerate for the encoder,
                // and well inside the limited range H.264 stores.
                float v = marked ? 0.92f : 0.24f;
                pixels[p] = v; pixels[p + 1] = v; pixels[p + 2] = v;
            }
        return new RgbImage(width, height, pixels);
    }

    /// <summary>The frame index a decoded frame carries, found as the brightest 8x8 cell.</summary>
    private static int ReadMarker(byte[] pixels, int width, int height, int stride, PixelLayout layout)
    {
        const int cell = 8;
        int columns = width / cell;
        int rows = height / cell;
        int bytesPerPixel = BytesPerPixel(layout);

        int best = -1;
        long bestSum = -1;
        for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < columns; cx++)
            {
                long sum = 0;
                for (int y = cy * cell; y < (cy + 1) * cell; y++)
                    for (int x = cx * cell; x < (cx + 1) * cell; x++)
                    {
                        int p = y * stride + x * bytesPerPixel;
                        sum += pixels[p] + pixels[p + 1] + pixels[p + 2];
                    }
                if (sum > bestSum)
                {
                    bestSum = sum;
                    best = cy * columns + cx;
                }
            }

        // The marker is genuinely bright: a frame of uniform noise would otherwise "identify"
        // as whichever cell won by a byte.
        Assert.True(bestSum > 3L * cell * cell * 160, $"no bright marker cell found (best cell mean {bestSum / (3.0 * cell * cell):0})");
        return best;
    }

    private static int BytesPerPixel(PixelLayout layout) =>
        layout is PixelLayout.Bgra or PixelLayout.Rgba ? 4 : 3;

    /// <summary>Deterministic, non-degenerate pixels (no two rows alike, every channel used).</summary>
    private static byte[] Gradient(int w, int h, int channels, bool opaque)
    {
        byte[] px = new byte[w * h * channels];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * channels;
                px[p] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[p + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[p + 2] = (byte)((x * 31 + y * 17) % 256);
                if (channels == 4) px[p + 3] = opaque ? (byte)255 : (byte)(255 - (x + y) * 7 % 256);
            }
        return px;
    }

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        int worst = 0, at = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - actual[i]);
            if (d > worst) { worst = d; at = i; }
        }
        Assert.True(worst <= tolerance, $"max channel difference {worst} at index {at} exceeds {tolerance}");
    }

    /// <summary>A minimal little-endian TIFF block with just IFD0 { Orientation = value }.</summary>
    private static byte[] TiffOrientation(int orientation) => new byte[]
    {
        (byte)'I', (byte)'I', 0x2A, 0x00,
        0x08, 0x00, 0x00, 0x00,
        0x01, 0x00,
        0x12, 0x01, 0x03, 0x00,
        0x01, 0x00, 0x00, 0x00,
        (byte)orientation, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>Insert an eXIf chunk straight after IHDR (byte 33 of every PNG).</summary>
    private static byte[] InjectPngOrientation(byte[] png, int orientation)
    {
        byte[] tiff = TiffOrientation(orientation);
        uint crc = PngCodec.ChunkCrc("eXIf", tiff);
        var chunk = new List<byte>();
        chunk.AddRange(new[] { (byte)(tiff.Length >> 24), (byte)(tiff.Length >> 16), (byte)(tiff.Length >> 8), (byte)tiff.Length });
        chunk.AddRange("eXIf"u8.ToArray());
        chunk.AddRange(tiff);
        chunk.AddRange(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        return png.Take(33).Concat(chunk).Concat(png.Skip(33)).ToArray();
    }

    /// <summary>8x4, left half white / right half black, quality 100 with no chroma
    /// subsampling (so the hard edge stays put), carrying EXIF orientation 6. Embedded rather
    /// than synthesised so the fixture needs no image library and is identical on every
    /// platform this harness is compiled for.</summary>
    private const string OrientedJpegBase64 =
        "/9j/4QAiRXhpZgAASUkqAAgAAAABABIBAwABAAAABgAAAAAAAAD/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAAEAAgBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAA//E" +
        "ABgQAAIDAAAAAAAAAAAAAAAAAAAKSYjI/9oACAEBAAA/ADeci6uzkc//2Q==";

    private const int HeicWidth = 64;
    private const int HeicHeight = 48;

    /// <summary>Top-left, top-right, bottom-left, bottom-right.</summary>
    private static readonly (byte r, byte g, byte b)[] HeicQuadrants =
    {
        (220, 40, 40), (40, 200, 80), (50, 90, 230), (240, 220, 60),
    };

    /// <summary>A real HEVC-in-HEIF still (64x48, four flat quadrants) written by macOS's own
    /// encoder — the container an iPhone camera produces, which is the format the managed
    /// decoder cannot read and the platform providers exist for.</summary>
    private const string HeicBase64 =
        "AAAAJGZ0eXBoZWljAAAAAG1pZjFNaUhFTWlQcm1pYWZoZWljAAABhm1ldGEAAAAAAAAAIWhkbHIAAAAAAAAAAHBpY3QAAAAAAAAAAAAA" +
        "AAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAADnBpdG0AAAAAAAEAAAAjaWluZgAAAAAAAQAAABVpbmZlAgAA" +
        "AAABAABodmMxAAAAAOZpcHJwAAAAxWlwY28AAAATY29scm5jbHgAAgACAAaAAAAADGNsbGkAywBAAAAAFGlzcGUAAAAAAAAAQAAAADAA" +
        "AAAJaXJvdAAAAAAQcGl4aQAAAAADCAgIAAAAcWh2Y0MBBAgAAAC+CAAAAAAe8AD8//j4AAALA6AAAQAXQAEMAf//BAgAAAMAvggAAAMA" +
        "AB4XAkChAAEAI0IBAQQIAAADAL4IAAADAAAekAKECDgYYRxAvciwqbgQEDAEogABAAlEAcBg1BCKqyAAAAAZaXBtYQAAAAAAAAABAAEG" +
        "gQIDBYaEAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAAboAAAMOAAAAAW1kYXQAAAAAAAADHgAAAwooAa6FX9Au/H+7C9//8qI8NxM+fPn0" +
        "R48ePHjxy5//+c1l//+R8ZvEd999+SSSSSSSJO//FTYAXdkTsWq55RKapqmqaqCIIgiCIIgiCILupiD7xe9j48YYLX/yOQALPBa4Ft7y" +
        "4QtAtAtAtAtttttttttttttttCJmqADbs4NlTXmqFGpGpGpGpG22222222222222pyi4Amcp1k7Y7kKfj/j/j/j/mZmZmZmZmZmZmZmU" +
        "vAAs8FrgW3vLhC0C0C0C0C222222222222220UtwB51ZWqNpmT2B8B8B8B8CDqDqDqDqDqDqDqCp8AIbREKESc2KMhwhwhwhwiSSSSSS" +
        "SSSSSSSR2GwBz16uubqs0cSoSoSoSoSviviviviviviviudUATuSIyHMX6CDuTuTuTuTvivivivivivivivef////KyJuV//+rn1T9L+" +
        "c+K+K+K+K+MGMGMGMGMGMGMGLBsv//7T/Z/1/6f5f5f5f5f5j5j5j5j5j5j5j5gST///Mb5d+R/De4e4e4e4e5K5K5K5K5K5K5K5F7//" +
        "/vTN4ncXsZo1o1o1o1pD5D5D5D5D5D5D43iH//4WJB8vq3BFYlYlYlYlfHfHfHfHfHfHfHd7n//9U5qU6S83MSsSsSsSsU+U+U+U+U+U" +
        "+U+TJX//9i9sG6w9HsmMmMmMmMoSoSoSoSoSoSoSn4K//+2dmr1jGpsaAaAaAaAa/K/K/K/K/K/K/K8H////8+Y0QRIAAF8sbVdidRq1" +
        "atW3Xr169evTVX64Z/yqrJJJHUR0TP//7S92jSZi3ByKKKKKKKKLerererererereqfgA00gweT1aiqqiAAJC4oXAfhT5yjKMoyjMdx3" +
        "Hcdx3Hcdq2RAK//9XOp6jIq9vb29vpaWlpaWlpXY4boAf8x/l/5P8X7ft+37fue57nue57nue4hPaIYJnciX/+QtnOc5z6MYxjFu5fYA" +
        "V5dyO90ppppqhQoUKFChAJ44AccfjjuIM802zbNs2ziWJYliWJYliV2+4hsVlIId2M8cLLcs";
}

/// <summary>The managed default: no HEIC, no video, straight alpha out of its own PNG decoder.</summary>
public sealed class ManagedProviderParityTests : MediaProviderParityTests
{
    protected override IImageCodec Image => ManagedMediaProvider.Instance;
    protected override IAudioDecoder Audio => ManagedMediaProvider.Instance;
    protected override IVideoDecoder? Video => null;
    protected override IVideoEncoder? Encoder => null;
    protected override bool DecodesHeic => false;
}

/// <summary>The desktop stack: Magick.NET (HEIC via libheif) plus OpenCV/ffmpeg video. Audio
/// was never native on desktop, so it stays on the managed decoders — which is itself part of
/// the contract this pins.</summary>
public sealed class DesktopProviderParityTests : MediaProviderParityTests
{
    protected override IImageCodec Image => DesktopMediaProvider.Instance;
    protected override IAudioDecoder Audio => ManagedMediaProvider.Instance;
    protected override IVideoDecoder? Video => DesktopMediaProvider.Instance;
    protected override IVideoEncoder? Encoder => DesktopMediaProvider.Instance;
    protected override bool DecodesHeic => true;
}

#if IOS || MACCATALYST
/// <summary>
/// The Apple stack. Compiled in only when this test project is built for an Apple target
/// framework, which a net10.0 desktop test host is not — so on CI this class does not exist
/// and the two subclasses above are what run. It is written out in full anyway because it is
/// the contract the iOS provider is held to: nothing about the assertions above is
/// desktop-specific, and the day this project grows an iOS head the provider is already
/// covered by them.
/// </summary>
public sealed class AppleProviderParityTests : MediaProviderParityTests
{
    private static TensorSharp.Models.Media.Apple.AppleMediaProvider Provider =>
        TensorSharp.Models.Media.Apple.AppleMediaProvider.Instance;

    protected override IImageCodec Image => Provider;
    protected override IAudioDecoder Audio => Provider;
    protected override IVideoDecoder? Video => Provider;
    protected override IVideoEncoder? Encoder => Provider;
    protected override bool DecodesHeic => true;

    /// <summary>Core Graphics has no straight-alpha 8-bit RGBA bitmap layout, so a decoded
    /// image that carried alpha is un-premultiplied arithmetically; at the alphas this harness
    /// uses (>= 64) that costs at most two levels.</summary>
    protected override int AlphaTolerance => 3;
}
#endif
