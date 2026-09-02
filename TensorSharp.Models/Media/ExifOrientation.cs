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

namespace TensorSharp.Models.Media
{
    /// <summary>
    /// EXIF orientation (tag 0x0112) for the managed image codec.
    ///
    /// <para>A phone stores the sensor's pixels as captured and records how to turn them
    /// upright; every reference loader the generative pipelines were matched against applies
    /// that on load (ImageMagick's <c>AutoOrient</c>, Pillow's <c>exif_transpose</c>). stb_image
    /// does not, so the managed default reads the tag itself and applies the same eight
    /// transforms.</para>
    /// </summary>
    internal static class ExifOrientation
    {
        /// <summary>The orientation from a JPEG's APP1 Exif segment; 1 (upright) when absent.</summary>
        internal static int ReadFromJpeg(byte[] data)
        {
            if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                return 1;

            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF)
                    return 1;   // lost sync; not worth guessing
                int marker = data[pos + 1];
                if (marker == 0xFF) { pos++; continue; }                  // fill byte
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    pos += 2;                                             // standalone markers carry no length
                    continue;
                }
                if (marker == 0xDA || marker == 0xD9)
                    return 1;                                             // SOS / EOI: headers are over

                int length = (data[pos + 2] << 8) | data[pos + 3];
                if (length < 2 || pos + 2 + length > data.Length)
                    return 1;
                if (marker == 0xE1 && length >= 8 &&
                    data[pos + 4] == (byte)'E' && data[pos + 5] == (byte)'x' && data[pos + 6] == (byte)'i' &&
                    data[pos + 7] == (byte)'f' && data[pos + 8] == 0 && data[pos + 9] == 0)
                {
                    return ReadFromTiff(data, pos + 10, length - 8);
                }
                pos += 2 + length;
            }
            return 1;
        }

        /// <summary>The orientation from a TIFF-structured Exif block (byte-order mark, 42,
        /// IFD0 offset, IFD0 entries) starting at <paramref name="start"/>; 1 when absent.</summary>
        internal static int ReadFromTiff(byte[] data, int start, int length)
        {
            if (data == null || start < 0 || length < 8 || start + length > data.Length)
                return 1;

            bool little;
            if (data[start] == (byte)'I' && data[start + 1] == (byte)'I') little = true;
            else if (data[start] == (byte)'M' && data[start + 1] == (byte)'M') little = false;
            else return 1;

            int end = start + length;
            if (U16(data, start + 2, little) != 42)
                return 1;

            long ifd = start + U32(data, start + 4, little);
            if (ifd < start || ifd + 2 > end)
                return 1;

            int count = U16(data, (int)ifd, little);
            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + i * 12L;
                if (entry + 12 > end)
                    return 1;
                if (U16(data, (int)entry, little) != 0x0112)
                    continue;
                if (U16(data, (int)entry + 2, little) != 3)   // SHORT
                    return 1;
                int value = U16(data, (int)entry + 8, little);
                return value is >= 1 and <= 8 ? value : 1;
            }
            return 1;
        }

        private static int U16(byte[] d, int o, bool little) =>
            little ? d[o] | (d[o + 1] << 8) : (d[o] << 8) | d[o + 1];

        private static uint U32(byte[] d, int o, bool little) =>
            little
                ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24))
                : (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        /// <summary>
        /// Return the upright pixels for RGBA8 stored with <paramref name="orientation"/>,
        /// updating <paramref name="width"/>/<paramref name="height"/> (swapped for 5..8).
        /// Orientation 1 (or anything out of range) returns the input buffer untouched.
        /// </summary>
        internal static byte[] Apply(byte[] rgba, ref int width, ref int height, int orientation)
        {
            if (orientation <= 1 || orientation > 8 || rgba == null)
                return rgba;

            int w = width, h = height;
            bool transposed = orientation >= 5;
            int outW = transposed ? h : w;
            int outH = transposed ? w : h;
            byte[] result = new byte[(long)outW * outH * 4];

            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    int sx, sy;
                    switch (orientation)
                    {
                        case 2: sx = w - 1 - x; sy = y; break;               // mirror horizontal
                        case 3: sx = w - 1 - x; sy = h - 1 - y; break;       // rotate 180
                        case 4: sx = x; sy = h - 1 - y; break;               // mirror vertical
                        case 5: sx = y; sy = x; break;                       // transpose
                        case 6: sx = y; sy = h - 1 - x; break;               // rotate 90 CW
                        case 7: sx = w - 1 - y; sy = h - 1 - x; break;       // transverse
                        default: sx = w - 1 - y; sy = x; break;              // 8: rotate 90 CCW
                    }
                    Buffer.BlockCopy(rgba, (sy * w + sx) * 4, result, (y * outW + x) * 4, 4);
                }
            }

            width = outW;
            height = outH;
            return result;
        }
    }
}
