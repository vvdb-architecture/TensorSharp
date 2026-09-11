// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.
using System.Collections.Generic;

namespace TensorSharp.Models.Media
{
    /// <summary>What a container reports about a clip before any frame is decoded.</summary>
    /// <param name="Fps">Nominal frame rate; <c>&lt;= 0</c> when the container does not say.</param>
    /// <param name="FrameCount">Total frames; <c>&lt;= 0</c> when the container does not say.</param>
    /// <param name="Width">Frame width in pixels (0 when unknown).</param>
    /// <param name="Height">Frame height in pixels (0 when unknown).</param>
    public sealed record VideoInfo(double Fps, int FrameCount, int Width, int Height);

    /// <summary>Channel order of the pixel buffer a decoder hands to <see cref="FrameCallback"/>.
    /// OpenCV decodes BGR(A); AVFoundation's cheapest output is BGRA
    /// (<c>kCVPixelFormatType_32BGRA</c>); the PNG writer takes any of the four.</summary>
    public enum PixelLayout
    {
        Bgr,
        Bgra,
        Rgb,
        Rgba,
    }

    /// <summary>
    /// Receives one decoded frame. <paramref name="pixels"/> is <b>owned by the decoder and
    /// valid only until the callback returns</b> — it is typically the same buffer the next
    /// frame will be decoded into, so anything kept must be copied out. Rows start every
    /// <paramref name="stride"/> bytes (which may exceed <c>width * bytesPerPixel</c> for
    /// padded rows). The callback may block: <see cref="MediaHelper"/> uses that as back-pressure
    /// so a long clip cannot balloon the heap with frames awaiting PNG encoding.
    /// </summary>
    /// <param name="index">The source frame index this frame is, echoing the requested list.</param>
    public delegate void FrameCallback(int index, byte[] pixels, int width, int height, int stride, PixelLayout layout);

    /// <summary>
    /// Sparse frame access to a video file. The sampling POLICY (which frames, how many, the
    /// PNG encoding and its memory budget) stays in <see cref="MediaHelper"/>; a provider only
    /// reaches the frames it is asked for.
    /// </summary>
    public interface IVideoDecoder
    {
        /// <summary>Open the file and report its geometry. Throws
        /// <see cref="System.IO.FileNotFoundException"/> when the file is missing and
        /// <see cref="System.InvalidOperationException"/> when the container cannot be opened;
        /// an unusable fps / frame count is reported as-is (<c>&lt;= 0</c>) and rejected by the caller.</summary>
        VideoInfo Probe(string path);

        /// <summary>
        /// Deliver the frames at <paramref name="frameIndices"/> (non-decreasing; a repeated
        /// index means the same frame twice, which is how a clip is held onto a faster timeline)
        /// to <paramref name="onFrame"/>, in order, on the calling thread.
        ///
        /// <para>How to reach them is the provider's choice. <see cref="MediaHelper.PrefersSequentialStepping"/>
        /// is the measured policy the desktop provider follows: below a gap of
        /// <see cref="MediaHelper.SequentialStepMaxGap"/> stepping forward beats seeking and is
        /// exact regardless of the container's index; above it, seek. A provider that runs out
        /// of frames before the list ends simply returns — a short read is how a clip ends, not
        /// an error — so callers must count what they received.</para>
        /// </summary>
        void ReadFrames(string path, IReadOnlyList<int> frameIndices, FrameCallback onFrame);
    }
}
