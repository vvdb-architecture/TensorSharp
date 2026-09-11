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
using TensorSharp.Models.QwenImage;
using TensorSharp.Models.Video;

namespace TensorAgent.Tests;

/// <summary>
/// The pictures, clips and sounds the media tests run against, generated here rather
/// than checked in.
///
/// <para>
/// Two reasons, and only the second is about repository hygiene. The first is that a
/// test which asserts what a model saw has to know what was in the picture, and a
/// binary fixture only ever tells you its file name — "a large red disc, centred,
/// covering half the frame" is a claim this file can keep and a committed
/// <c>cat.jpg</c> cannot. The second is that a fixture built through
/// <see cref="MediaCodecs"/> exercises the seam the app itself goes through, so a
/// provider that cannot encode a PNG or an MP4 fails here rather than in the middle
/// of an upload.
/// </para>
/// <para>
/// The JPEG writer below is the exception: nothing in the seam encodes JPEG, and a
/// JPEG is the one format needed to set an EXIF orientation the way an iPhone does.
/// It is deliberately self-contained, so this file needs no image library at all and
/// stays usable from a build that has none.
/// </para>
/// </summary>
internal static class MediaFixtures
{
    // ---- still images -------------------------------------------------------

    /// <summary>
    /// A red disc on white, filling most of the frame. Unambiguous enough that a
    /// vision model's answer can be asserted on: there is exactly one object, it has
    /// one shape and one colour, and nothing else is in the picture to describe
    /// instead.
    /// </summary>
    public static byte[] RedCircleOnWhitePng(int size = 448)
    {
        byte[] rgb = new byte[size * size * 3];
        Array.Fill(rgb, (byte)255);

        double centre = (size - 1) / 2.0;
        double radius = size * 0.38;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - centre, dy = y - centre;
                if (dx * dx + dy * dy > radius * radius)
                    continue;
                int p = (y * size + x) * 3;
                rgb[p] = 220;
                rgb[p + 1] = 20;
                rgb[p + 2] = 20;
            }
        }
        return MediaCodecs.Image.EncodePng(rgb, size, size, 3);
    }

    /// <summary>Packed RGB8 split down the middle, left half <paramref name="left"/>,
    /// right half <paramref name="right"/>. The seam between the halves lands on an 8-pixel
    /// boundary when <paramref name="width"/> is a multiple of 16, which is what lets the
    /// JPEG below come back out of a decoder unchanged.</summary>
    public static byte[] SideBySideRgb(int width, int height, (byte R, byte G, byte B) left, (byte R, byte G, byte B) right)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = x < width / 2 ? left : right;
                int p = (y * width + x) * 3;
                rgb[p] = r; rgb[p + 1] = g; rgb[p + 2] = b;
            }
        }
        return rgb;
    }

    // ---- JPEG ---------------------------------------------------------------

    /// <summary>
    /// Baseline JPEG, 4:4:4, one Huffman table pair shared by all three components.
    ///
    /// <para>
    /// It exists because <see cref="IImageCodec"/> encodes PNG and nothing else, and
    /// the EXIF-orientation contract that the whole iPhone-photo path depends on can
    /// only be stated in a JPEG. Quality is fixed and coarse — the point is a file a
    /// decoder accepts, not a small one — and a picture whose colour changes only on
    /// 8-pixel boundaries survives it exactly, because every block is then flat and
    /// carries no AC coefficients at all.
    /// </para>
    /// </summary>
    public static byte[] Jpeg(byte[] rgb8, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgb8);
        if (rgb8.Length != width * height * 3)
            throw new ArgumentException($"expected {width}x{height}x3 bytes, got {rgb8.Length}", nameof(rgb8));

        using var output = new MemoryStream();
        WriteSegment(output, 0xD8);
        WriteQuantizationTable(output);
        WriteFrameHeader(output, width, height);
        WriteHuffmanTables(output);
        WriteScanHeader(output);
        WriteScan(output, rgb8, width, height);
        WriteSegment(output, 0xD9);
        return output.ToArray();
    }

    /// <summary>
    /// Splice an APP1 Exif segment carrying <paramref name="orientation"/> in right
    /// after the SOI, which is where a camera puts it. A decoder that honours it
    /// returns the picture upright; one that ignores it returns the stored pixels, and
    /// the difference is visible in the dimensions alone for orientations 5 to 8.
    /// </summary>
    public static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new ArgumentException("not a JPEG: no SOI marker", nameof(jpeg));

        byte[] tiff =
        [
            0x4D, 0x4D, 0x00, 0x2A,                  // big-endian TIFF header
            0x00, 0x00, 0x00, 0x08,                  // offset of IFD0
            0x00, 0x01,                              // one entry
            0x01, 0x12,                              // tag 0x0112, Orientation
            0x00, 0x03,                              // type SHORT
            0x00, 0x00, 0x00, 0x01,                  // count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                  // no IFD1
        ];

        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        int length = payload.Length + 2;

        byte[] result = new byte[jpeg.Length + 4 + payload.Length];
        result[0] = 0xFF; result[1] = 0xD8;
        result[2] = 0xFF; result[3] = 0xE1;
        result[4] = (byte)(length >> 8); result[5] = (byte)length;
        payload.CopyTo(result, 6);
        Array.Copy(jpeg, 2, result, 6 + payload.Length, jpeg.Length - 2);
        return result;
    }

    // ---- audio --------------------------------------------------------------

    /// <summary>
    /// A pure tone at <paramref name="hertz"/>, through the app's own WAV writer.
    /// A tone rather than speech because nothing here can synthesise speech, and a
    /// test that claims a model transcribed a word it was never given would be worse
    /// than no test at all.
    /// </summary>
    public static byte[] ToneWav(double seconds = 3.0, int hertz = 440, int sampleRate = 16000)
    {
        int frames = (int)(seconds * sampleRate);
        float[] samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            // A short fade at each end keeps the click of a hard start out of the
            // spectrum, which is the sort of artefact an audio encoder would show.
            double fade = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (sampleRate * 0.05));
            samples[i] = (float)(0.6 * fade * Math.Sin(2 * Math.PI * hertz * i / sampleRate));
        }
        return WavWriter.Encode([samples], sampleRate);
    }

    // ---- video --------------------------------------------------------------

    /// <summary>
    /// A clip whose two halves are plainly different: solid red, then solid blue.
    ///
    /// <para>
    /// Written through <see cref="MediaCodecs.VideoEncoder"/>, which is the same
    /// encoder the video-generation route serves its output from, so a machine that
    /// cannot produce a playable MP4 says so here instead of during a generation. The
    /// returned codec is the encoder's own answer — <c>mp4v</c> means the file plays
    /// in VLC but not in a browser, which a caller may want to report.
    /// </para>
    /// </summary>
    public static byte[] TwoHalvesMp4(
        string scratchDirectory, out string codec,
        int width = 160, int height = 128, int frames = 24, int fps = 12)
    {
        ArgumentNullException.ThrowIfNull(scratchDirectory);
        Directory.CreateDirectory(scratchDirectory);
        string path = Path.Combine(scratchDirectory, $"fixture-{Guid.NewGuid():N}.mp4");

        var images = new RgbImage[frames];
        for (int i = 0; i < frames; i++)
        {
            bool first = i < frames / 2;
            images[i] = Solid(width, height, first ? (0.85f, 0.06f, 0.06f) : (0.06f, 0.10f, 0.85f));
        }

        codec = MediaCodecs.VideoEncoder.SaveMp4(path, images, fps);
        byte[] bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static RgbImage Solid(int width, int height, (float R, float G, float B) colour)
    {
        float[] pixels = new float[width * height * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = colour.R;
            pixels[i + 1] = colour.G;
            pixels[i + 2] = colour.B;
        }
        return new RgbImage(width, height, pixels);
    }

    // ---- the JPEG writer ----------------------------------------------------

    /// <summary>Every coefficient quantised by the same step. Coarse on purpose: a flat
    /// 8x8 block has only a DC coefficient, and rounding one DC term costs at most half a
    /// level of the colour it came from.</summary>
    private const int QuantStep = 8;

    private static readonly int[] Zigzag =
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly double[,] Cosines = BuildCosines();

    private static double[,] BuildCosines()
    {
        var table = new double[8, 8];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return table;
    }

    /// <summary>The DC symbols: a magnitude category from 0 to 11, which is the whole
    /// baseline range.</summary>
    private static readonly byte[] DcSymbols = [.. Enumerable.Range(0, 12).Select(i => (byte)i)];

    /// <summary>
    /// The AC symbols: end-of-block, zero-run-length, and every (run, category) pair
    /// baseline permits. Listing all of them rather than the standard tables' selection
    /// costs a few bits per block and removes the possibility of emitting a symbol the
    /// table does not carry, which is a corrupt file no decoder explains well.
    /// </summary>
    private static readonly byte[] AcSymbols = BuildAcSymbols();

    private static byte[] BuildAcSymbols()
    {
        var symbols = new List<byte> { 0x00, 0xF0 };
        for (int run = 0; run < 16; run++)
            for (int size = 1; size <= 11; size++)
                symbols.Add((byte)((run << 4) | size));
        return [.. symbols];
    }

    /// <summary>
    /// Code lengths for <paramref name="count"/> symbols spread over two adjacent
    /// depths, which needs no frequency analysis and which every canonical-Huffman
    /// decoder accepts. Returns the JPEG BITS array (index 1..16 = how many codes are
    /// that long).
    ///
    /// <para>
    /// The tree is built for one symbol more than there are and the extra leaf is then
    /// dropped, so the all-ones codeword is never handed out. That is not an
    /// optimisation: libjpeg rejects a table whose longest code is all ones outright
    /// ("Bogus Huffman table definition"), because such a code cannot be distinguished
    /// from the padding that ends an entropy-coded segment.
    /// </para>
    /// </summary>
    private static int[] CodeLengths(int count)
    {
        int deep = 2;
        while ((1 << deep) < count + 1)
            deep++;
        int shallow = (1 << deep) - (count + 1);   // leaves promoted one level up
        var bits = new int[17];
        bits[deep - 1] = shallow;
        bits[deep] = count - shallow;
        return bits;
    }

    private static (int[] Code, int[] Length) CanonicalCodes(byte[] symbols)
    {
        int[] bits = CodeLengths(symbols.Length);
        var code = new int[256];
        var length = new int[256];
        int next = 0, index = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len]; i++, index++, next++)
            {
                code[symbols[index]] = next;
                length[symbols[index]] = len;
            }
            next <<= 1;
        }
        return (code, length);
    }

    private static readonly (int[] Code, int[] Length) DcCodes = CanonicalCodes(DcSymbols);
    private static readonly (int[] Code, int[] Length) AcCodes = CanonicalCodes(AcSymbols);

    private static void WriteSegment(Stream output, byte marker)
    {
        output.WriteByte(0xFF);
        output.WriteByte(marker);
    }

    private static void WriteSegment(Stream output, byte marker, ReadOnlySpan<byte> body)
    {
        WriteSegment(output, marker);
        int length = body.Length + 2;
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
        output.Write(body);
    }

    private static void WriteQuantizationTable(Stream output)
    {
        Span<byte> body = stackalloc byte[65];
        body[0] = 0x00;                                  // 8-bit precision, table 0
        for (int i = 1; i <= 64; i++)
            body[i] = (byte)QuantStep;
        WriteSegment(output, 0xDB, body);
    }

    private static void WriteFrameHeader(Stream output, int width, int height)
    {
        Span<byte> body = stackalloc byte[6 + 3 * 3];
        body[0] = 8;
        body[1] = (byte)(height >> 8); body[2] = (byte)height;
        body[3] = (byte)(width >> 8); body[4] = (byte)width;
        body[5] = 3;
        for (int c = 0; c < 3; c++)
        {
            body[6 + c * 3] = (byte)(c + 1);
            body[7 + c * 3] = 0x11;                      // no subsampling: 4:4:4
            body[8 + c * 3] = 0x00;                      // quantisation table 0
        }
        WriteSegment(output, 0xC0, body);
    }

    private static void WriteHuffmanTables(Stream output)
    {
        WriteHuffmanTable(output, 0x00, DcSymbols);
        WriteHuffmanTable(output, 0x10, AcSymbols);
    }

    private static void WriteHuffmanTable(Stream output, byte id, byte[] symbols)
    {
        int[] bits = CodeLengths(symbols.Length);
        byte[] body = new byte[1 + 16 + symbols.Length];
        body[0] = id;
        for (int len = 1; len <= 16; len++)
            body[len] = (byte)bits[len];
        symbols.CopyTo(body, 17);
        WriteSegment(output, 0xC4, body);
    }

    private static void WriteScanHeader(Stream output)
    {
        Span<byte> body = stackalloc byte[1 + 3 * 2 + 3];
        body[0] = 3;
        for (int c = 0; c < 3; c++)
        {
            body[1 + c * 2] = (byte)(c + 1);
            body[2 + c * 2] = 0x00;                      // DC table 0, AC table 0
        }
        body[7] = 0; body[8] = 63; body[9] = 0;
        WriteSegment(output, 0xDA, body);
    }

    private static void WriteScan(Stream output, byte[] rgb8, int width, int height)
    {
        var writer = new BitWriter(output);
        var block = new double[64];
        var coefficients = new double[64];
        var quantised = new int[64];
        int[] previousDc = new int[3];

        for (int blockY = 0; blockY < (height + 7) / 8; blockY++)
        {
            for (int blockX = 0; blockX < (width + 7) / 8; blockX++)
            {
                for (int component = 0; component < 3; component++)
                {
                    FillBlock(block, rgb8, width, height, blockX * 8, blockY * 8, component);
                    Transform(block, coefficients);
                    for (int i = 0; i < 64; i++)
                        quantised[i] = (int)Math.Round(coefficients[Zigzag[i]] / QuantStep);
                    EncodeBlock(writer, quantised, ref previousDc[component]);
                }
            }
        }
        writer.Flush();
    }

    /// <summary>One 8x8 component block, level-shifted to [-128, 127]. Blocks that run off
    /// the edge repeat the last pixel, which is what keeps the padding from showing up as
    /// high-frequency coefficients at the border.</summary>
    private static void FillBlock(double[] block, byte[] rgb8, int width, int height, int originX, int originY, int component)
    {
        for (int y = 0; y < 8; y++)
        {
            int sy = Math.Min(originY + y, height - 1);
            for (int x = 0; x < 8; x++)
            {
                int sx = Math.Min(originX + x, width - 1);
                int p = (sy * width + sx) * 3;
                double r = rgb8[p], g = rgb8[p + 1], b = rgb8[p + 2];
                block[y * 8 + x] = component switch
                {
                    0 => 0.299 * r + 0.587 * g + 0.114 * b,
                    1 => -0.168736 * r - 0.331264 * g + 0.5 * b + 128,
                    _ => 0.5 * r - 0.418688 * g - 0.081312 * b + 128,
                } - 128;
            }
        }
    }

    private static void Transform(double[] block, double[] coefficients)
    {
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        sum += block[y * 8 + x] * Cosines[u, x] * Cosines[v, y];
                double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                coefficients[v * 8 + u] = 0.25 * cu * cv * sum;
            }
        }
    }

    private static void EncodeBlock(BitWriter writer, int[] zigzag, ref int previousDc)
    {
        int diff = zigzag[0] - previousDc;
        previousDc = zigzag[0];
        (int size, int value) = Magnitude(diff);
        writer.Write(DcCodes.Code[size], DcCodes.Length[size]);
        if (size > 0)
            writer.Write(value, size);

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            if (zigzag[k] == 0)
            {
                run++;
                continue;
            }
            while (run > 15)
            {
                writer.Write(AcCodes.Code[0xF0], AcCodes.Length[0xF0]);
                run -= 16;
            }
            (size, value) = Magnitude(zigzag[k]);
            int symbol = (run << 4) | size;
            writer.Write(AcCodes.Code[symbol], AcCodes.Length[symbol]);
            writer.Write(value, size);
            run = 0;
        }
        if (run > 0)
            writer.Write(AcCodes.Code[0x00], AcCodes.Length[0x00]);
    }

    /// <summary>JPEG's magnitude category and the bits that follow it: a negative value is
    /// stored as its one's complement within the category.</summary>
    private static (int Size, int Bits) Magnitude(int value)
    {
        int size = 0;
        for (int magnitude = Math.Abs(value); magnitude > 0; magnitude >>= 1)
            size++;
        return (size, value >= 0 ? value : value + (1 << size) - 1);
    }

    /// <summary>
    /// The entropy-coded segment's bit stream. Every 0xFF byte is followed by a zero,
    /// because an unescaped 0xFF is how a decoder recognises the next marker and would
    /// end the scan in the middle of a block.
    /// </summary>
    private sealed class BitWriter(Stream output)
    {
        private int _accumulator;
        private int _bits;

        public void Write(int code, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _accumulator = (_accumulator << 1) | ((code >> i) & 1);
                if (++_bits != 8)
                    continue;
                output.WriteByte((byte)_accumulator);
                if ((byte)_accumulator == 0xFF)
                    output.WriteByte(0x00);
                _accumulator = 0;
                _bits = 0;
            }
        }

        /// <summary>Pad the final byte with ones, which is the only padding a baseline
        /// decoder is required to skip.</summary>
        public void Flush()
        {
            while (_bits != 0)
                Write(1, 1);
        }
    }
}
