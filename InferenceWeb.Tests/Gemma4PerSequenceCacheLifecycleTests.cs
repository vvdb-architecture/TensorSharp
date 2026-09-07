// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Models;
using TensorSharp.Runtime;
using Xunit;

namespace InferenceWeb.Tests;

public class Gemma4PerSequenceCacheLifecycleTests
{
    [Fact]
    public void ReleaseActiveHolder_DisposesGrowthReplacedLiveArrays()
    {
        var allocator = new CpuAllocator(BlasEnum.DotNet);
        var staleK = new Tensor(allocator, DType.Float32, 1);
        var staleV = new Tensor(allocator, DType.Float32, 1);
        var liveK = new Tensor(allocator, DType.Float32, 1);
        var liveV = new Tensor(allocator, DType.Float32, 1);

        try
        {
            // Build only the per-sequence cache state needed by OnSequenceReleased.
            // Skipping the GGUF constructor keeps this lifecycle regression CPU-only;
            // the private holder is populated through reflection because its type is
            // deliberately an implementation detail.
            var model = (Gemma4Model)RuntimeHelpers.GetUninitializedObject(typeof(Gemma4Model));
            SetField(typeof(ModelBase), model, "<Config>k__BackingField", new ModelConfig { NumLayers = 1 });
            SetField(typeof(ModelBase), model, "<ExecutionPlan>k__BackingField", new BackendExecutionPlan(BackendType.Cpu));
            SetField(typeof(ModelBase), model, "_cacheSeqLen", 9);
            SetField(typeof(Gemma4Model), model, "_kvDonorMap", new Dictionary<int, int>());
            SetField(typeof(Gemma4Model), model, "_kvCacheK", new[] { liveK });
            SetField(typeof(Gemma4Model), model, "_kvCacheV", new[] { liveV });
            SetField(typeof(Gemma4Model), model, "_kvCacheSize", new[] { 16 });
            SetField(typeof(Gemma4Model), model, "_kvCacheGlobalCapacity", 16);

            Type holderType = typeof(Gemma4Model).GetNestedType(
                "Gemma4KvCacheHolder", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Gemma4 holder type not found.");
            object staleHolder = RuntimeHelpers.GetUninitializedObject(holderType);
            SetField(holderType, staleHolder, "K", new[] { staleK });
            SetField(holderType, staleHolder, "V", new[] { staleV });
            SetField(holderType, staleHolder, "Sizes", new[] { 8 });
            SetField(holderType, staleHolder, "GlobalCapacity", 8);
            SetField(holderType, staleHolder, "SeqLen", 4);

            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), holderType);
            var holders = (IDictionary)(Activator.CreateInstance(dictionaryType)
                ?? throw new InvalidOperationException("Gemma4 holder dictionary could not be created."));
            holders.Add("grown", staleHolder);
            SetField(typeof(Gemma4Model), model, "_fusedHolders", holders);
            SetField(typeof(Gemma4Model), model, "_activeFusedKey", "grown");

            model.OnSequenceReleased("grown");

            Assert.True(IsDisposed(liveK));
            Assert.True(IsDisposed(liveV));
            Assert.False(IsDisposed(staleK));
            Assert.False(IsDisposed(staleV));
        }
        finally
        {
            staleK.Dispose();
            staleV.Dispose();
            liveK.Dispose();
            liveV.Dispose();
        }
    }

    private static void SetField(Type declaringType, object target, string name, object value)
    {
        FieldInfo field = declaringType.GetField(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {declaringType.Name}.{name} not found.");
        field.SetValue(target, value);
    }

    private static bool IsDisposed(Tensor tensor)
    {
        FieldInfo field = typeof(Tensor).GetField(
            "isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Tensor disposal field not found.");
        return (int)field.GetValue(tensor)! != 0;
    }
}
