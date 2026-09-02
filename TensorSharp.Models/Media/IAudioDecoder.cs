// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.
using TensorSharp.Models.Video;

namespace TensorSharp.Models.Media
{
    /// <summary>Decodes an audio file to planar float PCM at its NATIVE sample rate and
    /// channel count. Resampling and channel folding are the caller's (each model has its
    /// own resampler and the results are pinned by fixtures), so a provider must not
    /// "helpfully" conform to 16 kHz mono.</summary>
    public interface IAudioDecoder
    {
        /// <summary>Decode <paramref name="path"/>. Throws <see cref="System.NotSupportedException"/>
        /// for a format the provider does not read, naming what would.</summary>
        DecodedAudio Decode(string path);
    }
}
