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
/// Reads a video track's display transform as one of the eight EXIF orientations, so a
/// decoder can hand upright frames to a model.
///
/// <para>A phone records portrait video as landscape pixels plus a rotation in the track
/// header (AVFoundation's <c>preferredTransform</c>; the same 3x2 matrix ffmpeg exposes as
/// the <c>displaymatrix</c> side data and OpenCV applies for you when
/// <c>CAP_PROP_ORIENTATION_AUTO</c> is on, which is its default). Nothing rotates the pixels
/// themselves, so a decoder that ignores the matrix feeds every portrait clip to the model
/// on its side — a failure that looks like the model being bad at video rather than like a
/// bug.</para>
///
/// <para>Expressed as an EXIF orientation because that is the vocabulary
/// <see cref="ExifOrientation.Apply"/> already speaks, which keeps rotated video frames and
/// rotated phone photos going through one tested transform instead of two.</para>
///
/// <para>Kept platform-neutral (six doubles rather than a <c>CGAffineTransform</c>) for the
/// same reason as <see cref="StraightAlpha"/>: the Apple decoder that calls it compiles only
/// for the iOS target, and this mapping is exactly the part that must be right.</para>
/// </summary>
internal static class VideoTransform
{
    /// <summary>Largest departure from a clean 0/±1 matrix still treated as axis-aligned.
    /// The matrices in real files are written as exact integers or as
    /// <c>cos/sin(pi/2)</c>, whose float64 sine leaves ~6e-17 residue, so this is loose
    /// enough for the trigonometry and far tighter than any real shear.</summary>
    private const double Tolerance = 1e-6;

    /// <summary>
    /// Map the linear part of a track's preferred transform to an EXIF orientation
    /// (1 = already upright, 6 = rotate 90° clockwise, and so on).
    ///
    /// <para>Only the 2x2 linear part matters; the translation merely re-origins the rotated
    /// frame and carries no orientation. Scale is divided out first, so a matrix that also
    /// resizes still reports its rotation rather than falling through as unrecognised.</para>
    /// </summary>
    /// <param name="a">Matrix <c>a</c> (x contribution to x).</param>
    /// <param name="b">Matrix <c>b</c> (x contribution to y).</param>
    /// <param name="c">Matrix <c>c</c> (y contribution to x).</param>
    /// <param name="d">Matrix <c>d</c> (y contribution to y).</param>
    /// <param name="recognised"><c>false</c> when the matrix is a shear, an arbitrary angle or
    /// degenerate — i.e. nothing the eight orientations can express. The orientation returned
    /// is then 1 (leave the pixels alone), and the caller is expected to say so out loud rather
    /// than silently pretend the clip was upright.</param>
    /// <returns>An EXIF orientation in 1..8.</returns>
    internal static int ToExifOrientation(double a, double b, double c, double d, out bool recognised)
    {
        recognised = true;

        // Normalise away scale: |(a,b)| is the length the x axis is mapped to and |(c,d)| the
        // y axis. A zero-length axis is degenerate and cannot say anything about rotation.
        double xLength = Math.Sqrt(a * a + b * b);
        double yLength = Math.Sqrt(c * c + d * d);
        if (xLength <= Tolerance || yLength <= Tolerance || double.IsNaN(xLength) || double.IsNaN(yLength))
        {
            recognised = false;
            return 1;
        }

        a /= xLength;
        b /= xLength;
        c /= yLength;
        d /= yLength;

        if (Is(a, 1) && Is(b, 0) && Is(c, 0) && Is(d, 1)) return 1;    // identity
        if (Is(a, -1) && Is(b, 0) && Is(c, 0) && Is(d, 1)) return 2;   // mirrored horizontally
        if (Is(a, -1) && Is(b, 0) && Is(c, 0) && Is(d, -1)) return 3;  // rotate 180
        if (Is(a, 1) && Is(b, 0) && Is(c, 0) && Is(d, -1)) return 4;   // mirrored vertically
        if (Is(a, 0) && Is(b, 1) && Is(c, 1) && Is(d, 0)) return 5;    // transpose
        if (Is(a, 0) && Is(b, 1) && Is(c, -1) && Is(d, 0)) return 6;   // rotate 90 clockwise
        if (Is(a, 0) && Is(b, -1) && Is(c, -1) && Is(d, 0)) return 7;  // transverse
        if (Is(a, 0) && Is(b, -1) && Is(c, 1) && Is(d, 0)) return 8;   // rotate 90 counter-clockwise

        recognised = false;
        return 1;
    }

    private static bool Is(double value, double expected) => Math.Abs(value - expected) <= Tolerance;
}
