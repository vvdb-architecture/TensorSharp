// Copyright (c) Zhongkai Fu. All rights reserved.
// Licensed under the BSD-3-Clause license in the repository root.

using System;
using System.Collections.Generic;

namespace TensorSharp.Models
{
    public abstract partial class ModelBase
    {
        // Stored matrix bytes approximate bandwidth cost without making a small
        // tensor of one format outweigh the rest of a mixed-precision checkpoint.
        // Each architecture identifies the matrices its active graph actually uses.
        protected static (long matchingBytes, long totalBytes) MeasureMatmulWeightBytes(
            IReadOnlyDictionary<string, QuantizedWeight> quantWeights,
            IReadOnlyDictionary<string, Tensor> weights, int ggmlType,
            Func<string, bool> isActiveMatrix)
        {
            long matchingBytes = 0, totalBytes = 0;
            foreach (var entry in quantWeights)
            {
                if (entry.Value.Ne1 <= 1 || !isActiveMatrix(entry.Key))
                    continue;
                totalBytes += entry.Value.RawBytes;
                if (entry.Value.GgmlType == ggmlType)
                    matchingBytes += entry.Value.RawBytes;
            }
            foreach (var entry in weights)
            {
                Tensor tensor = entry.Value;
                if (tensor.ElementType != DType.Float32 || tensor.DimensionCount != 2 ||
                    tensor.Sizes[0] <= 1 || !isActiveMatrix(entry.Key) || quantWeights.ContainsKey(entry.Key))
                    continue;
                // Count F32 matrices stored separately, excluding a cached F32
                // mirror when the quantized matrix is the active representation.
                totalBytes += tensor.ElementCount() * sizeof(float);
            }
            return (matchingBytes, totalBytes);
        }
    }
}
