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

#nullable enable

namespace TensorSharp.Models.Media;

/// <summary>
/// The alpha arithmetic an <see cref="IImageCodec"/> needs when its platform decoder can
/// only hand back premultiplied pixels.
///
/// <para>Core Graphics is the case this exists for: a <c>CGBitmapContext</c> accepts
/// <c>kCGImageAlphaPremultipliedLast</c> or <c>kCGImageAlphaNoneSkipLast</c> but has no
/// straight-alpha 8-bit RGBA layout at all, while <see cref="IImageCodec.DecodeRgba"/> is
/// contractually straight RGBA (ImageMagick's and stb_image's native output). So the Apple
/// provider draws premultiplied and undoes it here.</para>
///
/// <para>It lives outside <c>Media/Apple/</c> deliberately. The Apple sources compile only
/// for the iOS target, which no desktop test host can execute; this is the part of that
/// provider where a wrong constant silently darkens or lightens every transparent pixel a
/// model ever sees, so it is kept platform-neutral and covered by the parity tests that do
/// run on desktop CI.</para>
/// </summary>
internal static class StraightAlpha
{
    /// <summary>
    /// Convert premultiplied RGBA8 to straight RGBA8 in place.
    ///
    /// <para>Rounds to nearest (<c>(v * 255 + a/2) / a</c>) rather than truncating, which is
    /// what keeps a half-transparent mid-grey coming back as the byte it went in as instead of
    /// one level darker. Fully transparent pixels carry no recoverable colour — the premultiply
    /// destroyed it — and are left at zero, which is what ImageMagick reports for them too.</para>
    /// </summary>
    /// <param name="rgba">Row-major RGBA8, length a multiple of 4. Modified in place.</param>
    internal static void Unpremultiply(byte[] rgba)
    {
        if (rgba == null)
            throw new ArgumentNullException(nameof(rgba));
        if (rgba.Length % 4 != 0)
            throw new ArgumentException($"RGBA buffer length {rgba.Length} is not a multiple of 4", nameof(rgba));

        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte a = rgba[i + 3];
            if (a == 255)
                continue;
            if (a == 0)
            {
                rgba[i] = 0;
                rgba[i + 1] = 0;
                rgba[i + 2] = 0;
                continue;
            }

            rgba[i] = Divide(rgba[i], a);
            rgba[i + 1] = Divide(rgba[i + 1], a);
            rgba[i + 2] = Divide(rgba[i + 2], a);
        }
    }

    /// <summary>
    /// Set every alpha byte to 255.
    ///
    /// <para>What an opaque source needs after being drawn through a skip-alpha layout: the
    /// fourth byte of each pixel is padding the platform never wrote, so the buffer would
    /// otherwise carry whatever it was allocated with. This is the counterpart of ImageMagick's
    /// <c>Alpha(AlphaOption.Set)</c> on the desktop path, which is what makes the decoder's
    /// output uniformly RGBA whether or not the file had an alpha channel.</para>
    /// </summary>
    /// <param name="rgba">Row-major RGBA8, length a multiple of 4. Modified in place.</param>
    internal static void SetOpaque(byte[] rgba)
    {
        if (rgba == null)
            throw new ArgumentNullException(nameof(rgba));
        if (rgba.Length % 4 != 0)
            throw new ArgumentException($"RGBA buffer length {rgba.Length} is not a multiple of 4", nameof(rgba));

        for (int i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
    }

    private static byte Divide(byte value, byte alpha)
    {
        int straight = (value * 255 + alpha / 2) / alpha;
        return (byte)(straight > 255 ? 255 : straight);
    }
}
