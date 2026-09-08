// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// Shared-prefix checkpoints against REAL weights: a new chat that starts from a
// clone of the checkpoint must produce exactly the tokens it would have produced
// from a cold prefill. Anything less than token-for-token equality means the copy
// missed some state (a stale device mirror, an un-drained recurrent state, a
// wrong ring index) and the "fast" new chat would be a subtly different model.
//
// Gemma 4 covers the circular sliding-window cache; Qwen 3.5 covers attention K/V
// plus GatedDeltaNet recurrent state (conv ring, write index, delta state), the
// case with the most device-resident pieces to bring home before copying.
//
// Opt-in:
//   TS_TEST_MODEL_DIR=<dir containing gemma-4-E2B*.gguf and/or Qwen3.5-9B*.gguf>
//   TS_TEST_GGML_BACKEND=metal   (the phone's backend; cpu also runs, slowly)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TensorSharp;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace InferenceWeb.Tests;

public class PrefixCheckpointExactnessTests
{
    private const string EnvModelDir = "TS_TEST_MODEL_DIR";
    private const int NewTokens = 12;
    private readonly ITestOutputHelper _output;

    public PrefixCheckpointExactnessTests(ITestOutputHelper output) { _output = output; }

    [ModelFact(EnvModelDir, "gemma-4-e2b")]
    public async Task Gemma4_NewChatFromACheckpoint_MatchesAColdPrefillTokenForToken()
        => await RunAsync("gemma-4-e2b");

    [ModelFact(EnvModelDir, "qwen3.5-9b")]
    public async Task Qwen35_NewChatFromACheckpoint_MatchesAColdPrefillTokenForToken()
        => await RunAsync("qwen3.5-9b");

    private async Task RunAsync(string ggufContains)
    {
        string dir = Environment.GetEnvironmentVariable(EnvModelDir);
        string modelPath = dir == null ? null : TestGates.FindGguf(dir, ggufContains);
        if (modelPath == null) { _output.WriteLine($"no {ggufContains} model; skipping"); return; }

        BackendType backend = (Environment.GetEnvironmentVariable("TS_TEST_GGML_BACKEND") ?? "cpu")
            .Trim().ToLowerInvariant() switch
        {
            "metal" => BackendType.GgmlMetal,
            "cuda" => BackendType.GgmlCuda,
            _ => BackendType.GgmlCpu,
        };

        string prevCheckpoints = Environment.GetEnvironmentVariable("TS_PREFIX_CHECKPOINTS");
        string prevRetained = Environment.GetEnvironmentVariable("TS_RETAINED_FUSED_CACHE");
        Environment.SetEnvironmentVariable("TS_PREFIX_CHECKPOINTS", "1");
        Environment.SetEnvironmentVariable("TS_RETAINED_FUSED_CACHE", "1");
        try
        {
            using var model = TensorSharp.Models.ModelBase.Create(modelPath, backend);

            // A shared prefix the size of a real system prompt, then two different
            // first messages. Everything is raw tokens: the chat template is not what
            // is under test here, the copied state is.
            List<int> prefix = model.Tokenizer.Encode(SharedPrefixText(), addSpecial: true);
            List<int> promptA = new(prefix);
            // Each first message is well past the live-cache rewind allowance (16
            // tokens), so chat B cannot be served by rewinding chat A's live cache and
            // must come from the checkpoint clone.
            promptA.AddRange(model.Tokenizer.Encode(
                " First question, and please take it seriously: name three long rivers in Europe, say which" +
                " countries each one flows through, and add one memorable fact about each of them.", addSpecial: false));
            List<int> promptB = new(prefix);
            promptB.AddRange(model.Tokenizer.Encode(
                " Second question, and please take it seriously: describe the water cycle in four short, numbered" +
                " steps, naming the physical process behind every step and where on Earth it mostly happens.", addSpecial: false));
            _output.WriteLine($"[{ggufContains}] prefix {prefix.Count} tokens, A {promptA.Count}, B {promptB.Count}, backend {backend}");

            // 1. Chat A, cold. Its prefill stops at the prefix boundary, the state
            //    is checkpointed there, and the chat carries on.
            List<int> coldB;
            List<int> clonedB;
            int reusedByB;
            using (var engine = new InferenceEngine(model, Config(), NullLogger.Instance))
            {
                var a = await GenerateAsync(engine, promptA, prefix.Count, "chat-a");
                Assert.Equal(0, a.completion.PrefixCacheReusedTokens);
                _output.WriteLine($"[A] {Decode(model, a.output)}");
                Assert.True(promptA.Count - prefix.Count > 16, "chat A's message must exceed the rewind allowance");
                Assert.True(promptB.Count - prefix.Count > 16, "chat B's message must exceed the rewind allowance");

                // 2. Chat B, in the same engine: nothing to continue from (A's live
                //    cache diverges 20+ tokens back), so it starts from the clone.
                var b = await GenerateAsync(engine, promptB, prefix.Count, "chat-b");
                reusedByB = b.completion.PrefixCacheReusedTokens;
                clonedB = b.output;
                _output.WriteLine($"[B from checkpoint] reused {reusedByB}: {Decode(model, clonedB)}");
            }

            // 3. Chat B again, in a FRESH engine with nothing cached: the reference.
            using (var engine = new InferenceEngine(model, Config(), NullLogger.Instance))
            {
                var b = await GenerateAsync(engine, promptB, 0, "chat-b-cold");
                Assert.Equal(0, b.completion.PrefixCacheReusedTokens);
                coldB = b.output;
                _output.WriteLine($"[B cold] {Decode(model, coldB)}");
            }

            Assert.True(reusedByB == prefix.Count,
                $"chat B reused {reusedByB} tokens; expected the whole {prefix.Count}-token prefix from the checkpoint clone");
            Assert.Equal(NewTokens, coldB.Count);
            Assert.Equal(coldB, clonedB);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TS_PREFIX_CHECKPOINTS", prevCheckpoints);
            Environment.SetEnvironmentVariable("TS_RETAINED_FUSED_CACHE", prevRetained);
        }
    }

    private static string SharedPrefixText()
    {
        // ~600 tokens of the kind of prose a system prompt is made of, past the
        // 512-token sliding window so Gemma 4's local layers have wrapped by the
        // boundary - the case a pooled snapshot cannot restore and a copy must.
        var sb = new System.Text.StringBuilder();
        sb.Append("You are a careful assistant running on a phone. Answer briefly and precisely. ");
        for (int i = 0; i < 40; i++)
        {
            sb.Append("Rule ").Append(i + 1).Append(": when the user asks about geography, prefer well-known facts, ")
              .Append("cite the continent, and keep each sentence under twenty words. ");
        }
        sb.Append("Tools: none are available in this session. ");
        return sb.ToString();
    }

    private static SchedulerConfig Config() => new()
    {
        MaxNumBatchedTokens = 4096,
        MaxNumRunningSequences = 4,
        MaxPrefillChunkSize = 1024,
        SoloPrefillChunkSize = 1024,
        NumBlocks = 128,
        BlockSize = 256,
        EnablePrefixCaching = true,
        DecodeQuantumTokens = 256,
    };

    private static async Task<(InferenceCompletion completion, List<int> output)> GenerateAsync(
        InferenceEngine engine, List<int> prompt, int sharedPrefix, string id)
    {
        var seq = new SequenceState(id, prompt, NewTokens, 256, SamplingConfig.Greedy, sharedPrefixTokens: sharedPrefix);
        var handle = engine.SubmitRequest(seq);
        var output = new List<int>();
        await foreach (int t in handle.Tokens.ReadAllAsync())
            output.Add(t);
        var completion = await handle.Completion;
        return (completion, output);
    }

    private static string Decode(TensorSharp.Models.ModelBase model, List<int> tokens)
    {
        try { return model.Tokenizer.Decode(tokens).Replace("\n", "\\n"); }
        catch { return string.Join(",", tokens); }
    }
}
