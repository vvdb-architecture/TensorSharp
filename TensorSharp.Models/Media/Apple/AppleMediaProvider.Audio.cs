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
using System.IO;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using TensorSharp.Models.Video;

#nullable enable

namespace TensorSharp.Models.Media.Apple;

/// <summary>Audio decode through <c>AVAudioFile</c>: the slot the desktop build never filled,
/// because on a desktop the managed WAV/MP3/OGG readers were enough. On a phone they are not —
/// a Voice Memo is .m4a, a share sheet hands over .caf or .aac, and the Files app is full of
/// .flac.</summary>
public sealed partial class AppleMediaProvider
{
    /// <summary>Frames per read. Big enough that a normal clip is one or two passes, small
    /// enough that a long recording never needs one giant contiguous buffer — an hour of
    /// 48 kHz stereo is 1.4 GB if read in a single shot.</summary>
    private const int AudioChunkFrames = 1 << 18;

    /// <summary>
    /// Decode to planar float PCM at the file's OWN sample rate and channel count.
    ///
    /// <para>Deliberately not conformed to 16 kHz mono: each model owns its resampler and its
    /// outputs are pinned by fixtures, so a provider that "helpfully" downmixed here would
    /// quietly change every audio model's input. <c>AVAudioFile</c>'s processing format is
    /// requested as non-interleaved float32 explicitly, which makes the plane layout the same
    /// for every container instead of depending on what the file happens to hold.</para>
    /// </summary>
    public DecodedAudio Decode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"audio file not found: {path}", path);

        using NSUrl url = NSUrl.FromFilename(path);
        using var file = new AVAudioFile(url, AVAudioCommonFormat.PCMFloat32, interleaved: false, out NSError error);
        if (error != null || file.Handle == IntPtr.Zero)
        {
            throw new NotSupportedException(
                $"AVFoundation could not decode '{Path.GetFileName(path)}': {error?.LocalizedDescription ?? "the file could not be opened"}. " +
                "AVAudioFile reads .m4a, .aac, .caf, .aiff, .flac, .wav and .mp3; a format outside that set has no " +
                "decoder on this device.");
        }

        AVAudioFormat format = file.ProcessingFormat;
        int channels = (int)format.ChannelCount;
        int sampleRate = (int)Math.Round(format.SampleRate);
        long totalFrames = file.Length;
        if (channels <= 0 || sampleRate <= 0)
            throw new InvalidDataException($"AVAudioFile reported {channels} channels at {sampleRate} Hz for '{Path.GetFileName(path)}'.");
        if (totalFrames <= 0)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' decodes to no audio frames.");
        if (totalFrames > int.MaxValue)
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is {totalFrames} frames long, past the 2^31 sample limit.");

        var planes = new float[channels][];
        for (int c = 0; c < channels; c++)
            planes[c] = new float[totalFrames];

        int written = 0;
        using (var buffer = new AVAudioPcmBuffer(format, AudioChunkFrames))
        {
            if (buffer.Handle == IntPtr.Zero)
                throw new InvalidOperationException($"Could not allocate a {AudioChunkFrames}-frame PCM buffer for '{Path.GetFileName(path)}'.");

            while (written < totalFrames)
            {
                if (!file.ReadIntoBuffer(buffer, out NSError readError) || readError != null)
                {
                    throw new InvalidDataException(
                        $"AVAudioFile failed part-way through '{Path.GetFileName(path)}' (at frame {written}): " +
                        $"{readError?.LocalizedDescription ?? "unknown error"}");
                }

                int produced = (int)buffer.FrameLength;
                if (produced <= 0)
                    break;   // end of file: a container whose header over-reports its length

                CopyPlanes(buffer, planes, written, Math.Min(produced, (int)totalFrames - written), channels, path);
                written += produced;
            }
        }

        // A trailing partial read is normal (the frame count in a compressed header is a
        // priming-adjusted estimate); returning the padded tail would append silence to
        // whatever the model is conditioned on.
        if (written < totalFrames)
        {
            for (int c = 0; c < channels; c++)
                Array.Resize(ref planes[c], written);
        }
        if (written == 0)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' decoded to zero samples.");

        return new DecodedAudio { Channels = planes, SampleRate = sampleRate };
    }

    /// <summary><c>floatChannelData</c> is a <c>float **</c>: one pointer per channel, each to
    /// <c>frameLength</c> contiguous samples (the buffer is non-interleaved by construction, so
    /// its stride is 1 and the planes can be copied wholesale).</summary>
    private static unsafe void CopyPlanes(AVAudioPcmBuffer buffer, float[][] planes, int offset, int count, int channels, string path)
    {
        var data = (float**)buffer.FloatChannelData;
        if (data == null)
        {
            throw new InvalidDataException(
                $"AVAudioFile returned no float channel data for '{Path.GetFileName(path)}'; the processing format is not float32.");
        }

        for (int c = 0; c < channels; c++)
        {
            if (data[c] == null)
                throw new InvalidDataException($"AVAudioFile returned no data for channel {c} of '{Path.GetFileName(path)}'.");
            Marshal.Copy((IntPtr)data[c], planes[c], offset, count);
        }
    }
}
#endif
