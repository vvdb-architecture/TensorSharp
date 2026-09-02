// TEMPORARY measurement harness (not for commit): peak ggml-metal allocation for
// MiniMax-H3 components, using the same technique as
// TensorAgent.Tests.MediaScenarioTests.AnEditFitsTheMemoryBudgetOfTheSmallestPhoneItIsOfferedTo.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TensorSharp.GGML;
using TensorSharp.Models.MiniMaxH3;
using TensorSharp.Models.Video;
using TensorSharp.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace InferenceWeb.Tests
{
    public class MiniMaxH3PhoneBudgetMeasureTests
    {
        private const string DirEnv = "TS_MINIMAX_H3_DIR";
        private readonly ITestOutputHelper _output;
        public MiniMaxH3PhoneBudgetMeasureTests(ITestOutputHelper output) { _output = output; }

        private static string Root => Environment.GetEnvironmentVariable(DirEnv);

        private sealed class Sampler : IDisposable
        {
            private readonly CancellationTokenSource _cts = new();
            private readonly Thread _thread;
            public long Peak;
            public long Total;
            public long Baseline;
            private readonly ITestOutputHelper _out;
            private readonly List<string> _marks = new();

            public Sampler(ITestOutputHelper output)
            {
                _out = output;
                if (GgmlBasicOps.TryGetBackendMemory(out long free, out long total))
                {
                    Baseline = total - free;
                    Total = total;
                }
                _thread = new Thread(() =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        if (GgmlBasicOps.TryGetBackendMemory(out long f, out long t))
                        {
                            long now = t - f;
                            long prev = Interlocked.Read(ref Peak);
                            if (now > prev) Interlocked.Exchange(ref Peak, now);
                            Interlocked.Exchange(ref Total, t);
                        }
                        Thread.Sleep(25);
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public long Now()
            {
                if (GgmlBasicOps.TryGetBackendMemory(out long f, out long t))
                {
                    long now = t - f;
                    long prev = Interlocked.Read(ref Peak);
                    if (now > prev) Interlocked.Exchange(ref Peak, now);
                    return now;
                }
                return -1;
            }

            public void Mark(string label)
            {
                long now = Now();
                string line = $"[mem] {label,-34} now={now / 1e9,8:F3} GB  peak={Interlocked.Read(ref Peak) / 1e9,8:F3} GB";
                _marks.Add(line);
                Console.WriteLine(line);
                _out.WriteLine(line);
            }

            public void Dispose() { _cts.Cancel(); _thread.Join(2000); }
        }

        private static float[] Fill(float[] a, Random r)
        {
            for (long i = 0; i < a.LongLength; i++)
            {
                double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
                a[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
            return a;
        }

        // (a) ref2va DiT alone: construct + ONE forward step (the constructor only
        // mmaps and pins host pointers; the device upload happens on first Forward).
        [ModelFact(DirEnv)]
        public void MeasureRef2VaDitPeak()
        {
            string path = Path.Combine(Root ?? ".", "minimax_h3_ref2va_pruned-Q4_K.gguf");
            if (!File.Exists(path)) { _output.WriteLine("absent"); return; }

            using var s = new Sampler(_output);
            _output.WriteLine($"[mem] backend total (recommendedMaxWorkingSetSize) = {s.Total / 1e9:F3} GB");
            _output.WriteLine($"[mem] baseline = {s.Baseline / 1e9:F3} GB");
            s.Mark("before ctor");

            var ctorClock = Stopwatch.StartNew();
            using var dit = new MiniMaxH3DiT(path, BackendType.GgmlMetal, null);
            ctorClock.Stop();
            s.Mark($"after ctor ({ctorClock.Elapsed.TotalSeconds:F1}s)");
            _output.WriteLine("[cfg] " + dit.Config);

            // Default request geometry: 640x384, 22 frames (MiniMaxH3Pipeline's default).
            int width = 640, height = 384, frames = 22, textLength = 370;
            var shape = MiniMaxH3Geometry.Resolve(width, height, frames);
            int videoCount = shape.VideoTokenCount, audioCount = shape.AudioTokenCount;
            int keyframeTokens = shape.TokenGridWidth * shape.TokenGridHeight;
            var rng = new Random(4321);
            var textHidden = Fill(new float[(long)textLength * dit.Config.TextDim], new Random(1234));
            var video = Fill(new float[(long)videoCount * dit.Config.VideoPatchDim], rng);
            var audio = Fill(new float[(long)audioCount * dit.Config.AudioLatentChannels], rng);
            var keyframe = Fill(new float[(long)keyframeTokens * dit.Config.VideoPatchDim], rng);
            var layout = MiniMaxH3Layout.Build(textLength, shape, 1.0f,
                conditionFrames: new List<int> { 1 }, keyframeAtEnd: new List<bool> { false });
            _output.WriteLine($"[geo] {shape.Width}x{shape.Height} {shape.Frames}f -> {layout.TokenCount} packed tokens");
            s.Mark("before forward");

            var fw = Stopwatch.StartNew();
            var (v, a) = dit.Forward(video, videoCount, audio, audioCount,
                textHidden, textLength, layout, 1.0f,
                conditionTokens: keyframe, conditionCount: keyframeTokens);
            fw.Stop();
            s.Mark($"after 1 forward ({fw.Elapsed.TotalSeconds:F1}s)");
            Assert.True(v.Length > 0 && a.Length > 0);

            var fw2 = Stopwatch.StartNew();
            dit.Forward(video, videoCount, audio, audioCount,
                textHidden, textLength, layout, 0.75f,
                conditionTokens: keyframe, conditionCount: keyframeTokens);
            fw2.Stop();
            s.Mark($"after 2nd forward ({fw2.Elapsed.TotalSeconds:F1}s)");

            long peak = s.Peak;
            string result = $"RESULT ref2va-DiT peak={peak / 1e9:F3} GB  baseline={s.Baseline / 1e9:F3} GB  net={(peak - s.Baseline) / 1e9:F3} GB  deviceTotal={s.Total / 1e9:F3} GB";
            Console.WriteLine(result);
            _output.WriteLine(result);
        }

        // (b) Qwen3-VL-32B encoder alone: construct + one text-only encode.
        [ModelFact(DirEnv)]
        public void MeasureTextEncoderPeak()
        {
            string path = Path.Combine(Root ?? ".", "qwen3vl_32b_minimax_h3-Q4_K_M.gguf");
            if (!File.Exists(path)) { _output.WriteLine("absent"); return; }

            using var s = new Sampler(_output);
            _output.WriteLine($"[mem] backend total = {s.Total / 1e9:F3} GB, baseline = {s.Baseline / 1e9:F3} GB");
            s.Mark("before ctor");

            var ctorClock = Stopwatch.StartNew();
            using var te = new MiniMaxH3TextEncoder(path, null, BackendType.GgmlMetal, null);
            ctorClock.Stop();
            s.Mark($"after ctor ({ctorClock.Elapsed.TotalSeconds:F1}s)");
            _output.WriteLine($"[cfg] {te.Config}, hasVision={te.HasVision}");

            var ids = te.Tokenize(
                "A slow cinematic dolly shot through a rain-soaked neon alley at night, "
                + "steam rising from a grate, reflections shimmering in the puddles, "
                + "a lone figure in a long coat walking away from camera.");
            _output.WriteLine($"[tok] {ids.Count} tokens");
            s.Mark("before encode");

            var enc = Stopwatch.StartNew();
            float[] hidden = te.Encode(ids);
            enc.Stop();
            s.Mark($"after encode ({enc.Elapsed.TotalSeconds:F1}s)");
            Assert.True(hidden.Length > 0);

            long peak = s.Peak;
            string result = $"RESULT qwen3vl-32b-encoder peak={peak / 1e9:F3} GB  baseline={s.Baseline / 1e9:F3} GB  net={(peak - s.Baseline) / 1e9:F3} GB  deviceTotal={s.Total / 1e9:F3} GB";
            Console.WriteLine(result);
            _output.WriteLine(result);
        }

        // (c) End-to-end: the whole Ref2VA pipeline through MiniMaxH3Model.GenerateVideo
        // (encoder with the vision tower, video VAE reference encode, N denoise steps,
        // video + audio VAE decode) with the peak sampled across the entire run.
        [ModelFact(DirEnv)]
        public void MeasureEndToEndPeak()
        {
            string dit = Path.Combine(Root ?? ".", "minimax_h3_ref2va_pruned-Q4_K.gguf");
            if (!File.Exists(dit)) { _output.WriteLine("absent"); return; }
            string refImage = Environment.GetEnvironmentVariable("H3_REF_IMAGE");
            Assert.True(File.Exists(refImage), $"H3_REF_IMAGE missing: {refImage}");
            int steps = int.TryParse(Environment.GetEnvironmentVariable("H3_STEPS"), out int st) && st > 0 ? st : 20;
            int frames = int.TryParse(Environment.GetEnvironmentVariable("H3_FRAMES"), out int fr) && fr > 0 ? fr : 22;

            using var s = new Sampler(_output);
            _output.WriteLine($"[mem] backend total = {s.Total / 1e9:F3} GB, baseline = {s.Baseline / 1e9:F3} GB");
            s.Mark("before model ctor");

            using var model = new MiniMaxH3Model(dit, BackendType.GgmlMetal);
            s.Mark("after model ctor");

            var p = new VideoGenerationParams
            {
                Width = 640,
                Height = 384,
                Frames = frames,
                Steps = steps,
                CfgScale = 1.0f,
                Seed = 42,
                ReferenceImagePaths = new List<string> { refImage },
            };
            var clock = Stopwatch.StartNew();
            var video = model.GenerateVideo(
                "A slow cinematic dolly shot through a rain-soaked neon alley at night.", p);
            clock.Stop();
            s.Mark($"after GenerateVideo ({clock.Elapsed.TotalSeconds:F1}s)");
            Assert.NotNull(video);

            long peak = s.Peak;
            string result = $"RESULT h3-end-to-end steps={steps} frames={frames} peak={peak / 1e9:F3} GB  "
                + $"baseline={s.Baseline / 1e9:F3} GB  net={(peak - s.Baseline) / 1e9:F3} GB  deviceTotal={s.Total / 1e9:F3} GB";
            Console.WriteLine(result);
            _output.WriteLine(result);
        }
    }
}
