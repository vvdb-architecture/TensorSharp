// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.
using TensorSharp.Models.QwenImage;

namespace TensorSharp.Models.Media
{
    /// <summary>Writes generated frames as an MP4 file.</summary>
    public interface IVideoEncoder
    {
        /// <summary>
        /// Write <paramref name="frames"/> (all the same size, HWC RGB float [0,1]) at
        /// <paramref name="fps"/> to <paramref name="path"/>, whose directory already exists.
        /// Returns the codec actually used: <c>"h264"</c> plays in browsers, <c>"mp4v"</c>
        /// (MPEG-4 Part 2) does not — callers that serve the file check this. A provider that
        /// cannot encode at all throws <see cref="System.InvalidOperationException"/> (or
        /// <see cref="System.NotSupportedException"/> when no encoder is registered) rather
        /// than writing a file nothing can play.
        /// </summary>
        string SaveMp4(string path, RgbImage[] frames, int fps);
    }
}
