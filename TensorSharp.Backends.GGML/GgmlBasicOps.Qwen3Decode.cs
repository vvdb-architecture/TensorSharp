// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

using System;
using System.Runtime.InteropServices;

namespace TensorSharp.GGML
{
    /// <summary>
    /// One dense Qwen3 layer for the persistent whole-model decode graph.
    /// Field order must match <c>TSGgmlQwen3LayerDesc</c> in
    /// <c>ggml_ops_qwen3_decode.cpp</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Qwen3LayerDecodeArgs
    {
        public IntPtr AttnNormW;
        public IntPtr QkvW;
        public IntPtr QNormW;
        public IntPtr KNormW;
        public IntPtr OW;
        public IntPtr FfnNormW;
        public IntPtr GuW;
        public IntPtr DownW;
        public IntPtr KCache;
        public IntPtr VCache;

        public long QkvBytes;
        public long OBytes;
        public long GuBytes;
        public long DownBytes;

        public int QkvType;
        public int OType;
        public int GuType;
        public int DownType;
        public int StructBytes;
    }

    public partial class GgmlBasicOps
    {
        /// <summary>
        /// Decode one Qwen3 token through every layer and the final RMSNorm/LM
        /// head in one resident GGML graph, returning logits directly.
        /// </summary>
        public static void Qwen3ModelDecodeLogits(
            Qwen3LayerDecodeArgs[] layers,
            int tokenId,
            IntPtr tokenEmbedding, int tokenEmbeddingType,
            long tokenEmbeddingNe0, long tokenEmbeddingNe1,
            long tokenEmbeddingBytes,
            int hiddenSize, int position,
            int numHeads, int numKvHeads, int headDim, int cacheSize,
            int intermediateSize, int kvCacheType,
            float eps, float ropeBase, float ropeFreqScale,
            int ropeMode, int ropeOriginalContext,
            float ropeExtFactor, float ropeAttnFactor,
            float ropeBetaFast, float ropeBetaSlow,
            IntPtr logits, int vocabSize,
            IntPtr lmHead, int lmHeadType,
            long lmHeadNe0, long lmHeadNe1, long lmHeadBytes,
            IntPtr finalNorm)
        {
            GgmlNative.Qwen3ModelDecodeLogits(
                layers, layers?.Length ?? 0,
                tokenId,
                tokenEmbedding, tokenEmbeddingType,
                tokenEmbeddingNe0, tokenEmbeddingNe1,
                tokenEmbeddingBytes,
                hiddenSize, position,
                numHeads, numKvHeads, headDim, cacheSize,
                intermediateSize, kvCacheType,
                eps, ropeBase, ropeFreqScale,
                ropeMode, ropeOriginalContext,
                ropeExtFactor, ropeAttnFactor,
                ropeBetaFast, ropeBetaSlow,
                logits, vocabSize,
                lmHead, lmHeadType,
                lmHeadNe0, lmHeadNe1, lmHeadBytes,
                finalNorm);
        }

        /// <summary>Drop all retained Qwen3 decode graphs.</summary>
        public static void Qwen3ResetDecodeCache() => GgmlNative.Qwen3ResetDecodeCache();

        /// <summary>Drop the retained graph whose first-layer K cache has this host address.</summary>
        public static void Qwen3DropDecodeCache(IntPtr firstKCache)
            => GgmlNative.Qwen3DropDecodeCache(firstKCache);
    }
}
