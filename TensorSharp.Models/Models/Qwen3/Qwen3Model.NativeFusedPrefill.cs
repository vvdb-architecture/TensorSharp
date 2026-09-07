// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.

using System;
using TensorSharp.GGML;

namespace TensorSharp.Models
{
    public partial class Qwen3Model
    {
        private readonly struct Qwen3NativeWeight
        {
            public readonly IntPtr Data;
            public readonly int Type;
            public readonly long Ne0;
            public readonly long Ne1;
            public readonly long Bytes;

            public Qwen3NativeWeight(IntPtr data, int type, long ne0, long ne1, long bytes)
            {
                Data = data;
                Type = type;
                Ne0 = ne0;
                Ne1 = ne1;
                Bytes = bytes;
            }
        }

        private Qwen3PrefillLayerArgs[] _qwen3PrefillLayers;
        private ModelDecodeArrays _qwen3PrefillArraysSource;
        private Qwen3NativeWeight _qwen3PrefillEmbedding;
        private Qwen3NativeWeight _qwen3PrefillLmHead;
        private IntPtr _qwen3PrefillOutputNorm;

        private bool CanUseNativeQwen3Prefill =>
            IsGgmlBackend &&
            !IsTensorParallel &&
            _modelDecodeArrays != null &&
            _hasQkNorm && !_hasQkvBias &&
            string.Equals(Config.Architecture, "qwen3", StringComparison.OrdinalIgnoreCase) &&
            !HasSidecarWeightScales &&
            !string.Equals(
                Environment.GetEnvironmentVariable("TS_QWEN3_MODEL_PREFILL"),
                "0", StringComparison.Ordinal);

        private unsafe bool TryGetQwen3NativeWeight(
            string name, out Qwen3NativeWeight native)
        {
            if (_quantWeights.TryGetValue(name, out QuantizedWeight quant) &&
                quant != null && quant.CacheKey != IntPtr.Zero && quant.Scale == 1.0f)
            {
                native = new Qwen3NativeWeight(
                    quant.CacheKey, quant.GgmlType,
                    quant.Ne0, quant.Ne1, quant.RawBytes);
                return true;
            }

            if (_weights.TryGetValue(name, out Tensor weight) &&
                weight != null && weight.DimensionCount == 2 &&
                weight.ElementType == DType.Float32 && weight.IsContiguous())
            {
                native = new Qwen3NativeWeight(
                    (IntPtr)GetFloatPtr(weight), 0,
                    weight.Sizes[1], weight.Sizes[0],
                    weight.ElementCount() * sizeof(float));
                return native.Data != IntPtr.Zero;
            }

            native = default;
            return false;
        }

        private unsafe bool EnsureQwen3PrefillDescriptors()
        {
            ModelDecodeArrays a = _modelDecodeArrays;
            if (a == null)
                return false;

            int n = Config.NumLayers;
            bool rebuild = _qwen3PrefillLayers == null ||
                           _qwen3PrefillLayers.Length != n ||
                           !ReferenceEquals(_qwen3PrefillArraysSource, a);
            if (rebuild)
            {
                if (!TryGetQwen3NativeWeight(
                        "token_embd.weight", out _qwen3PrefillEmbedding))
                    return false;
                if (!TryGetQwen3NativeWeight(
                        "output.weight", out _qwen3PrefillLmHead) &&
                    !TryGetQwen3NativeWeight(
                        "token_embd.weight", out _qwen3PrefillLmHead))
                    return false;
                if (!_weights.TryGetValue(
                        "output_norm.weight", out Tensor outputNorm) ||
                    outputNorm == null || outputNorm.ElementType != DType.Float32 ||
                    outputNorm.ElementCount() != Config.HiddenSize)
                    return false;

                _qwen3PrefillOutputNorm = (IntPtr)GetFloatPtr(outputNorm);
                if (_qwen3PrefillOutputNorm == IntPtr.Zero)
                    return false;

                var desc = new Qwen3PrefillLayerArgs[n];
                for (int l = 0; l < n; ++l)
                {
                    // This optimized path deliberately consumes the fused QKV
                    // and fused gate/up weights used by Bonsai. Mixed-type GGUFs
                    // that could not be fused stay on the existing path.
                    if (a.Qkv[l] == IntPtr.Zero || a.Gu[l] == IntPtr.Zero ||
                        a.QNorm == null || a.KNorm == null ||
                        a.QNorm[l] == IntPtr.Zero || a.KNorm[l] == IntPtr.Zero)
                        return false;

                    string[] wn = _layerWeightNames[l];
                    if ((_quantWeights.TryGetValue(wn[1], out QuantizedWeight qkv) &&
                         qkv.Scale != 1.0f) ||
                        (_quantWeights.TryGetValue(wn[4], out QuantizedWeight o) &&
                         o.Scale != 1.0f) ||
                        (_quantWeights.TryGetValue(wn[6], out QuantizedWeight gu) &&
                         gu.Scale != 1.0f) ||
                        (_quantWeights.TryGetValue(wn[7], out QuantizedWeight down) &&
                         down.Scale != 1.0f))
                        return false;

                    desc[l] = new Qwen3PrefillLayerArgs
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
                        QkvBias = a.QkvBias != null ? a.QkvBias[l] : IntPtr.Zero,
                        QkvNe0 = a.QkvNe0,
                        QkvNe1 = a.QkvNe1,
                        QkvBytes = a.QkvBytesPerLayer[l],
                        ONe0 = a.ONe0,
                        ONe1 = a.ONe1,
                        OBytes = a.OBytesPerLayer[l],
                        GuNe0 = a.GuNe0,
                        GuNe1 = a.GuNe1,
                        GuBytes = a.GuBytesPerLayer[l],
                        DownNe0 = a.DownNe0,
                        DownNe1 = a.DownNe1,
                        DownBytes = a.DownBytesPerLayer[l],
                        StructBytes = Qwen3PrefillLayerArgs.NativeSize,
                        QkvType = a.QkvTypes[l],
                        OType = a.OTypes[l],
                        GuType = a.GuTypes[l],
                        DownType = a.DownTypes[l],
                    };
                }

                _qwen3PrefillLayers = desc;
                _qwen3PrefillArraysSource = a;
            }

            // Cache holders and cache growth replace the storage pointers while
            // the layer weights remain fixed. Mirror the active holder on every
            // invocation, just as the retained decode descriptor does.
            for (int l = 0; l < n; ++l)
            {
                _qwen3PrefillLayers[l].KCache = a.KCache[l];
                _qwen3PrefillLayers[l].VCache = a.VCache[l];
            }
            return true;
        }

        private unsafe bool TryNativeQwen3Prefill(
            int[] tokens, int startPos, bool computeLogits, out float[] logits)
        {
            logits = null;
            if (tokens == null || tokens.Length <= 1 ||
                !CanUseNativeQwen3Prefill ||
                !EnsureQwen3PrefillDescriptors())
                return false;

            // A retained single-token graph owns external bindings into this
            // holder. Tear it down before a separately allocated prefill graph
            // touches the same resident K/V buffers; it will be rebuilt on the
            // next decode token with the newly populated prefix.
            DropNativeQwen3DecodeForActiveCache();

            int kvType = _kvCacheDtype.GgmlType();
            if (kvType != 0 && kvType != 1 && kvType != 2 && kvType != 8)
                return false;

            int maxSeqLen = (int)_kvCacheK[0].Sizes[1];
            if (computeLogits &&
                (_logitsBuffer == null || _logitsBuffer.Length != Config.VocabSize))
                _logitsBuffer = new float[Config.VocabSize];

            bool ok;
            if (computeLogits)
            {
                fixed (float* logitsPtr = _logitsBuffer)
                {
                    ok = GgmlBasicOps.TryQwen3ModelPrefill(
                        _qwen3PrefillLayers, Config.NumLayers,
                        tokens, tokens.Length, startPos,
                        (IntPtr)logitsPtr, Config.VocabSize, true,
                        _qwen3PrefillEmbedding.Data,
                        _qwen3PrefillEmbedding.Type,
                        _qwen3PrefillEmbedding.Ne0,
                        _qwen3PrefillEmbedding.Ne1,
                        _qwen3PrefillEmbedding.Bytes,
                        _qwen3PrefillOutputNorm,
                        _qwen3PrefillLmHead.Data,
                        _qwen3PrefillLmHead.Type,
                        _qwen3PrefillLmHead.Ne0,
                        _qwen3PrefillLmHead.Ne1,
                        _qwen3PrefillLmHead.Bytes,
                        Config.HiddenSize, Config.NumHeads,
                        Config.NumKVHeads, Config.HeadDim,
                        Config.IntermediateSize, maxSeqLen, kvType,
                        Config.Eps, Config.RopeBase,
                        1.0f / Config.RopeScale, 2,
                        _ropeOriginalContext, _ropeExtFactor,
                        _ropeAttnFactor, _ropeBetaFast, _ropeBetaSlow);
                }
                if (ok)
                    logits = _logitsBuffer;
            }
            else
            {
                ok = GgmlBasicOps.TryQwen3ModelPrefill(
                    _qwen3PrefillLayers, Config.NumLayers,
                    tokens, tokens.Length, startPos,
                    IntPtr.Zero, Config.VocabSize, false,
                    _qwen3PrefillEmbedding.Data,
                    _qwen3PrefillEmbedding.Type,
                    _qwen3PrefillEmbedding.Ne0,
                    _qwen3PrefillEmbedding.Ne1,
                    _qwen3PrefillEmbedding.Bytes,
                    _qwen3PrefillOutputNorm,
                    _qwen3PrefillLmHead.Data,
                    _qwen3PrefillLmHead.Type,
                    _qwen3PrefillLmHead.Ne0,
                    _qwen3PrefillLmHead.Ne1,
                    _qwen3PrefillLmHead.Bytes,
                    Config.HiddenSize, Config.NumHeads,
                    Config.NumKVHeads, Config.HeadDim,
                    Config.IntermediateSize, maxSeqLen, kvType,
                    Config.Eps, Config.RopeBase,
                    1.0f / Config.RopeScale, 2,
                    _ropeOriginalContext, _ropeExtFactor,
                    _ropeAttnFactor, _ropeBetaFast, _ropeBetaSlow);
            }

            return ok;
        }
    }
}
