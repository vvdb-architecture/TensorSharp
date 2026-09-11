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

namespace InferenceWeb.Tests;

/// <summary>
/// The arithmetic the iOS media provider depends on, run on a desktop.
///
/// <para><c>TensorSharp.Models/Media/Apple/</c> compiles only for the <c>net10.0-ios</c>
/// target, so nothing in it can be executed by this test host. Two pieces of it were
/// deliberately kept out of that folder and platform-neutral — the premultiplied-alpha
/// recovery (<see cref="StraightAlpha"/>) and the display-matrix reading
/// (<see cref="VideoTransform"/>) — because they are exactly the parts where a wrong constant
/// produces plausible-looking output that no crash and no log line would ever reveal: every
/// semi-transparent pixel slightly wrong, or every portrait video fed to the model on its
/// side. This is what covers them.</para>
/// </summary>
public class AppleProviderMathTests
{
    // ---- premultiplied alpha ---------------------------------------------------------

    /// <summary>
    /// Un-premultiplying recovers the original colour, and the error stays inside the bound
    /// the parity harness allows the Apple provider.
    ///
    /// <para>The loop is the whole domain, not a sample: every (value, alpha) pair a
    /// <c>kCGImageAlphaPremultipliedLast</c> context could produce, checked against the bound
    /// half a quantisation step implies. Nothing here is expensive and the failure mode is a
    /// systematic bias, which a spot check is the worst possible way to find.</para>
    /// </summary>
    [Fact]
    public void Unpremultiply_RecoversTheColourWithinTheQuantisationBound()
    {
        for (int alpha = 1; alpha <= 255; alpha++)
        {
            int bound = (int)Math.Ceiling(127.5 / alpha);
            for (int value = 0; value <= 255; value++)
            {
                // What Core Graphics stores for a straight `value` at this alpha.
                byte premultiplied = (byte)((value * alpha + 127) / 255);
                byte[] rgba = { premultiplied, premultiplied, premultiplied, (byte)alpha };

                StraightAlpha.Unpremultiply(rgba);

                Assert.Equal(alpha, rgba[3]);
                Assert.Equal(rgba[0], rgba[1]);
                Assert.Equal(rgba[1], rgba[2]);
                Assert.True(
                    Math.Abs(rgba[0] - value) <= bound,
                    $"alpha {alpha}, straight {value}: premultiplied to {premultiplied}, recovered {rgba[0]} (bound {bound})");
            }
        }
    }

    /// <summary>At the alphas the parity harness uses the recovery is exact to within the
    /// tolerance that harness declares, so the two numbers cannot drift apart unnoticed.</summary>
    [Theory]
    [InlineData(255)]
    [InlineData(192)]
    [InlineData(128)]
    [InlineData(64)]
    public void Unpremultiply_IsWithinThreeLevelsAtTheAlphasTheParityHarnessUses(int alpha)
    {
        const int parityHarnessTolerance = 3;
        for (int value = 0; value <= 255; value++)
        {
            byte premultiplied = (byte)((value * alpha + 127) / 255);
            byte[] rgba = { premultiplied, 0, 0, (byte)alpha };
            StraightAlpha.Unpremultiply(rgba);
            Assert.True(Math.Abs(rgba[0] - value) <= parityHarnessTolerance,
                $"alpha {alpha}, straight {value}: recovered {rgba[0]}");
        }
    }

    [Fact]
    public void Unpremultiply_LeavesOpaquePixelsAloneAndZeroesFullyTransparentOnes()
    {
        byte[] rgba =
        {
            10, 20, 30, 255,      // opaque: untouched
            7, 9, 11, 0,          // fully transparent: the premultiply destroyed the colour
        };
        StraightAlpha.Unpremultiply(rgba);

        Assert.Equal(new byte[] { 10, 20, 30, 255, 0, 0, 0, 0 }, rgba);
    }

    [Fact]
    public void SetOpaque_FillsOnlyTheAlphaPlane()
    {
        byte[] rgba = { 1, 2, 3, 0, 4, 5, 6, 17 };
        StraightAlpha.SetOpaque(rgba);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, rgba);
    }

    [Fact]
    public void AlphaHelpers_RejectMalformedBuffers()
    {
        Assert.Throws<ArgumentNullException>(() => StraightAlpha.Unpremultiply(null!));
        Assert.Throws<ArgumentNullException>(() => StraightAlpha.SetOpaque(null!));
        Assert.Throws<ArgumentException>(() => StraightAlpha.Unpremultiply(new byte[7]));
        Assert.Throws<ArgumentException>(() => StraightAlpha.SetOpaque(new byte[5]));
    }

    // ---- the video display matrix ----------------------------------------------------

    /// <summary>
    /// The four matrices a phone actually writes, mapped to the EXIF orientation that turns
    /// the frames upright.
    ///
    /// <para>These are AVFoundation's <c>preferredTransform</c> values for the four recording
    /// orientations, and the mapping is the one <c>UIImageOrientation</c> uses: landscape-right
    /// is the identity, portrait is a 90° clockwise rotation (EXIF 6), portrait-upside-down is
    /// 90° counter-clockwise (EXIF 8), landscape-left is 180° (EXIF 3). Get 6 and 8 the wrong
    /// way round and every portrait clip arrives upside down — which still looks like video, so
    /// only this test would catch it.</para>
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, 1, 1)]      // landscape right: already upright
    [InlineData(0, 1, -1, 0, 6)]     // portrait: rotate 90 clockwise
    [InlineData(0, -1, 1, 0, 8)]     // portrait upside down: rotate 90 counter-clockwise
    [InlineData(-1, 0, 0, -1, 3)]    // landscape left: rotate 180
    public void PreferredTransform_MapsTheFourRecordingOrientations(double a, double b, double c, double d, int expected)
    {
        Assert.Equal(expected, VideoTransform.ToExifOrientation(a, b, c, d, out bool recognised));
        Assert.True(recognised);
    }

    /// <summary>The mirrored and transposed matrices as well, so the mapping is the whole EXIF
    /// table rather than the four common cases plus guesses.</summary>
    [Theory]
    [InlineData(-1, 0, 0, 1, 2)]     // mirrored horizontally
    [InlineData(1, 0, 0, -1, 4)]     // mirrored vertically
    [InlineData(0, 1, 1, 0, 5)]      // transpose
    [InlineData(0, -1, -1, 0, 7)]    // transverse
    public void PreferredTransform_CoversTheMirroredOrientationsToo(double a, double b, double c, double d, int expected)
    {
        Assert.Equal(expected, VideoTransform.ToExifOrientation(a, b, c, d, out bool recognised));
        Assert.True(recognised);
    }

    /// <summary>A matrix that also scales still reports its rotation: the scale is normalised
    /// away before the comparison, so an anamorphic or resized track is not mistaken for a
    /// shear and left unrotated.</summary>
    [Theory]
    [InlineData(0, 2, -2, 0, 6)]
    [InlineData(0, 1.5, -0.5, 0, 6)]
    [InlineData(3, 0, 0, 3, 1)]
    public void PreferredTransform_IgnoresScale(double a, double b, double c, double d, int expected)
    {
        Assert.Equal(expected, VideoTransform.ToExifOrientation(a, b, c, d, out bool recognised));
        Assert.True(recognised);
    }

    /// <summary>The float64 residue of <c>cos/sin(pi/2)</c> — how a rotation matrix is written
    /// when it is computed rather than typed — is inside the tolerance.</summary>
    [Fact]
    public void PreferredTransform_AcceptsAComputedRightAngle()
    {
        double angle = Math.PI / 2;
        double cos = Math.Cos(angle);   // 6.12e-17, not 0
        double sin = Math.Sin(angle);
        Assert.Equal(6, VideoTransform.ToExifOrientation(cos, sin, -sin, cos, out bool recognised));
        Assert.True(recognised);
    }

    /// <summary>Anything that is not a right-angle orientation says so, so the decoder can warn
    /// instead of silently claiming the clip was upright.</summary>
    [Theory]
    [InlineData(1, 0.5, 0, 1)]        // shear
    [InlineData(0.7071, 0.7071, -0.7071, 0.7071)]   // 45 degrees
    [InlineData(0, 0, 0, 0)]          // degenerate
    [InlineData(1, 0, 0, 0)]          // collapsed y axis
    public void PreferredTransform_ReportsWhatItCannotExpress(double a, double b, double c, double d)
    {
        Assert.Equal(1, VideoTransform.ToExifOrientation(a, b, c, d, out bool recognised));
        Assert.False(recognised);
    }

    /// <summary>
    /// The orientations the matrix produces are the ones
    /// <see cref="TensorSharp.Models.Media.ExifOrientation"/> already implements, and they mean
    /// the same thing for a four-byte BGRA video frame as for RGBA stills — which is the whole
    /// reason the video decoder expresses rotation this way instead of writing its own.
    /// </summary>
    [Fact]
    public void PortraitRotation_TurnsAFrameThroughTheSharedTransform()
    {
        // A 4x2 BGRA "frame" whose pixels carry their own coordinates.
        const int w = 4, h = 2;
        byte[] frame = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                frame[p] = (byte)x; frame[p + 1] = (byte)y; frame[p + 2] = 200; frame[p + 3] = 255;
            }

        int orientation = VideoTransform.ToExifOrientation(0, 1, -1, 0, out bool recognised);
        Assert.True(recognised);

        int width = w, height = h;
        byte[] upright = ExifOrientation.Apply(frame, ref width, ref height, orientation);

        // 90 degrees clockwise: the landscape buffer becomes a portrait one, and the source's
        // top-left pixel ends up at the top right.
        Assert.Equal((h, w), (width, height));
        Assert.Equal(0, upright[(0 * width + (width - 1)) * 4]);       // source x = 0
        Assert.Equal(0, upright[(0 * width + (width - 1)) * 4 + 1]);   // source y = 0
        Assert.Equal(200, upright[2]);                                 // the third channel rides along untouched
        Assert.Equal(255, upright[3]);
    }
}
