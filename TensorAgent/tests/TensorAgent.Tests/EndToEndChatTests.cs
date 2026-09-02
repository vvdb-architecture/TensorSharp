using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Settings;

namespace TensorAgent.Tests;

/// <summary>
/// A fact that needs real weights on this machine, and says how to supply them
/// rather than passing silently when they are absent.
/// </summary>
public sealed class LiveModelFactAttribute : FactAttribute
{
    public LiveModelFactAttribute()
    {
        if (EndToEndChatTests.Unavailable(out _, out _) is { } reason)
            Skip = reason;
    }
}

/// <summary>
/// A real model, a real turn, over the real API.
///
/// <para>
/// Every other test here checks a part in isolation, which is what makes them fast
/// and hermetic — and also what makes them unable to answer the only question that
/// matters: does a person typing into this app get an answer back. That needs
/// weights, so it needs a machine that has some. Point
/// <c>TENSORAGENT_TEST_MODEL_DIR</c> at a directory holding the catalog's own file
/// names and these run; otherwise they skip, saying what to set.
/// </para>
/// <para>
/// The model is linked into place rather than copied. A catalog entry is eight
/// gigabytes and copying one per test class would be slower than the inference.
/// </para>
/// </summary>
public sealed class EndToEndChatTests : IDisposable
{
    /// <summary>Where the catalog's GGUF files can be found on this machine.</summary>
    public const string ModelDirVariable = "TENSORAGENT_TEST_MODEL_DIR";

    /// <summary>An alternative file name for the weights, when the local copy is named differently.</summary>
    public const string ModelFileVariable = "TENSORAGENT_TEST_MODEL_FILE";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-e2e-" + Guid.NewGuid().ToString("N"));
    private AgentAppHost? _host;
    private HttpClient? _client;

    public void Dispose()
    {
        _client?.Dispose();
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Why these cannot run here, or null when they can.</summary>
    internal static string? Unavailable(out CatalogModel model, out string weights)
    {
        model = null!;
        weights = string.Empty;

        string? directory = Environment.GetEnvironmentVariable(ModelDirVariable);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return $"set {ModelDirVariable} to a directory holding one of the catalog's GGUF files";

        string? named = Environment.GetEnvironmentVariable(ModelFileVariable);
        foreach (CatalogModel candidate in ModelCatalog.BuiltIn)
        {
            if (candidate.Kind == CatalogArchitectureKind.Diffusion)
                continue;
            string path = Path.Combine(directory, named ?? candidate.Weights.FileName);
            if (!File.Exists(path))
                continue;
            // A truncated or unrelated file would fail deep inside the loader with a
            // message about tensors; check the size the catalog expects instead.
            if (named is null && new FileInfo(path).Length != candidate.Weights.Bytes)
                continue;
            model = candidate;
            weights = path;
            return null;
        }
        return $"no catalog GGUF found in {directory}; set {ModelFileVariable} to use a differently named file";
    }

    private AgentAppHost Start(CatalogModel model, string weights)
    {
        var paths = new AgentPaths(Path.Combine(_root, "data"), Path.Combine(_root, "cache")) { DeviceMemoryGB = 16 };
        paths.EnsureCreated();

        // Link, do not copy: these files are gigabytes.
        string target = Path.Combine(paths.ModelsDirectory, model.Id);
        Directory.CreateDirectory(target);
        File.CreateSymbolicLink(Path.Combine(target, model.Weights.FileName), weights);

        var settings = new SettingsStore(paths.SettingsFile);
        AppSettings chosen = settings.Load();
        chosen.SelectedModelId = model.Id;
        chosen.MaxTokens = 64;
        settings.Save(chosen);

        _host = new AgentAppHost(paths);
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri(_host.Server.BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        _client.DefaultRequestHeaders.Add("Cookie", $"{LoopbackServer.TokenCookie}={_host.Server.Token}");
        return _host;
    }

    /// <summary>Load the selected model on the CPU backend, which is what a test machine has.</summary>
    private async Task LoadAsync(CatalogModel model)
    {
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/api/models/load", new
        {
            model = model.Weights.FileName,
            backend = "ggml_cpu",
        });
        string payload = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"loading the model failed: {(int)response.StatusCode} {payload}");
    }

    /// <summary>Read a server-sent-event stream into its frames, as the page does.</summary>
    private async Task<List<JsonElement>> StreamAsync(object body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await _client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.True(response.IsSuccessStatusCode, $"chat failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var frames = new List<JsonElement>();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            frames.Add(JsonSerializer.Deserialize<JsonElement>(line[6..]));
        }
        return frames;
    }

    private static string TextOf(IEnumerable<JsonElement> frames)
    {
        var text = new StringBuilder();
        foreach (JsonElement frame in frames)
        {
            if (frame.TryGetProperty("token", out JsonElement token) && token.GetString() is { } piece)
                text.Append(piece);
            else if (frame.TryGetProperty("replace", out JsonElement replace) && replace.GetString() is { } whole)
                text.Clear().Append(whole);
        }
        return text.ToString();
    }

    [LiveModelFact]
    public async Task AModelLoadsAndAnswersAQuestionThroughTheSameApiThePageCalls()
    {
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        AgentAppHost host = Start(model, weights);
        await LoadAsync(model);

        // The models route must now report it, because that is what the page reads
        // before it will let anyone send anything.
        JsonElement models = JsonSerializer.Deserialize<JsonElement>(await _client!.GetStringAsync("/api/models"));
        Assert.False(string.IsNullOrEmpty(models.GetProperty("loaded").GetString()));

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = session.GetProperty("sessionId").GetString()!;
        string conversationId = session.GetProperty("conversationId").GetString()!;

        var clock = Stopwatch.StartNew();
        List<JsonElement> frames = await StreamAsync(new
        {
            sessionId,
            messages = new[] { new { role = "user", content = "Reply with exactly the word: pineapple" } },
            maxTokens = 32,
            think = false,
        });
        clock.Stop();

        Assert.NotEmpty(frames);
        Assert.Contains(frames, f => f.TryGetProperty("token", out _));
        Assert.Contains(frames, f => f.TryGetProperty("done", out _));

        string answer = TextOf(frames);
        Assert.False(string.IsNullOrWhiteSpace(answer), "the model produced no text");
        Assert.Contains("pineapple", answer, StringComparison.OrdinalIgnoreCase);

        // The turn must have been written down, because on a phone the page's own
        // copy does not survive the app being closed.
        Conversation? saved = new ConversationStore(host.Paths.ConversationsDirectory).Load(conversationId);
        Assert.NotNull(saved);
        Assert.Contains(saved!.Messages, m => m.Role == "user" && m.Content.Contains("pineapple", StringComparison.OrdinalIgnoreCase));
    }

    [LiveModelFact]
    public async Task ASecondTurnInTheSameSessionSeesTheFirst()
    {
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = session.GetProperty("sessionId").GetString()!;

        var history = new List<object>
        {
            new { role = "user", content = "Remember this: my favourite fruit is a pineapple." },
        };
        // Room to answer. A budget tight enough to truncate the reply turns a memory
        // test into a test of how quickly this particular fine-tune gets to the point.
        List<JsonElement> first = await StreamAsync(new { sessionId, messages = history, maxTokens = 128, think = false });
        history.Add(new { role = "assistant", content = TextOf(first) });
        history.Add(new { role = "user", content = "What is my favourite fruit?" });

        List<JsonElement> second = await StreamAsync(new { sessionId, messages = history, maxTokens = 128, think = false });
        string answer = TextOf(second);
        Assert.True(answer.Contains("pineapple", StringComparison.OrdinalIgnoreCase),
            "the model did not recall the first turn; it answered: " + answer);
    }

    /// <summary>The numbers the final frame reports about one turn.</summary>
    private readonly record struct TurnStats(int PromptTokens, int ReusedTokens, double ReusePercent, double Seconds, int Tokens)
    {
        public override string ToString() =>
            $"prompt {PromptTokens}, reused {ReusedTokens} ({ReusePercent:0.0}%), {Tokens} tokens in {Seconds:0.0}s";
    }

    private static TurnStats StatsOf(IEnumerable<JsonElement> frames)
    {
        JsonElement done = frames.Last(f => f.TryGetProperty("done", out _));
        return new TurnStats(
            done.GetProperty("promptTokens").GetInt32(),
            done.GetProperty("kvReusedTokens").GetInt32(),
            done.GetProperty("kvReusePercent").GetDouble(),
            done.GetProperty("elapsed").GetDouble(),
            done.GetProperty("tokenCount").GetInt32());
    }

    [LiveModelFact]
    public async Task AMultiTurnConversationReusesTheKeyValueCacheInsteadOfReprocessingIt()
    {
        // The property that decides whether a conversation on a phone is usable at
        // all. Every turn resends the whole history, so without cache reuse turn five
        // re-processes turns one to four from scratch, and the wait before the first
        // token grows with the conversation until the app is unusable. The engine
        // reports what it reused; this asserts the number is real.
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = session.GetProperty("sessionId").GetString()!;

        var history = new List<object>();
        var stats = new List<TurnStats>();
        string[] asks =
        [
            "Remember the number 41. Reply with just: ok",
            "Remember the colour amber. Reply with just: ok",
            "Remember the city Lisbon. Reply with just: ok",
            "List the three things I asked you to remember.",
        ];

        string lastAnswer = string.Empty;
        foreach (string ask in asks)
        {
            history.Add(new { role = "user", content = ask });
            // Enough room for the final turn to actually list what it remembered:
            // this fine-tune opens with a paragraph of reasoning, and a budget that
            // truncates before the answer would test its verbosity, not its memory.
            List<JsonElement> frames = await StreamAsync(new { sessionId, messages = history, maxTokens = 256, think = false });
            lastAnswer = TextOf(frames);
            history.Add(new { role = "assistant", content = lastAnswer });
            TurnStats turn = StatsOf(frames);
            stats.Add(turn);
            Console.WriteLine($"e2e turn {stats.Count}: {turn}");
        }

        // Turn one has nothing to reuse. Every turn after it must reuse essentially
        // the whole of the previous prompt: the history is a strict prefix extension,
        // so the only tokens needing work are the new message and the last answer.
        Assert.Equal(0, stats[0].ReusedTokens);
        for (int i = 1; i < stats.Count; i++)
        {
            Assert.True(stats[i].ReusedTokens >= stats[i - 1].PromptTokens,
                $"turn {i + 1} reused {stats[i].ReusedTokens} of a {stats[i].PromptTokens}-token prompt, "
                + $"but turn {i} alone had already processed {stats[i - 1].PromptTokens}");
            Assert.True(stats[i].ReusePercent > 80,
                $"turn {i + 1} reused only {stats[i].ReusePercent:0.0}% of its prompt");
        }

        // The prompt genuinely grew, so the reuse is not an artefact of a prompt that
        // never changed.
        Assert.True(stats[^1].PromptTokens > stats[0].PromptTokens,
            "the prompt did not grow across turns, so this proves nothing about reuse");

        // And the model actually used the history it kept: the last question asked it
        // to list all three, one from each earlier turn.
        int recalled = new[] { "41", "amber", "Lisbon" }
            .Count(item => lastAnswer.Contains(item, StringComparison.OrdinalIgnoreCase));
        Assert.True(recalled >= 2,
            $"the model recalled {recalled} of the 3 things across four turns; it answered: {lastAnswer}");
    }

    [LiveModelFact]
    public async Task AConversationSurvivesTheAppBeingKilledAndKeepsReusingTheCacheAfterwards()
    {
        // The phone's real multi-turn story. An app is suspended and killed
        // constantly, and when the user comes back the engine session is gone with
        // its KV cache. What must survive is the transcript: the resumed chat sends
        // the whole history, the first turn after the restart necessarily re-prefills,
        // and every turn after that reuses again.
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        AgentAppHost first = Start(model, weights);
        await LoadAsync(model);

        JsonElement opened = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string conversationId = opened.GetProperty("conversationId").GetString()!;

        var history = new List<object> { new { role = "user", content = "Remember the number 41. Reply with just: ok" } };
        List<JsonElement> frames = await StreamAsync(new
        {
            sessionId = opened.GetProperty("sessionId").GetString(),
            messages = history,
            maxTokens = 64,
            think = false,
        });
        history.Add(new { role = "assistant", content = TextOf(frames) });

        // Kill it. A new host over the same directories is what a relaunch is.
        AgentPaths paths = first.Paths;
        _client!.Dispose();
        first.Dispose();

        _host = new AgentAppHost(paths);
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri(_host.Server.BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        _client.DefaultRequestHeaders.Add("Cookie", $"{LoopbackServer.TokenCookie}={_host.Server.Token}");
        await LoadAsync(model);

        // Resuming must hand back the transcript, which is what the page replays into
        // the history it sends next.
        JsonElement resumed = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync($"/api/sessions?conversation={conversationId}", null)).Content.ReadAsStringAsync());
        string sessionId = resumed.GetProperty("sessionId").GetString()!;
        Assert.Equal(conversationId, resumed.GetProperty("conversationId").GetString());
        Assert.True(resumed.GetProperty("messages").GetArrayLength() >= 2,
            "the saved turn did not survive the restart");

        history.Add(new { role = "user", content = "What number did I give you?" });
        List<JsonElement> after = await StreamAsync(new { sessionId, messages = history, maxTokens = 128, think = false, newChat = true });
        TurnStats cold = StatsOf(after);
        Console.WriteLine($"e2e after restart: {cold}");

        // The model still knows, because the history came from disk rather than from
        // a cache that did not survive.
        Assert.Contains("41", TextOf(after), StringComparison.Ordinal);
        Assert.Equal(0, cold.ReusedTokens);

        history.Add(new { role = "assistant", content = TextOf(after) });
        history.Add(new { role = "user", content = "Say ok." });
        TurnStats warm = StatsOf(await StreamAsync(new { sessionId, messages = history, maxTokens = 32, think = false }));
        Console.WriteLine($"e2e after restart, next turn: {warm}");
        Assert.True(warm.ReusePercent > 80,
            $"reuse did not resume after the restart: {warm.ReusePercent:0.0}% of a {warm.PromptTokens}-token prompt");
    }

    [LiveModelFact]
    public async Task StartingAFreshChatDropsTheCacheRatherThanReusingAnotherConversation()
    {
        // The other half of the contract. The Web UI's New Chat sends newChat=true,
        // and if the engine went on reusing the previous conversation's cache the new
        // one would begin with the old one's context silently attached.
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = session.GetProperty("sessionId").GetString()!;

        var history = new List<object> { new { role = "user", content = "Remember the number 41. Reply with just: ok" } };
        List<JsonElement> first = await StreamAsync(new { sessionId, messages = history, maxTokens = 32, think = false });
        history.Add(new { role = "assistant", content = TextOf(first) });
        history.Add(new { role = "user", content = "Say ok again." });

        TurnStats reused = StatsOf(await StreamAsync(new { sessionId, messages = history, maxTokens = 32, think = false }));
        Assert.True(reused.ReusedTokens > 0, "the second turn of the same chat reused nothing");

        TurnStats fresh = StatsOf(await StreamAsync(new
        {
            sessionId,
            messages = new[] { new { role = "user", content = "Say ok." } },
            maxTokens = 32,
            think = false,
            newChat = true,
        }));
        Assert.Equal(0, fresh.ReusedTokens);
    }

    [LiveModelFact]
    public async Task RewritingAnEarlierTurnInvalidatesTheCacheFromThatPointOn()
    {
        // What the page's Revert does: it drops the last exchange and sends a
        // different one. The shared prefix is still good and must still be reused;
        // everything after the point where the two histories diverge must not be.
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = session.GetProperty("sessionId").GetString()!;

        var history = new List<object> { new { role = "user", content = "Remember the number 41. Reply with just: ok" } };
        List<JsonElement> first = await StreamAsync(new { sessionId, messages = history, maxTokens = 32, think = false });
        history.Add(new { role = "assistant", content = TextOf(first) });

        var continued = new List<object>(history) { new { role = "user", content = "What number did I give you?" } };
        TurnStats straight = StatsOf(await StreamAsync(new { sessionId, messages = continued, maxTokens = 64, think = false }));

        // Now rewrite the first question. Nothing after the system preamble is shared.
        var rewritten = new List<object> { new { role = "user", content = "Remember the number 77. Reply with just: ok" } };
        TurnStats diverged = StatsOf(await StreamAsync(new { sessionId, messages = rewritten, maxTokens = 32, think = false }));

        Console.WriteLine($"e2e straight: {straight}");
        Console.WriteLine($"e2e diverged: {diverged}");
        Assert.True(straight.ReusedTokens > diverged.ReusedTokens,
            "rewriting an earlier turn reused as much as continuing it, so the cache was not invalidated at the divergence");

        string answer = TextOf(await StreamAsync(new
        {
            sessionId,
            messages = new List<object>(rewritten) { new { role = "user", content = "What number did I give you?" } },
            maxTokens = 64,
            think = false,
        }));
        Assert.Contains("77", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("41", answer, StringComparison.Ordinal);
    }

    [LiveModelFact]
    public async Task GenerationIsFastEnoughToBeWorthUsing()
    {
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());

        var clock = Stopwatch.StartNew();
        List<JsonElement> frames = await StreamAsync(new
        {
            sessionId = session.GetProperty("sessionId").GetString(),
            messages = new[] { new { role = "user", content = "Count from one to twenty, in words, separated by commas." } },
            maxTokens = 128,
            think = false,
        });
        clock.Stop();

        int tokens = frames.Count(f => f.TryGetProperty("token", out _));
        double perSecond = tokens / clock.Elapsed.TotalSeconds;

        // The floor is deliberately low. This runs on whatever CPU the test machine
        // has, with no Metal, and the point is to catch a collapse — a backend that
        // fell back to a scalar path, a cache that is being rebuilt every token —
        // not to measure the device. The number is printed so a regression is visible
        // even when the assertion passes.
        Console.WriteLine($"e2e: {tokens} tokens in {clock.Elapsed.TotalSeconds:0.0}s = {perSecond:0.00} tok/s (CPU)");
        Assert.True(tokens > 0, "no tokens were produced");
        Assert.True(perSecond > 0.5, $"generation collapsed to {perSecond:0.00} tokens per second");

        // The exact members the page reads off the final frame to render its stats
        // line. Asserting them by name is the point: a rename here empties the line
        // in the UI and nothing else would notice.
        JsonElement done = frames.Last(f => f.TryGetProperty("done", out _));
        Assert.True(done.GetProperty("tokenCount").GetInt32() > 0);
        Assert.True(done.GetProperty("elapsed").GetDouble() > 0);
        Assert.True(done.GetProperty("tokPerSec").GetDouble() > 0);
        Assert.False(done.GetProperty("aborted").GetBoolean());
        Assert.True(done.GetProperty("promptTokens").GetInt32() > 0);
    }

    [LiveModelFact]
    public async Task ALongAnswerRunsToCompletionWithoutFaulting()
    {
        // The control for the abort test below: same prompt, same budget, nobody
        // cancels. If this crashes too then the fault is in generating that much,
        // not in stopping it.
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());

        List<JsonElement> frames = await StreamAsync(new
        {
            sessionId = session.GetProperty("sessionId").GetString(),
            messages = new[] { new { role = "user", content = "Write a very long essay about the sea." } },
            maxTokens = 2048,
            think = false,
        });

        JsonElement done = frames.Last(f => f.TryGetProperty("done", out _));
        Console.WriteLine($"e2e long: {done.GetProperty("tokenCount").GetInt32()} tokens, "
            + $"{done.GetProperty("tokPerSec").GetDouble():0.00} tok/s, truncated={done.GetProperty("truncated").GetBoolean()}");
        Assert.True(done.GetProperty("tokenCount").GetInt32() > 200, "the model stopped almost immediately");
    }

    [LiveModelFact]
    public async Task AStoppedGenerationEndsPromptlyAndSaysItWasAborted()
    {
        Assert.Null(Unavailable(out CatalogModel model, out string weights));
        Start(model, weights);
        await LoadAsync(model);

        JsonElement session = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var clock = Stopwatch.StartNew();
        try
        {
            await StreamAsync(new
            {
                sessionId = session.GetProperty("sessionId").GetString(),
                messages = new[] { new { role = "user", content = "Write a very long essay about the sea." } },
                maxTokens = 2048,
                think = false,
            }, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the page aborts a generation exactly this way.
        }
        clock.Stop();

        // A client that walks away must not leave the engine generating for minutes.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the aborted turn took {clock.Elapsed.TotalSeconds:0.0}s to unwind");
    }
}
