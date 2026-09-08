// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorSharp.Runtime.Scheduling;

namespace TensorAgent.Tests;

/// <summary>
/// The compute gate with a real model behind it.
///
/// <para>
/// <see cref="BackgroundGenerationTests"/> proves the wiring on a host with no model,
/// where a closed gate holds a refusal back. That says nothing about the part that
/// matters on a phone: the ENGINE. It decodes on its own thread into an unbounded
/// channel, so a page that stops reading, or a wrapper that stops pulling, stops
/// nothing — the GPU keeps being asked for the next token, and on iOS that is the ask
/// that comes back refused and poisons ggml-metal for the rest of the process. The
/// engine's step loop has to park itself, and only a running engine can show that it
/// does.
/// </para>
/// <para>
/// So this loads real weights, asks for a long answer, closes the gate a few tokens
/// in, and watches the engine's own step counter: it must stop moving while the gate
/// is closed and move again the moment it opens, and the answer must then finish
/// without a single token lost. No GPU refusal can happen on a development machine, so
/// the engine is also expected to come through it unmarked.
/// </para>
/// </summary>
[Collection(LiveModelCollection.Name)]
public sealed class BackgroundGenerationLiveTests : LiveModelHarness
{
    [LiveModelFact]
    public async Task ClosingTheGateParksTheEngineMidAnswerAndOpeningItFinishesTheAnswer()
    {
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        AgentAppHost host = Start(model, weights, maxTokens: 400);
        await LoadAsync(model);
        // A load starts the prefix-cache warm-up; the turn below stops and waits for
        // it, so its cancellation is not what this measures.
        JsonElement session = await OpenSessionAsync();
        string sessionId = session.GetProperty("sessionId").GetString()!;

        // Read the stream on its own task, because the point is to observe it NOT
        // moving: a reader that blocks in the test body cannot be watched.
        var frames = new ConcurrentQueue<JsonElement>();
        int tokens = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abandon = new CancellationTokenSource(RequestTimeout);
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        sessionId,
                        messages = new[] { new { role = "user", content = "Count from one to two hundred, one number per line, nothing else." } },
                        maxTokens = 300,
                        think = false,
                    }), Encoding.UTF8, "application/json"),
                };
                using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abandon.Token);
                await using Stream stream = await response.Content.ReadAsStreamAsync(abandon.Token);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(abandon.Token) is { } line)
                {
                    if (!line.StartsWith("data: ", StringComparison.Ordinal))
                        continue;
                    JsonElement frame = JsonSerializer.Deserialize<JsonElement>(line[6..]);
                    if (frame.TryGetProperty("token", out _))
                        Interlocked.Increment(ref tokens);
                    frames.Enqueue(frame);
                }
                finished.SetResult();
            }
            catch (Exception ex)
            {
                finished.SetException(ex);
            }
        });

        // A few tokens in, so the prefill is over and the engine is decoding.
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref tokens) < 6 && !finished.Task.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(10))
            await Task.Delay(50);
        Assert.False(finished.Task.IsCompleted, "the answer finished before the gate could be closed; ask for a longer one");
        Assert.True(Volatile.Read(ref tokens) >= 6, "no tokens arrived within ten minutes");

        InferenceEngine engine = host.ModelService.EngineHost.TryGetEngine()!;
        Assert.NotNull(engine);
        Assert.Same(host.Compute, engine.ComputeGate);

        // ---- the app leaves ------------------------------------------------------
        host.Compute.Close();
        long stepsAtClose = engine.TotalStepsRun;
        int tokensAtClose = Volatile.Read(ref tokens);
        long heldBefore = engine.StepsHeldByGate;

        await Task.Delay(TimeSpan.FromSeconds(3));

        long stepsWhileClosed = engine.TotalStepsRun - stepsAtClose;
        int tokensWhileClosed = Volatile.Read(ref tokens) - tokensAtClose;
        // At most the ONE step that was already in flight when the gate closed -- that
        // is the residual iOS cannot help with either, and it is why recovery exists.
        // Any more and the loop did not park.
        Assert.True(stepsWhileClosed <= 1,
            $"the engine ran {stepsWhileClosed} steps while the gate was closed; it must park between steps");
        Assert.True(tokensWhileClosed <= 2,
            $"{tokensWhileClosed} tokens reached the reader while the gate was closed");
        Assert.True(engine.StepsHeldByGate > heldBefore, "the engine never reported being held by the gate");
        Assert.False(finished.Task.IsCompleted, "the turn ended while the gate was closed");
        Assert.True(host.Turns.IsBusy, "the turn is still owned and running, merely paused");

        // ---- and comes back --------------------------------------------------------
        host.Compute.Open();
        await finished.Task.WaitAsync(RequestTimeout);

        int total = Volatile.Read(ref tokens);
        Assert.True(total > tokensAtClose + 5, $"only {total - tokensAtClose} more tokens came after the gate opened");
        Assert.Contains(frames, f => f.TryGetProperty("done", out _));
        Assert.DoesNotContain(frames, f => f.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String);
        Assert.DoesNotContain(frames, f => f.TryGetProperty("restart", out _));

        // The text is one answer, uninterrupted: the pause left no hole and no error
        // in it, and the tokens after the gate opened are the same answer's.
        string answer = TextOf(frames);
        Assert.False(string.IsNullOrWhiteSpace(answer), "the model produced no text");

        // Nothing was refused, so nothing needs rebuilding.
        Assert.False(host.EngineNeedsReload);
        Assert.Equal(0, host.EngineRebuilds);
        Assert.Equal(1, host.Compute.Closures);
    }

    [LiveModelFact]
    public async Task TheEngineIsRebuiltOnANewBackendAndAnswersAgain()
    {
        // The repair path, driven deliberately: this machine's GPU never refuses
        // anything, so the poisoned state is asserted from outside and the whole of
        // "unload, recreate the backend, load again, answer" is run for real -- which
        // is the part that crashed with a double free before the order was fixed.
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        AgentAppHost host = Start(model, weights, maxTokens: 64);
        await LoadAsync(model);
        PublishTheWayTheAppInstalls(host, model, weights);

        // The same shape the wrapper sees: a `done` frame carrying the device's sentence.
        host.EnginePoisoned += _ => { };
        Assert.False(host.EngineNeedsReload);
        var marked = typeof(AgentAppHost).GetMethod("NoteEngineMayBePoisoned",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        marked.Invoke(host, new object[] { "ggml_metal_synchronize: error: command buffer 0 failed with status 5 | error: Insufficient Permission (to submit GPU work from background)" });
        Assert.True(host.EngineNeedsReload);

        var clock = Stopwatch.StartNew();
        Assert.True(host.RecoverEngineIfNeeded(warmAfterwards: false));
        clock.Stop();
        Assert.False(host.EngineNeedsReload);
        Assert.Equal(1, host.EngineRebuilds);
        Assert.Equal(AgentAppHost.ModelLoadState.Loaded, host.ModelLoad);

        // And the rebuilt engine answers, through the real route, on the real backend.
        JsonElement session = await OpenSessionAsync();
        List<JsonElement> frames = await StreamAsync(new
        {
            sessionId = session.GetProperty("sessionId").GetString(),
            messages = new[] { new { role = "user", content = "Reply with exactly the word: pineapple" } },
            maxTokens = 32,
            think = false,
        });
        Assert.DoesNotContain(frames, f => f.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String);
        Assert.Contains("pineapple", TextOf(frames), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.EngineRebuilds);

        // The checkpoint every new chat starts from lived in the backend that was just
        // thrown away, and a turn-driven rebuild deliberately runs no warm-up. So the
        // turn above paid for the whole prompt (the first line below) -- and it must
        // have left the checkpoint behind for the NEXT new chat, or every new chat
        // after a recovery would pay it again until something happened to warm the
        // cache. That is the "new chat is slow after switching apps" a user would feel.
        TurnStats paidInFull = StatsOf(frames);
        JsonElement later = await OpenSessionAsync();
        List<JsonElement> next = await StreamAsync(new
        {
            sessionId = later.GetProperty("sessionId").GetString(),
            messages = new[] { new { role = "user", content = "Reply with exactly the word: mango" } },
            maxTokens = 32,
            think = false,
        });
        TurnStats afterwards = StatsOf(next);
        Console.WriteLine($"live model: after the rebuild, first new chat {paidInFull}; next new chat {afterwards}");
        Assert.True(afterwards.ReusedTokens > 0.9 * afterwards.PromptTokens,
            $"the new chat after a rebuilt engine reused only {afterwards.ReusedTokens} of {afterwards.PromptTokens} tokens: the shared-prefix checkpoint was not re-taken");
    }

    /// <summary>
    /// Put the weights where a recovery reload will look for them.
    ///
    /// <para>
    /// The harness hosts the file under its SOURCE name so a test can prove which
    /// checkpoint it ran; the app installs a model under the catalog's own file names,
    /// and <c>UseModel</c> -- which is what the recovery calls -- resolves the selected
    /// model to exactly those. Hard links rather than symlinks, because the store's
    /// completeness check reads <c>FileInfo.Length</c>, which on macOS is a symlink's
    /// own length. The projector goes in too when the source directory has it; a
    /// catalog entry whose projector is required cannot be reloaded without it.
    /// </para>
    /// </summary>
    private static void PublishTheWayTheAppInstalls(AgentAppHost host, CatalogModel model, string weights)
    {
        string directory = Path.Combine(host.Paths.ModelsDirectory, model.Id);
        Directory.CreateDirectory(directory);
        string source = Path.GetDirectoryName(Path.GetFullPath(weights))!;
        foreach ((string name, string from) in new[]
                 {
                     (model.Weights.FileName, Path.GetFullPath(weights)),
                     (model.Projector?.FileName ?? string.Empty, Path.Combine(source, model.Projector?.FileName ?? "-")),
                 })
        {
            if (name.Length == 0 || !File.Exists(from))
                continue;
            string target = Path.Combine(directory, name);
            if (File.Exists(target) || Directory.Exists(target) || new FileInfo(target).LinkTarget is not null)
                File.Delete(target);
            if (link(from, target) != 0)
                File.Copy(from, target);
        }
        Assert.True(File.Exists(host.Paths.SelectedModelPath(host.Settings.Load())),
            "the catalog-named weights are not in the store, so a recovery reload cannot find them");
    }

    /// <summary>A hard link, the way the shell's <c>ln</c> makes one; the runtime has no API for it.</summary>
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int link(string existing, string created);
}
