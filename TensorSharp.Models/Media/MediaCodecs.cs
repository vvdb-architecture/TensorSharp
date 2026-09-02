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
    /// The one seam between TensorSharp.Models and the native media libraries.
    ///
    /// <para>Everything in this assembly that touches a codec goes through these four
    /// properties; nothing outside <c>Media/Desktop/</c> names OpenCvSharp, ImageMagick or
    /// <c>System.Diagnostics.Process</c> (a drift-guard test enforces it). The reason is iOS:
    /// OpenCvSharp and Magick.NET ship no ios-arm64 binaries and process spawning does not
    /// exist there, so the app must be able to supply AVFoundation / ImageIO instead — and
    /// the desktop build must keep the exact libraries the generative pipelines were validated
    /// against. Each property starts on <see cref="ManagedMediaProvider"/>; on non-iOS builds a
    /// module initializer in <c>Media/Desktop/DesktopMediaProvider.cs</c> then swaps in
    /// OpenCV + ffmpeg + Magick.NET before any user code runs, so desktop behaviour is unchanged.</para>
    ///
    /// <para><b>Contract for an iOS provider</b> (lives in the app; not in this assembly):</para>
    /// <list type="bullet">
    /// <item><description><see cref="Image"/>: ImageIO <c>CGImageSource</c> for HEIC/HEIF, JPEG,
    /// PNG, GIF and WebP. <see cref="IImageCodec.DecodeRgba"/> must apply
    /// <c>kCGImagePropertyOrientation</c> (draw through a <c>CGContext</c> or use
    /// <c>UIImage</c>'s oriented rendering) and return straight — NOT premultiplied — RGBA8:
    /// draw into a <c>CGBitmapContext</c> with <c>kCGImageAlphaNoneSkipLast</c> when the
    /// source has no alpha, or un-premultiply after <c>kCGImageAlphaPremultipliedLast</c>.
    /// <see cref="IImageCodec.ReadDimensions"/> comes from <c>CGImageSourceCopyPropertiesAtIndex</c>
    /// (stored dimensions, no orientation). <see cref="IImageCodec.EncodePng"/> can simply
    /// forward to <see cref="ManagedMediaProvider"/>; <see cref="IImageCodec.ResizeRgb8"/> for
    /// <see cref="ResizeFilter.Lanczos"/> should be validated against a Magick.NET reference
    /// (the MiniMax-H3 parity fixtures) before replacing the managed Pillow Lanczos, which is
    /// already a faithful port and needs no native code.</description></item>
    /// <item><description><see cref="Video"/>: <c>AVAssetReader</c> + <c>AVAssetReaderTrackOutput</c>
    /// with <c>kCVPixelFormatType_32BGRA</c> (hand the locked base address's bytes, its
    /// <c>bytesPerRow</c> as the stride and <see cref="PixelLayout.Bgra"/> to the callback).
    /// <see cref="IVideoDecoder.Probe"/> = <c>AVAssetTrack.nominalFrameRate</c> and
    /// <c>duration * nominalFrameRate</c>. Step sequentially for dense requests and reopen the
    /// reader with a <c>timeRange</c> for sparse ones (<see cref="MediaHelper.PrefersSequentialStepping"/>).
    /// Honour a repeated index by delivering the same frame twice.</description></item>
    /// <item><description><see cref="VideoEncoder"/>: <c>AVAssetWriter</c> with
    /// <c>AVVideoCodecTypeH264</c> (yuv420p) and an <c>AVAssetWriterInputPixelBufferAdaptor</c>;
    /// return <c>"h264"</c>.</description></item>
    /// <item><description><see cref="Audio"/>: <c>AVAudioFile</c> read into a non-interleaved
    /// float32 <c>AVAudioPCMBuffer</c> at the file's own rate and channel count; this is what
    /// makes .m4a / .aac / .caf / .flac (Voice Memos) work, since the managed decoders only
    /// read WAV, MP3 and Ogg Vorbis.</description></item>
    /// </list>
    /// <para>Register at startup, before the first model call: <c>MediaCodecs.Image = new ImageIOCodec();</c>
    /// etc. Set every property the platform can serve; a property left on the managed default
    /// throws an actionable <see cref="NotSupportedException"/> when reached rather than
    /// silently producing nothing.</para>
    /// </summary>
    public static class MediaCodecs
    {
        private static IImageCodec _image = ManagedMediaProvider.Instance;
        private static IVideoDecoder _video = ManagedMediaProvider.Instance;
        private static IVideoEncoder _videoEncoder = ManagedMediaProvider.Instance;
        private static IAudioDecoder _audio = ManagedMediaProvider.Instance;

        /// <summary>Still-image decode / PNG encode / resize.</summary>
        public static IImageCodec Image
        {
            get => _image;
            set => _image = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Video frame access for <see cref="MediaHelper"/>.</summary>
        public static IVideoDecoder Video
        {
            get => _video;
            set => _video = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>MP4 export for generated video (<see cref="TensorSharp.Models.WanVideo.VideoIO"/>).</summary>
        public static IVideoEncoder VideoEncoder
        {
            get => _videoEncoder;
            set => _videoEncoder = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Audio decode for the formats the managed WAV/MP3/OGG readers do not cover.</summary>
        public static IAudioDecoder Audio
        {
            get => _audio;
            set => _audio = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>One line naming the registered providers, for a startup banner or a bug report.</summary>
        public static string Describe() =>
            $"image={_image.GetType().Name} video={_video.GetType().Name} " +
            $"mp4={_videoEncoder.GetType().Name} audio={_audio.GetType().Name}";
    }
}
