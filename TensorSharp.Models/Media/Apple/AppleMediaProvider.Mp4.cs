// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// MP4 writing for generated video, through AVAssetWriter + VideoToolbox.
//
// There is no preference ladder here, unlike the desktop provider's ffmpeg -> OpenCV avc1 ->
// OpenCV mp4v: two of those rungs cannot exist on a phone (no spawned processes, no
// ios-arm64 OpenCV) and the third is the same hardware encoder this goes to. So it is one
// path that either produces browser-playable H.264 or throws.
#if IOS || MACCATALYST
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using AVFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using TensorSharp.Models.QwenImage;

#nullable enable

namespace TensorSharp.Models.Media.Apple;

public sealed partial class AppleMediaProvider
{
    /// <summary>Bits per pixel per second asked of the encoder. VideoToolbox has no CRF mode,
    /// so quality has to be requested as a rate; 0.6 bpp is generous for the smooth, synthetic
    /// frames a diffusion model produces (a phone camera records 1080p30 at about 0.28 bpp) and
    /// is the closest this can come to the desktop path's <c>-crf 17</c>.</summary>
    private const double BitsPerPixel = 0.6;

    /// <summary>Floor and ceiling for the derived bit rate: enough that a postage-stamp clip is
    /// not wrecked by a tiny budget, capped so a large one does not ask for a rate the encoder
    /// will refuse.</summary>
    private const int MinBitRate = 1_000_000;
    private const int MaxBitRate = 60_000_000;

    /// <summary>How long to wait for the encoder to drain before giving up on a frame. The
    /// input goes not-ready when VideoToolbox's queue is full; on a phone that clears in
    /// milliseconds, so a minute means something is genuinely wrong.</summary>
    private static readonly TimeSpan AppendTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Write frames as an H.264 MP4 and return <c>"h264"</c>. <paramref name="path"/>
    /// is a full path whose directory exists (<see cref="TensorSharp.Models.WanVideo.VideoIO.SaveMp4"/>
    /// sees to both).</summary>
    public string SaveMp4(string path, RgbImage[] frames, int fps)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
        if (frames == null || frames.Length == 0)
            throw new ArgumentException("no frames to save", nameof(frames));
        if (fps <= 0) fps = 16;

        int width = frames[0].Width;
        int height = frames[0].Height;
        for (int i = 1; i < frames.Length; i++)
        {
            if (frames[i] == null || frames[i].Width != width || frames[i].Height != height)
            {
                throw new ArgumentException(
                    $"frame {i} is {frames[i]?.Width ?? 0}x{frames[i]?.Height ?? 0}, not {width}x{height}; " +
                    "an MP4 track has one frame size", nameof(frames));
            }
        }

        // H.264 in yuv420p subsamples chroma 2x2, so an odd dimension has no valid encoding.
        // Saying so beats letting VideoToolbox fail with a numeric OSStatus.
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot encode a {width}x{height} MP4: H.264 4:2:0 needs even dimensions. Pad or crop the frames " +
                "to an even size before saving.");
        }

        // AVAssetWriter refuses to start when the output URL already exists.
        if (File.Exists(path))
            File.Delete(path);

        // The binding hands the UTI back as an NSString that is nullable in theory only; the
        // literal is the documented value of AVFileTypeMPEG4 and keeps the call non-nullable.
        string fileType = (string?)AVFileTypes.Mpeg4.GetConstant() ?? "public.mpeg-4";
        string mediaType = (string?)AVMediaTypes.Video.GetConstant() ?? "vide";

        using NSUrl url = NSUrl.FromFilename(path);
        AVAssetWriter? writer = AVAssetWriter.FromUrl(url, fileType, out NSError error);
        if (writer == null || error != null)
        {
            throw new InvalidOperationException(
                $"Could not create an MP4 writer at '{path}': {error?.LocalizedDescription ?? "unknown error"}");
        }

        try
        {
            int bitRate = (int)Math.Clamp((long)(width * (double)height * fps * BitsPerPixel), MinBitRate, MaxBitRate);
            var settings = new AVVideoSettingsCompressed
            {
                CodecType = AVVideoCodecType.H264,
                Width = width,
                Height = height,
                CodecSettings = new AVVideoCodecSettings
                {
                    AverageBitRate = bitRate,
                    // A keyframe a second keeps seeking usable in a browser without spending
                    // much of the budget on intra frames.
                    MaxKeyFrameInterval = fps,
                    ProfileLevelH264 = AVVideoProfileLevelH264.HighAutoLevel,
                },
            };

            using var input = new AVAssetWriterInput(mediaType, settings)
            {
                // Offline encode: let the writer apply back-pressure instead of dropping.
                ExpectsMediaDataInRealTime = false,
            };
            using var adaptor = new AVAssetWriterInputPixelBufferAdaptor(
                input,
                new CVPixelBufferAttributes
                {
                    PixelFormatType = CVPixelFormatType.CV32BGRA,
                    Width = (nint)width,
                    Height = (nint)height,
                });

            if (!writer.CanAddInput(input))
                throw new InvalidOperationException($"AVAssetWriter refused an H.264 {width}x{height} input.");
            writer.AddInput(input);

            if (!writer.StartWriting())
            {
                throw new InvalidOperationException(
                    $"AVAssetWriter could not start writing '{path}': {writer.Error?.LocalizedDescription ?? writer.Status.ToString()}");
            }
            writer.StartSessionAtSourceTime(CMTime.Zero);

            for (int i = 0; i < frames.Length; i++)
            {
                WaitForInput(input, writer, i);

                // A fresh buffer per frame: the adaptor hands it to VideoToolbox and may still
                // be holding it when the next append is made, so reuse would be a data race.
                using CVPixelBuffer pixelBuffer = CreateBgraBuffer(frames[i], width, height);
                if (!adaptor.AppendPixelBufferWithPresentationTime(pixelBuffer, new CMTime(i, fps)))
                {
                    throw new InvalidOperationException(
                        $"AVAssetWriter rejected frame {i} of '{path}': {writer.Error?.LocalizedDescription ?? writer.Status.ToString()}");
                }
            }

            input.MarkAsFinished();
            writer.EndSessionAtSourceTime(new CMTime(frames.Length, fps));
            FinishWriting(writer, path);
            return "h264";
        }
        catch
        {
            if (writer.Status == AVAssetWriterStatus.Writing)
                writer.CancelWriting();
            // A half-written file plays as nothing; do not leave one behind pretending to be output.
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            throw;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Flush the encoder and close the container, then block until it is done.
    ///
    /// <para>The completion-handler form, not the synchronous <c>finishWriting</c> that has
    /// been deprecated since iOS 6 (it can block the caller for the length of the flush with no
    /// way to observe failure). AVFoundation runs the handler on its own queue, so waiting on
    /// it here cannot deadlock even when a caller happens to be on the UI thread — and
    /// <see cref="SaveMp4"/> is a synchronous API, so somebody has to wait.</para>
    /// </summary>
    private static void FinishWriting(AVAssetWriter writer, string path)
    {
        using var done = new ManualResetEventSlim(false);
        writer.FinishWriting(() => done.Set());
        if (!done.Wait(AppendTimeout))
            throw new TimeoutException($"The H.264 encoder did not finish '{path}' within {AppendTimeout.TotalSeconds:0} s.");

        if (writer.Status != AVAssetWriterStatus.Completed)
        {
            throw new InvalidOperationException(
                $"AVAssetWriter could not finish '{path}': {writer.Error?.LocalizedDescription ?? writer.Status.ToString()}");
        }
    }

    /// <summary>Block until the encoder will take another frame. Polling rather than
    /// <c>RequestMediaData</c> because <see cref="SaveMp4"/> is a synchronous API called from
    /// a pipeline thread that has nothing else to do; the callback form would need a dispatch
    /// queue and a completion handshake to end up in exactly the same place.</summary>
    private static void WaitForInput(AVAssetWriterInput input, AVAssetWriter writer, int frameIndex)
    {
        DateTime deadline = DateTime.UtcNow + AppendTimeout;
        while (!input.ReadyForMoreMediaData)
        {
            if (writer.Status is AVAssetWriterStatus.Failed or AVAssetWriterStatus.Cancelled)
            {
                throw new InvalidOperationException(
                    $"AVAssetWriter stopped at frame {frameIndex}: {writer.Error?.LocalizedDescription ?? writer.Status.ToString()}");
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The H.264 encoder did not accept frame {frameIndex} within {AppendTimeout.TotalSeconds:0} s.");
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Turn one HWC RGB float frame into a BGRA CoreVideo buffer.
    ///
    /// <para>The float-to-byte rounding is deliberately the same expression the desktop
    /// provider uses (<c>(int)(v * 255 + 0.5)</c>, clamped), so the two encoders are fed
    /// identical pixels and any difference in the resulting file is the encoder's, not a
    /// quantisation difference nobody would think to look for.</para>
    /// </summary>
    private static CVPixelBuffer CreateBgraBuffer(RgbImage frame, int width, int height)
    {
        var pixelBuffer = new CVPixelBuffer(
            (nint)width, (nint)height, CVPixelFormatType.CV32BGRA,
            new CVPixelBufferAttributes
            {
                // IOSurface-backed is what VideoToolbox wants; without it the encoder copies
                // every frame through a staging buffer.
                AllocateWithIOSurface = true,
                CGImageCompatibility = true,
                CGBitmapContextCompatibility = true,
            });

        if (pixelBuffer.Handle == IntPtr.Zero)
        {
            pixelBuffer.Dispose();
            throw new InvalidOperationException($"CVPixelBufferCreate failed for a {width}x{height} BGRA frame.");
        }

        CVReturn locked = pixelBuffer.Lock(CVPixelBufferLock.None);
        if (locked != CVReturn.Success)
        {
            pixelBuffer.Dispose();
            throw new InvalidOperationException($"CVPixelBufferLockBaseAddress failed ({locked}) while writing a frame.");
        }

        try
        {
            int stride = (int)pixelBuffer.BytesPerRow;
            IntPtr baseAddress = pixelBuffer.BaseAddress;
            if (baseAddress == IntPtr.Zero || stride < width * 4)
                throw new InvalidOperationException($"CVPixelBuffer gave an unusable base address / stride {stride}.");

            float[] pixels = frame.Pixels;
            byte[] row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                int src = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    row[x * 4 + 0] = ToByte(pixels[src + x * 3 + 2]);   // B
                    row[x * 4 + 1] = ToByte(pixels[src + x * 3 + 1]);   // G
                    row[x * 4 + 2] = ToByte(pixels[src + x * 3 + 0]);   // R
                    row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, baseAddress + y * stride, row.Length);
            }
        }
        catch
        {
            pixelBuffer.Unlock(CVPixelBufferLock.None);
            pixelBuffer.Dispose();
            throw;
        }

        pixelBuffer.Unlock(CVPixelBufferLock.None);
        return pixelBuffer;
    }

    private static byte ToByte(float value)
    {
        int i = (int)(value * 255f + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }
}
#endif
