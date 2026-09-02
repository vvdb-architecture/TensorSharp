// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.
#if IOS || MACCATALYST
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AVFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using Foundation;

#nullable enable

namespace TensorSharp.Models.Media.Apple;

/// <summary>Frame access through <c>AVAssetReader</c>, the counterpart of the desktop
/// provider's OpenCV <c>VideoCapture</c>.</summary>
public sealed partial class AppleMediaProvider
{
    /// <summary>Time scale for the seek path's <c>CMTime</c>s. 600 is the classic QuickTime
    /// scale — divisible by 24, 25, 30 and 60, so every common frame boundary lands on an
    /// exact integer instead of a rounded one.</summary>
    private const int SeekTimeScale = 600;

    /// <summary>Warn-once latch for a track whose display matrix is not one of the eight
    /// orientations. Static because the point is one line per process, not per file.</summary>
    private static int s_warnedUnknownTransform;

    public VideoInfo Probe(string path)
    {
        RequireFile(path);

        using AVUrlAsset asset = OpenAsset(path);
        AVAssetTrack track = FirstVideoTrack(asset, path);

        double fps = track.NominalFrameRate;
        CMTime duration = asset.Duration;
        double seconds = duration.IsNumeric ? duration.Seconds : 0;

        // Frame count is not carried by an ISO base-media file, so it is derived the way the
        // seam specifies: duration x nominal rate. An unusable rate leaves it at 0, which the
        // caller rejects — the same "report it as-is" contract OpenCV's frame count has.
        int frameCount = fps > 0 && seconds > 0 ? (int)Math.Round(seconds * fps) : 0;

        CGSize natural = track.NaturalSize;
        int width = (int)Math.Round((double)natural.Width);
        int height = (int)Math.Round((double)natural.Height);

        // The dimensions must describe the frames ReadFrames actually delivers, which are
        // upright; a portrait clip's natural size is its landscape pixel buffer.
        if (OrientationOf(track, path) >= 5)
            (width, height) = (height, width);

        return new VideoInfo(fps, frameCount, width, height);
    }

    /// <summary>
    /// Deliver the requested frames, upright, as BGRA.
    ///
    /// <para>Two access paths, chosen by <see cref="MediaHelper.PrefersSequentialStepping"/> so
    /// this provider follows the same measured policy as the desktop one: a dense request walks
    /// one reader forward and counts sample buffers (exact, and never trusts a container
    /// index), while a sparse or repeating request reopens the reader on a
    /// <c>timeRange</c> per frame. The two are never mixed inside one extraction, for the
    /// reason the desktop provider gives: a seek landing off by a frame would then skew every
    /// subsequent step.</para>
    ///
    /// <para>The buffer handed to the callback is reused between frames, as the contract
    /// requires — <see cref="MediaHelper"/> copies it into PNG scanlines before returning.</para>
    /// </summary>
    public void ReadFrames(string path, IReadOnlyList<int> frameIndices, FrameCallback onFrame)
    {
        if (frameIndices == null) throw new ArgumentNullException(nameof(frameIndices));
        if (onFrame == null) throw new ArgumentNullException(nameof(onFrame));
        RequireFile(path);
        if (frameIndices.Count == 0)
            return;

        using AVUrlAsset asset = OpenAsset(path);
        AVAssetTrack track = FirstVideoTrack(asset, path);
        int orientation = OrientationOf(track, path);
        var frame = new FrameBuffers();

        if (MediaHelper.PrefersSequentialStepping(frameIndices))
            ReadSequential(asset, track, path, frameIndices, orientation, frame, onFrame);
        else
            ReadBySeeking(asset, track, path, frameIndices, orientation, frame, onFrame);
    }

    // ------------------------------------------------------------------
    // Access paths
    // ------------------------------------------------------------------

    private static void ReadSequential(
        AVAsset asset, AVAssetTrack track, string path, IReadOnlyList<int> frameIndices,
        int orientation, FrameBuffers frame, FrameCallback onFrame)
    {
        using TrackReader reader = TrackReader.Open(asset, track, path, timeRange: null);

        // Index of the next frame CopyNextSampleBuffer will produce.
        int next = 0;
        foreach (int wanted in frameIndices)
        {
            if (wanted < 0)
                throw new ArgumentOutOfRangeException(nameof(frameIndices), "frame indices must be >= 0");

            while (next <= wanted)
            {
                using CMSampleBuffer? sample = reader.NextFrame();
                if (sample == null)
                    return;   // a short read is how a clip ends, not an error

                bool deliver = next == wanted;
                next++;
                if (deliver)
                    Deliver(wanted, sample, orientation, frame, onFrame);
            }
        }
    }

    private static void ReadBySeeking(
        AVAsset asset, AVAssetTrack track, string path, IReadOnlyList<int> frameIndices,
        int orientation, FrameBuffers frame, FrameCallback onFrame)
    {
        double fps = track.NominalFrameRate;
        if (fps <= 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(path)}' reports no nominal frame rate, so frame {frameIndices[0]} cannot be " +
                "located by time. Sample it densely (which steps through the frames instead) or re-encode the clip.");
        }

        int lastDelivered = -1;
        foreach (int wanted in frameIndices)
        {
            if (wanted < 0)
                throw new ArgumentOutOfRangeException(nameof(frameIndices), "frame indices must be >= 0");

            // A repeated index is how a clip is held onto a faster timeline. The previous
            // frame is still intact in the reusable buffer, so hand it over again rather than
            // decoding it twice.
            if (wanted == lastDelivered && frame.HasFrame)
            {
                frame.Redeliver(wanted, onFrame);
                continue;
            }

            // Start half a frame early so the sample whose display interval contains the
            // target is inside the range whichever way AVAssetReader rounds the boundary.
            double target = wanted / fps;
            CMTime start = CMTime.FromSeconds(Math.Max(0, target - 0.5 / fps), SeekTimeScale);
            using TrackReader reader = TrackReader.Open(
                asset, track, path,
                new CMTimeRange { Start = start, Duration = CMTime.PositiveInfinity });

            bool delivered = false;
            while (!delivered)
            {
                using CMSampleBuffer? sample = reader.NextFrame();
                if (sample == null)
                    return;   // ran out before the wanted frame: a short read

                // Presentation time back to a frame index: a keyframe-anchored range can open
                // earlier than asked for, and this is what tells those frames apart.
                CMTime pts = sample.PresentationTimeStamp;
                if (pts.IsNumeric && (int)Math.Round(pts.Seconds * fps) < wanted)
                    continue;

                Deliver(wanted, sample, orientation, frame, onFrame);
                delivered = true;
            }

            lastDelivered = wanted;
        }
    }

    // ------------------------------------------------------------------
    // One frame
    // ------------------------------------------------------------------

    /// <summary>
    /// Copy one decoded sample out of the CoreVideo buffer and hand it to the callback.
    ///
    /// <para>Upright clips take the cheap path the seam describes: the locked base address's
    /// bytes with the buffer's own <c>bytesPerRow</c> as the stride, no repacking. A rotated
    /// clip is repacked tight and put through <see cref="ExifOrientation.Apply"/>, the same
    /// permutation the still-image decoders use for a rotated photo — four bytes per pixel
    /// either way, so BGRA rotates exactly as RGBA does.</para>
    /// </summary>
    private static void Deliver(int index, CMSampleBuffer sample, int orientation, FrameBuffers frame, FrameCallback onFrame)
    {
        using CVImageBuffer? imageBuffer = sample.GetImageBuffer();
        if (imageBuffer is not CVPixelBuffer pixelBuffer)
        {
            throw new NotSupportedException(
                "AVAssetReader produced a sample with no CVPixelBuffer; the track output was configured for " +
                "kCVPixelFormatType_32BGRA and is expected to deliver one.");
        }

        CVReturn locked = pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
        if (locked != CVReturn.Success)
            throw new InvalidOperationException($"CVPixelBufferLockBaseAddress failed ({locked}) for frame {index}.");

        try
        {
            int width = (int)pixelBuffer.Width;
            int height = (int)pixelBuffer.Height;
            int stride = (int)pixelBuffer.BytesPerRow;
            IntPtr baseAddress = pixelBuffer.BaseAddress;
            if (width <= 0 || height <= 0 || stride < width * 4 || baseAddress == IntPtr.Zero)
                throw new InvalidOperationException($"AVAssetReader produced an unusable {width}x{height} frame (stride {stride}).");

            if (orientation <= 1)
            {
                long bytes = (long)stride * height;
                if (bytes > int.MaxValue)
                    throw new NotSupportedException($"frame of {width}x{height} exceeds the 2 GB buffer limit");

                byte[] buffer = frame.Rent((int)bytes);
                Marshal.Copy(baseAddress, buffer, 0, (int)bytes);
                frame.Record(buffer, width, height, stride);
            }
            else
            {
                long bytes = (long)width * height * 4;
                if (bytes > int.MaxValue)
                    throw new NotSupportedException($"frame of {width}x{height} exceeds the 2 GB buffer limit");

                byte[] tight = frame.Rent((int)bytes);
                for (int y = 0; y < height; y++)
                    Marshal.Copy(baseAddress + y * stride, tight, y * width * 4, width * 4);

                int uprightWidth = width, uprightHeight = height;
                byte[] rotated = ExifOrientation.Apply(tight, ref uprightWidth, ref uprightHeight, orientation);
                frame.Record(rotated, uprightWidth, uprightHeight, uprightWidth * 4);
            }
        }
        finally
        {
            pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly);
        }

        frame.Redeliver(index, onFrame);
    }

    // ------------------------------------------------------------------
    // AVFoundation plumbing
    // ------------------------------------------------------------------

    private static void RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"video not found: {path}", path);
    }

    /// <summary><c>PreferPreciseDurationAndTiming</c> is on because <see cref="Probe"/>'s frame
    /// count is derived from the duration; the fast path reports an approximate one for
    /// anything but a fragmented MP4.</summary>
    private static AVUrlAsset OpenAsset(string path)
    {
        using NSUrl url = NSUrl.FromFilename(path);
        var asset = new AVUrlAsset(url, new AVUrlAssetOptions { PreferPreciseDurationAndTiming = true });
        if (asset.Handle == IntPtr.Zero)
        {
            asset.Dispose();
            throw new InvalidOperationException($"Failed to open video file: {path}");
        }
        return asset;
    }

    private static AVAssetTrack FirstVideoTrack(AVAsset asset, string path)
    {
        AVAssetTrack[]? tracks = asset.GetTracks(AVMediaTypes.Video);
        if (tracks == null || tracks.Length == 0)
        {
            throw new InvalidOperationException(
                $"Failed to open video file: '{Path.GetFileName(path)}' has no video track that AVFoundation can read " +
                "(it may be audio-only, DRM-protected, or in a codec this device has no decoder for).");
        }
        return tracks[0];
    }

    /// <summary>The track's display rotation as an EXIF orientation. A matrix that is not one
    /// of the eight is reported once and then ignored, rather than silently pretending the clip
    /// was upright or refusing a file that is otherwise perfectly decodable.</summary>
    private static int OrientationOf(AVAssetTrack track, string path)
    {
        CGAffineTransform t = track.PreferredTransform;
        int orientation = VideoTransform.ToExifOrientation(
            (double)t.A, (double)t.B, (double)t.C, (double)t.D, out bool recognised);

        if (!recognised && System.Threading.Interlocked.Exchange(ref s_warnedUnknownTransform, 1) == 0)
        {
            Console.WriteLine(
                $"  [video] '{Path.GetFileName(path)}' carries a display matrix [{t.A}, {t.B}, {t.C}, {t.D}] that is " +
                "not one of the eight right-angle orientations; frames are delivered unrotated.");
        }

        return orientation;
    }

    /// <summary>
    /// The reusable pixel buffer one extraction hands to the callback, plus the geometry of
    /// whatever is currently in it so a repeated frame index can be served without decoding
    /// twice.
    /// </summary>
    private sealed class FrameBuffers
    {
        private byte[]? _buffer;
        private byte[]? _current;
        private int _width, _height, _stride;

        internal bool HasFrame => _current != null;

        /// <summary>The shared buffer, grown as needed. The contract says the pixels are the
        /// decoder's and valid only until the callback returns, so one buffer per extraction
        /// is exactly right.</summary>
        internal byte[] Rent(int bytes)
        {
            if (_buffer == null || _buffer.Length < bytes)
                _buffer = new byte[bytes];
            return _buffer;
        }

        internal void Record(byte[] pixels, int width, int height, int stride)
        {
            _current = pixels;
            _width = width;
            _height = height;
            _stride = stride;
        }

        internal void Redeliver(int index, FrameCallback onFrame)
        {
            if (_current == null)
                throw new InvalidOperationException("no frame has been decoded yet");
            onFrame(index, _current, _width, _height, _stride, PixelLayout.Bgra);
        }
    }

    /// <summary>An <c>AVAssetReader</c> and its track output as one disposable unit; the reader
    /// owns the output once added, but both handles have to be released together and the
    /// reader has to be cancelled when a caller stops early.</summary>
    private sealed class TrackReader : IDisposable
    {
        private readonly AVAssetReader _reader;
        private readonly AVAssetReaderTrackOutput _output;

        private TrackReader(AVAssetReader reader, AVAssetReaderTrackOutput output)
        {
            _reader = reader;
            _output = output;
        }

        internal static TrackReader Open(AVAsset asset, AVAssetTrack track, string path, CMTimeRange? timeRange)
        {
            AVAssetReader? reader = AVAssetReader.FromAsset(asset, out NSError error);
            if (reader == null || error != null)
            {
                reader?.Dispose();
                throw new InvalidOperationException(
                    $"AVAssetReader could not open '{Path.GetFileName(path)}': {error?.LocalizedDescription ?? "unknown error"}");
            }

            AVAssetReaderTrackOutput? output = null;
            try
            {
                if (timeRange.HasValue)
                    reader.TimeRange = timeRange.Value;

                // 32BGRA is the format the seam specifies and the cheapest thing VideoToolbox
                // will convert YUV to; AlwaysCopiesSampleData stays off because every frame is
                // copied out while the buffer is locked and never held past the callback.
                output = new AVAssetReaderTrackOutput(
                    track, new AVVideoSettingsUncompressed { PixelFormatType = CVPixelFormatType.CV32BGRA })
                {
                    AlwaysCopiesSampleData = false,
                };

                if (!reader.CanAddOutput(output))
                    throw new InvalidOperationException($"AVAssetReader refused a 32BGRA output for '{Path.GetFileName(path)}'.");
                reader.AddOutput(output);

                if (!reader.StartReading())
                {
                    throw new InvalidOperationException(
                        $"AVAssetReader could not start reading '{Path.GetFileName(path)}': " +
                        $"{reader.Error?.LocalizedDescription ?? reader.Status.ToString()}");
                }

                return new TrackReader(reader, output);
            }
            catch
            {
                output?.Dispose();
                reader.Dispose();
                throw;
            }
        }

        /// <summary>The next decoded frame, or <c>null</c> at the end of the range. A failed
        /// read is not an end: it throws, so a truncated file is not mistaken for a short clip.</summary>
        internal CMSampleBuffer? NextFrame()
        {
            CMSampleBuffer? sample = _output.CopyNextSampleBuffer();
            if (sample != null)
                return sample;

            if (_reader.Status == AVAssetReaderStatus.Failed)
            {
                throw new InvalidDataException(
                    $"AVAssetReader failed part-way through the clip: {_reader.Error?.LocalizedDescription ?? "unknown error"}");
            }
            return null;
        }

        public void Dispose()
        {
            // Cancelling first tears down the decode pipeline even when the caller stopped
            // early; disposing a still-reading AVAssetReader leaves it running until finalized.
            if (_reader.Status == AVAssetReaderStatus.Reading)
                _reader.CancelReading();
            _output.Dispose();
            _reader.Dispose();
        }
    }
}
#endif
