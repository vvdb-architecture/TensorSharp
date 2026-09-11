// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

using System;
using System.Runtime.InteropServices;
using TensorSharp.GGML;

namespace TensorSharp.Models
{
    public partial class Qwen3Model
    {
        private Qwen3LayerDecodeArgs[] _qwen3DecodeLayers;
        private ModelDecodeArrays _qwen3DecodeArraysSource;

        private bool CanUseNativeQwen3LogitsDecode =>
            _backend == BackendType.GgmlMetal &&
            !IsTensorParallel &&
            _modelDecodeArrays != null &&
            _hasQkNorm && !_hasQkvBias &&
            string.Equals(Config.Architecture, "qwen3", StringComparison.OrdinalIgnoreCase) &&
            !HasSidecarWeightScales &&
            !string.Equals(
                Environment.GetEnvironmentVariable("TS_QWEN3_FUSED_LOGITS_DECODE"),
                "0", StringComparison.Ordinal);

        private void EnsureQwen3DecodeDescriptors()
        {
            ModelDecodeArrays a = _modelDecodeArrays;
            if (a == null)
                return;

            int n = Config.NumLayers;
            bool rebuild = _qwen3DecodeLayers == null ||
                           _qwen3DecodeLayers.Length != n ||
                           !ReferenceEquals(_qwen3DecodeArraysSource, a);
            if (rebuild)
            {
                int structBytes = Marshal.SizeOf<Qwen3LayerDecodeArgs>();
                var desc = new Qwen3LayerDecodeArgs[n];
                for (int l = 0; l < n; l++)
                {
                    desc[l] = new Qwen3LayerDecodeArgs
                    {
                        AttnNormW = a.AttnNorm[l],
                        QkvW = a.Qkv[l],
                        QNormW = a.QNorm[l],
                        KNormW = a.KNorm[l],
                        OW = a.O[l],
                        FfnNormW = a.FfnNorm[l],
                        GuW = a.Gu[l],
                        DownW = a.Down[l],
                        KCache = a.KCache[l],
                        VCache = a.VCache[l],
                        QkvBytes = a.QkvBytesPerLayer[l],
                        OBytes = a.OBytesPerLayer[l],
                        GuBytes = a.GuBytesPerLayer[l],
                        DownBytes = a.DownBytesPerLayer[l],
                        QkvType = a.QkvTypes[l],
                        OType = a.OTypes[l],
                        GuType = a.GuTypes[l],
                        DownType = a.DownTypes[l],
                        StructBytes = structBytes,
                    };
                }
                _qwen3DecodeLayers = desc;
                _qwen3DecodeArraysSource = a;
            }
            else
            {
                // A per-request cache holder swap updates ModelDecodeArrays in
                // place. Mirror only the two movable pointers; weights stay fixed.
                for (int l = 0; l < n; l++)
                {
                    _qwen3DecodeLayers[l].KCache = a.KCache[l];
                    _qwen3DecodeLayers[l].VCache = a.VCache[l];
                }
            }
        }

        private unsafe bool TryNativeQwen3DecodeLogits(
            int token, int position, out float[] logits)
        {
            logits = null;
            if (!CanUseNativeQwen3LogitsDecode)
                return false;

            EnsureQwen3DecodeDescriptors();
            if (_qwen3DecodeLayers == null)
                return false;
            for (int l = 0; l < _qwen3DecodeLayers.Length; l++)
            {
                // This specialized path intentionally covers Bonsai/Qwen3's
                // uniformly fused QKV + gate/up layout. Mixed-type split weights
                // continue through the proven generic decoder.
                if (_qwen3DecodeLayers[l].QkvW == IntPtr.Zero ||
                    _qwen3DecodeLayers[l].GuW == IntPtr.Zero)
                    return false;
            }

            if (!_weights.TryGetValue("output_norm.weight", out Tensor finalNorm))
                return false;
            if (!TryGetQwen3NativeWeight(
                    "token_embd.weight", out Qwen3NativeWeight tokenEmbedding))
                return false;
            if (!_quantWeights.TryGetValue("output.weight", out QuantizedWeight lmHead) &&
                !_quantWeights.TryGetValue("token_embd.weight", out lmHead))
                return false;
            if (lmHead.Scale != 1.0f)
                return false;

            if (_logitsBuffer == null || _logitsBuffer.Length != Config.VocabSize)
                _logitsBuffer = new float[Config.VocabSize];

            float* normPtr = GetFloatPtr(finalNorm);
            fixed (float* logitsPtr = _logitsBuffer)
            {
                GgmlBasicOps.Qwen3ModelDecodeLogits(
                    _qwen3DecodeLayers,
                    token,
                    tokenEmbedding.Data, tokenEmbedding.Type,
                    tokenEmbedding.Ne0, tokenEmbedding.Ne1,
                    tokenEmbedding.Bytes,
                    Config.HiddenSize, position,
                    Config.NumHeads, Config.NumKVHeads, Config.HeadDim,
                    (int)_kvCacheK[0].Sizes[1],
                    Config.IntermediateSize, _kvCacheDtype.GgmlType(),
                    Config.Eps, Config.RopeBase, 1.0f / Config.RopeScale,
                    2, _ropeOriginalContext,
                    _ropeExtFactor, _ropeAttnFactor,
                    _ropeBetaFast, _ropeBetaSlow,
                    (IntPtr)logitsPtr, Config.VocabSize,
                    lmHead.CacheKey, lmHead.GgmlType,
                    lmHead.Ne0, lmHead.Ne1, lmHead.RawBytes,
                    (IntPtr)normPtr);
            }

            logits = _logitsBuffer;
            return true;
        }

        private void DropNativeQwen3DecodeForActiveCache()
        {
            if (_backend != BackendType.GgmlMetal || _kvCacheK == null ||
                _kvCacheK.Length == 0 || _kvCacheK[0] == null)
                return;
            // Resolve the key from the CURRENT holder, not the descriptor mirror.
            // A holder swap updates _modelDecodeArrays immediately but refreshes
            // _qwen3DecodeLayers only on the next decode invocation; using that
            // mirror here can drop the previous holder and leave this one's graph
            // alive across prefill/growth/injection.
            IntPtr firstK = TensorComputePrimitives.GetStoragePointer(_kvCacheK[0]);
            if (firstK != IntPtr.Zero)
                GgmlBasicOps.Qwen3DropDecodeCache(firstK);
        }

        private void DropNativeQwen3DecodeForCache(Tensor[] kCache)
        {
            if (_backend != BackendType.GgmlMetal || kCache == null ||
                kCache.Length == 0 || kCache[0] == null)
                return;
            GgmlBasicOps.Qwen3DropDecodeCache(
                TensorComputePrimitives.GetStoragePointer(kCache[0]));
        }
    }
}
