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
    /// <summary>Container formats told apart by their leading bytes.</summary>
    internal enum ImageFormat
    {
        Unknown,
        Png,
        Jpeg,
        Heic,
        Gif,
        Bmp,
        WebP,
        Psd,
        Tiff,
    }

    /// <summary>
    /// Magic-byte sniffing shared by every codec. Dispatch is by content, never by file
    /// extension, because uploads arrive under GUID names and phones lie about extensions
    /// (a ".jpg" from Photos is routinely HEIC).
    /// </summary>
    internal static class ImageFormatSniffer
    {
        internal static ImageFormat Detect(ReadOnlySpan<byte> d)
        {
            if (IsPng(d)) return ImageFormat.Png;
            if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xD8) return ImageFormat.Jpeg;
            if (IsHeic(d)) return ImageFormat.Heic;
            if (d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' &&
                d[3] == (byte)'8' && (d[4] == (byte)'7' || d[4] == (byte)'9') && d[5] == (byte)'a')
                return ImageFormat.Gif;
            if (d.Length >= 12 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F' &&
                d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
                return ImageFormat.WebP;
            if (d.Length >= 4 && d[0] == (byte)'8' && d[1] == (byte)'B' && d[2] == (byte)'P' && d[3] == (byte)'S')
                return ImageFormat.Psd;
            if (d.Length >= 4 && ((d[0] == (byte)'I' && d[1] == (byte)'I' && d[2] == 0x2A && d[3] == 0x00) ||
                                  (d[0] == (byte)'M' && d[1] == (byte)'M' && d[2] == 0x00 && d[3] == 0x2A)))
                return ImageFormat.Tiff;
            // BMP last: "BM" is a weak two-byte magic, so every stronger signature gets first refusal.
            if (d.Length >= 14 && d[0] == (byte)'B' && d[1] == (byte)'M') return ImageFormat.Bmp;
            return ImageFormat.Unknown;
        }

        internal static bool IsPng(ReadOnlySpan<byte> d) =>
            d.Length >= 8 &&
            d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 &&
            d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;

        // HEIC/HEIF files use the ISOBMFF container: a leading "ftyp" box whose
        // major_brand (offset 8..11) or compatible_brands (offsets 16, 20, 24, ...
        // up to the box size) carry the HEIF/HEIC marker. Detect the major brand
        // first and then scan the compatible_brands list so files authored with a
        // generic major_brand (e.g. "mif1") but a HEIC/HEIF compatible brand are
        // still recognised.
        internal static bool IsHeic(ReadOnlySpan<byte> d)
        {
            if (d.Length < 12)
                return false;

            if (d[4] != (byte)'f' || d[5] != (byte)'t' || d[6] != (byte)'y' || d[7] != (byte)'p')
                return false;

            int boxSize = (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3];
            if (boxSize <= 0 || boxSize > d.Length)
                boxSize = d.Length;

            if (IsHeifBrand(d, 8))
                return true;

            for (int offset = 16; offset + 4 <= boxSize; offset += 4)
            {
                if (IsHeifBrand(d, offset))
                    return true;
            }

            return false;
        }

        private static bool IsHeifBrand(ReadOnlySpan<byte> d, int offset)
        {
            if (offset < 0 || offset + 4 > d.Length)
                return false;

            string brand = System.Text.Encoding.ASCII.GetString(d.Slice(offset, 4));
            return brand switch
            {
                "heic" or "heix" or "heim" or "heis" or
                "hevc" or "hevx" or "hevm" or "hevs" or
                "mif1" or "msf1" or "heif" => true,
                _ => false,
            };
        }
    }
}
