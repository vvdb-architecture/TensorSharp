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

namespace InferenceWeb.Tests;

public class Qwen3PerSequenceCacheLifecycleTests
{
    [Fact]
    public void NativeDecodePoolLock_CoversDecodeResetAndDropEntryPoints()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "TensorSharp.GGML.Native",
            "ggml_ops_qwen3_decode.cpp"));

        Assert.Contains("std::mutex g_qwen3_decode_mutex;", source);

        string decode = EntryPointSource(
            source,
            "TSG_EXPORT int TSGgml_Qwen3ModelDecodeLogits(",
            "TSG_EXPORT void TSGgml_Qwen3ResetDecodeCache()");
        int tryStart = decode.IndexOf("try", StringComparison.Ordinal);
        int decodeLock = decode.IndexOf(
            "std::lock_guard<std::mutex> pool_lock(g_qwen3_decode_mutex);",
            StringComparison.Ordinal);
        int firstPoolAccess = decode.IndexOf("qwen3_decode_pool()", StringComparison.Ordinal);
        Assert.True(tryStart >= 0 && decodeLock > tryStart && firstPoolAccess > decodeLock,
            "The RAII pool lock must be acquired inside the decode try block before any retained entry is accessed.");

        string reset = EntryPointSource(
            source,
            "TSG_EXPORT void TSGgml_Qwen3ResetDecodeCache()",
            "TSG_EXPORT void TSGgml_Qwen3DropDecodeCache(");
        int resetLock = reset.IndexOf(
            "std::lock_guard<std::mutex> pool_lock(g_qwen3_decode_mutex);",
            StringComparison.Ordinal);
        int resetAccess = reset.IndexOf("pool.reset_all()", StringComparison.Ordinal);
        Assert.True(resetLock >= 0 && resetAccess > resetLock,
            "Reset must hold the pool lock before freeing retained entries.");

        string drop = source[source.IndexOf(
            "TSG_EXPORT void TSGgml_Qwen3DropDecodeCache(",
            StringComparison.Ordinal)..];
        int dropLock = drop.IndexOf(
            "std::lock_guard<std::mutex> pool_lock(g_qwen3_decode_mutex);",
            StringComparison.Ordinal);
        int dropAccess = drop.IndexOf("pool.drop_by_cache", StringComparison.Ordinal);
        Assert.True(dropLock >= 0 && dropAccess > dropLock,
            "Drop must hold the pool lock before freeing a retained entry.");
    }

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
            // Construct only the ownership state needed by OnSequenceReleased.
            // The stale dictionary holder models the pre-growth checkout while
            // the model fields contain the replacement arrays that are actually
            // live. No GGUF or GPU backend is needed for this lifecycle contract.
            var model = (Qwen3Model)RuntimeHelpers.GetUninitializedObject(typeof(Qwen3Model));
            SetField(typeof(ModelBase), model, "<Config>k__BackingField", new ModelConfig { NumLayers = 1 });
            SetField(typeof(ModelBase), model, "<ExecutionPlan>k__BackingField", new BackendExecutionPlan(BackendType.Cpu));
            SetField(typeof(ModelBase), model, "_cacheSeqLen", 9);
            SetField(typeof(Qwen3Model), model, "_kvCacheK", new[] { liveK });
            SetField(typeof(Qwen3Model), model, "_kvCacheV", new[] { liveV });
            SetField(typeof(Qwen3Model), model, "_kvCacheCapacity", 16);

            Type holderType = typeof(Qwen3Model).GetNestedType(
                "Qwen3KvCacheHolder", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Qwen3 holder type not found.");
            object staleHolder = RuntimeHelpers.GetUninitializedObject(holderType);
            SetField(holderType, staleHolder, "K", new[] { staleK });
            SetField(holderType, staleHolder, "V", new[] { staleV });
            SetField(holderType, staleHolder, "Capacity", 8);
            SetField(holderType, staleHolder, "SeqLen", 4);

            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), holderType);
            var holders = (IDictionary)(Activator.CreateInstance(dictionaryType)
                ?? throw new InvalidOperationException("Qwen3 holder dictionary could not be created."));
            holders.Add("grown", staleHolder);
            SetField(typeof(Qwen3Model), model, "_fusedHolders", holders);
            SetField(typeof(Qwen3Model), model, "_activeFusedKey", "grown");

            model.RefreshActiveFusedHolderAfterCacheGrowth();

            object refreshedHolder = holders["grown"]!;
            Assert.Same(liveK, ((Tensor[])GetField(holderType, refreshedHolder, "K"))[0]);
            Assert.Same(liveV, ((Tensor[])GetField(holderType, refreshedHolder, "V"))[0]);
            Assert.Equal(16, (int)GetField(holderType, refreshedHolder, "Capacity"));
            Assert.Equal(9, (int)GetField(holderType, refreshedHolder, "SeqLen"));

            // Release must still fail safe if a stale record is observed (for
            // example, a future replacement path forgets the proactive refresh).
            holders["grown"] = staleHolder;
            model.OnSequenceReleased("grown");

            Assert.True(IsDisposed(liveK));
            Assert.True(IsDisposed(liveV));
            Assert.False(IsDisposed(staleK));
            Assert.False(IsDisposed(staleV));
            Assert.False(model.HasFusedSequenceCache("grown"));
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

    private static object GetField(Type declaringType, object target, string name)
    {
        FieldInfo field = declaringType.GetField(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {declaringType.Name}.{name} not found.");
        return field.GetValue(target)!;
    }

    private static bool IsDisposed(Tensor tensor)
    {
        FieldInfo field = typeof(Tensor).GetField(
            "isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Tensor disposal field not found.");
        return (int)field.GetValue(tensor)! != 0;
    }

    private static string EntryPointSource(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Native entry point marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Native entry point terminator not found: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TensorSharp.sln"))
                || File.Exists(Path.Combine(dir.FullName, "TensorSharp.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the TensorSharp repository root.");
    }
}
