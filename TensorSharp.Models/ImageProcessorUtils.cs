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
using System.IO;
using System.Threading.Tasks;
using StbImageSharp;
using TensorSharp.Models.Media;

namespace TensorSharp.Models
{
    internal static class ImageProcessorUtils
    {
        /// <summary>
        /// Decode an image file to RGBA8 for the chat vision processors.
        ///
        /// <para>PNG and JPEG are decoded here, in managed code, exactly as they always were —
        /// in particular WITHOUT applying EXIF orientation, since every vision-model reference
        /// (llama.cpp's stb_image path) ignores it too. Everything else (HEIC/HEIF from an
        /// iPhone, GIF, WebP, BMP, ...) is the platform image provider's business:
        /// Magick.NET on desktop, ImageIO on iOS, via <see cref="MediaCodecs.Image"/>.</para>
        /// </summary>
        internal static byte[] DecodeImageToRGBA(byte[] fileBytes, out int width, out int height)
        {
            if (ImageFormatSniffer.IsPng(fileBytes))
                return PngCodec.Decode(fileBytes, out width, out height);

            if (IsJpeg(fileBytes))
                return DecodeJPEG(fileBytes, out width, out height);

            return MediaCodecs.Image.DecodeRgba(fileBytes, out width, out height);
        }

        internal static (int width, int height) ReadImageDimensions(string imagePath)
        {
            byte[] fileBytes = File.ReadAllBytes(imagePath);

            if (ImageFormatSniffer.IsPng(fileBytes))
                return PngCodec.ReadDimensions(fileBytes);

            if (IsJpeg(fileBytes))
            {
                DecodeJPEG(fileBytes, out int width, out int height);
                return (width, height);
            }

            return MediaCodecs.Image.ReadDimensions(fileBytes);
        }

        private static bool IsJpeg(byte[] fileBytes) =>
            fileBytes.Length >= 2 &&
            fileBytes[0] == 0xFF &&
            fileBytes[1] == 0xD8;

        private static byte[] DecodeJPEG(byte[] data, out int width, out int height)
        {
            try
            {
                ImageResult decoded = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
                width = decoded.Width;
                height = decoded.Height;
                return decoded.Data;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Failed to decode JPEG image.", ex);
            }
        }

        internal static byte[] CompositeOverWhite(byte[] rgba, int width, int height)
        {
            byte[] result = new byte[width * height * 4];
            Parallel.For(0, height, y =>
            {
                int srcRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int pixBase = srcRow + x * 4;
                    int a = rgba[pixBase + 3];
                    if (a == 255)
                    {
                        result[pixBase] = rgba[pixBase];
                        result[pixBase + 1] = rgba[pixBase + 1];
                        result[pixBase + 2] = rgba[pixBase + 2];
                    }
                    else
                    {
                        float alpha = a / 255f;
                        result[pixBase] = (byte)(rgba[pixBase] * alpha + 255 * (1 - alpha));
                        result[pixBase + 1] = (byte)(rgba[pixBase + 1] * alpha + 255 * (1 - alpha));
                        result[pixBase + 2] = (byte)(rgba[pixBase + 2] * alpha + 255 * (1 - alpha));
                    }

                    result[pixBase + 3] = 255;
                }
            });
            return result;
        }

        internal static byte[] BilinearResize(byte[] rgba, int srcW, int srcH, int dstW, int dstH)
        {
            byte[] result = new byte[dstW * dstH * 4];
            double xRatio = (double)srcW / dstW;
            double yRatio = (double)srcH / dstH;

            Parallel.For(0, dstH, dy =>
            {
                double srcY = (dy + 0.5) * yRatio - 0.5;
                int y0 = Math.Max(0, (int)srcY);
                int y1 = Math.Min(srcH - 1, y0 + 1);
                double fy = srcY - y0;

                for (int dx = 0; dx < dstW; dx++)
                {
                    double srcX = (dx + 0.5) * xRatio - 0.5;
                    int x0 = Math.Max(0, (int)srcX);
                    int x1 = Math.Min(srcW - 1, x0 + 1);
                    double fx = srcX - x0;

                    for (int c = 0; c < 3; c++)
                    {
                        double v00 = rgba[(y0 * srcW + x0) * 4 + c];
                        double v01 = rgba[(y0 * srcW + x1) * 4 + c];
                        double v10 = rgba[(y1 * srcW + x0) * 4 + c];
                        double v11 = rgba[(y1 * srcW + x1) * 4 + c];

                        double v = v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy) +
                                   v10 * (1 - fx) * fy + v11 * fx * fy;
                        result[(dy * dstW + dx) * 4 + c] = (byte)Math.Clamp(v + 0.5, 0, 255);
                    }
                    result[(dy * dstW + dx) * 4 + 3] = 255;
                }
            });

            return result;
        }

        internal static float[] ResizeRgbaToChannelFirstNormalized(byte[] rgba, int srcW, int srcH, int dstW, int dstH)
        {
            int pixels = dstW * dstH;
            float[] result = new float[3 * pixels];
            double xRatio = (double)srcW / dstW;
            double yRatio = (double)srcH / dstH;

            Parallel.For(0, dstH, dy =>
            {
                double srcY = (dy + 0.5) * yRatio - 0.5;
                int y0 = Math.Max(0, (int)srcY);
                int y1 = Math.Min(srcH - 1, y0 + 1);
                double fy = srcY - y0;

                for (int dx = 0; dx < dstW; dx++)
                {
                    double srcX = (dx + 0.5) * xRatio - 0.5;
                    int x0 = Math.Max(0, (int)srcX);
                    int x1 = Math.Min(srcW - 1, x0 + 1);
                    double fx = srcX - x0;

                    int dstIdx = dy * dstW + dx;
                    result[dstIdx] = BilinearSampleNormalized(rgba, srcW, x0, y0, x1, y1, fx, fy, 0);
                    result[pixels + dstIdx] = BilinearSampleNormalized(rgba, srcW, x0, y0, x1, y1, fx, fy, 1);
                    result[2 * pixels + dstIdx] = BilinearSampleNormalized(rgba, srcW, x0, y0, x1, y1, fx, fy, 2);
                }
            });

            return result;
        }

        private static float BilinearSampleNormalized(byte[] rgba, int srcW, int x0, int y0, int x1, int y1,
            double fx, double fy, int channel)
        {
            float v00 = CompositeChannelToNormalized(rgba, (y0 * srcW + x0) * 4, channel);
            float v01 = CompositeChannelToNormalized(rgba, (y0 * srcW + x1) * 4, channel);
            float v10 = CompositeChannelToNormalized(rgba, (y1 * srcW + x0) * 4, channel);
            float v11 = CompositeChannelToNormalized(rgba, (y1 * srcW + x1) * 4, channel);

            double v = v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy) +
                       v10 * (1 - fx) * fy + v11 * fx * fy;
            return Math.Clamp((float)v, -1f, 1f);
        }

        private static float CompositeChannelToNormalized(byte[] rgba, int pixelBase, int channel)
        {
            float alpha = rgba[pixelBase + 3] / 255f;
            float composited = rgba[pixelBase + channel] * alpha + 255f * (1f - alpha);
            return composited / 255f * 2f - 1f;
        }

        /// <summary>
        /// Resize (bilinear, composite over white) and normalize to channel-first
        /// [C, H, W] using explicit per-channel mean/std: (pixel/255 - mean) / std.
        /// Used by models whose mmproj declares its own image_mean / image_std
        /// (e.g. the Gemma 4 unified vision embedder with mean=0, std=1 -> [0,1]).
        /// </summary>
        internal static float[] ResizeRgbaToChannelFirstNormalized(
            byte[] rgba, int srcW, int srcH, int dstW, int dstH, float[] mean, float[] std)
        {
            int pixels = dstW * dstH;
            float[] result = new float[3 * pixels];
            double xRatio = (double)srcW / dstW;
            double yRatio = (double)srcH / dstH;

            Parallel.For(0, dstH, dy =>
            {
                double srcY = (dy + 0.5) * yRatio - 0.5;
                int y0 = Math.Max(0, (int)srcY);
                int y1 = Math.Min(srcH - 1, y0 + 1);
                double fy = srcY - y0;

                for (int dx = 0; dx < dstW; dx++)
                {
                    double srcX = (dx + 0.5) * xRatio - 0.5;
                    int x0 = Math.Max(0, (int)srcX);
                    int x1 = Math.Min(srcW - 1, x0 + 1);
                    double fx = srcX - x0;

                    int dstIdx = dy * dstW + dx;
                    for (int c = 0; c < 3; c++)
                    {
                        float v00 = CompositeChannel01(rgba, (y0 * srcW + x0) * 4, c);
                        float v01 = CompositeChannel01(rgba, (y0 * srcW + x1) * 4, c);
                        float v10 = CompositeChannel01(rgba, (y1 * srcW + x0) * 4, c);
                        float v11 = CompositeChannel01(rgba, (y1 * srcW + x1) * 4, c);

                        double v = v00 * (1 - fx) * (1 - fy) + v01 * fx * (1 - fy) +
                                   v10 * (1 - fx) * fy + v11 * fx * fy;
                        result[c * pixels + dstIdx] = (float)((v - mean[c]) / std[c]);
                    }
                }
            });

            return result;
        }

        // Composite an RGBA channel over a white background and rescale to [0, 1].
        private static float CompositeChannel01(byte[] rgba, int pixelBase, int channel)
        {
            float alpha = rgba[pixelBase + 3] / 255f;
            float composited = rgba[pixelBase + channel] * alpha + 255f * (1f - alpha);
            return composited / 255f;
        }
    }
}
