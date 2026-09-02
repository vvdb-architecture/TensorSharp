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
    /// <summary>Resampling filter for <see cref="IImageCodec.ResizeRgb8"/>.</summary>
    public enum ResizeFilter
    {
        /// <summary>2x2 tent filter. Cheap, and what the chat vision processors' own
        /// references use; identical on every platform because it never leaves managed code.</summary>
        Bilinear,

        /// <summary>Lanczos-3 windowed sinc. The generative pipelines (Qwen-Image edit, Wan,
        /// MiniMax-H3) were validated against ImageMagick's Lanczos, which the desktop provider
        /// keeps; the managed default is the Pillow fixed-point port shared with Muse-Glimmer.</summary>
        Lanczos,
    }

    /// <summary>
    /// Still-image decode / encode / resize behind which the platform image library sits.
    ///
    /// <para>Every contract downstream of this interface is fixed by the engines: decoders
    /// hand out <b>RGBA8, row-major, no row padding</b> (<c>width * height * 4</c> bytes) and
    /// the vision processors composite, resize and normalise that themselves; the generative
    /// pipelines convert it to <see cref="TensorSharp.Models.QwenImage.RgbImage"/> (HWC float
    /// [0,1]). A provider therefore never resizes on decode and never premultiplies alpha.</para>
    /// </summary>
    public interface IImageCodec
    {
        /// <summary>Whether <paramref name="header"/> (the first bytes of a file — 32 are
        /// enough for every format sniffed today) is a format this codec decodes. A
        /// <c>false</c> is a promise that <see cref="DecodeRgba"/> would throw
        /// <see cref="NotSupportedException"/> for the same bytes.</summary>
        bool CanDecode(ReadOnlySpan<byte> header);

        /// <summary>Decode a whole file to RGBA8 with the EXIF orientation applied, so a phone
        /// photo stored rotated comes out upright (what ImageMagick's <c>AutoOrient</c> did for
        /// the generative pipelines). <paramref name="width"/>/<paramref name="height"/> are the
        /// UPRIGHT dimensions. Unsupported formats throw <see cref="NotSupportedException"/>
        /// naming the provider that would handle them; corrupt files throw
        /// <see cref="System.IO.InvalidDataException"/>.</summary>
        byte[] DecodeRgba(byte[] file, out int width, out int height);

        /// <summary>The dimensions as STORED (no orientation applied), read from the header where
        /// the format allows it. Used for token-count estimates before a full decode.</summary>
        (int width, int height) ReadDimensions(byte[] file);

        /// <summary>Encode packed 8-bit pixels (<paramref name="channels"/> = 3 for RGB, 4 for
        /// RGBA; row-major, no padding) as a PNG file.</summary>
        byte[] EncodePng(byte[] rgb8, int width, int height, int channels);

        /// <summary>Resize packed RGB8 (no alpha, no padding) to exactly
        /// <paramref name="dstW"/> x <paramref name="dstH"/>, ignoring aspect ratio — the caller
        /// has already decided the geometry. Returns a new buffer (or the input when the size
        /// is unchanged).</summary>
        byte[] ResizeRgb8(byte[] rgb8, int srcW, int srcH, int dstW, int dstH, ResizeFilter filter);
    }
}
