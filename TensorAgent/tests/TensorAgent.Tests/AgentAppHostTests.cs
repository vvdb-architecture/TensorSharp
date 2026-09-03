using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Sandbox;
using TensorAgent.Core.Settings;
using TensorSharp.AgentHost.CodeExec;
using TensorSharp.Server;

namespace TensorAgent.Tests;

/// <summary>
/// The whole app, assembled and driven over HTTP.
///
/// <para>
/// Every other test in this project checks one part. This one checks that the parts
/// were connected: that the settings the user sees actually reach the sandbox, that
/// a session created by the page is filed under a conversation that survives a
/// restart, and that the code runner underneath the chat tool is the in-process one
/// rather than a process launcher that would not exist on a phone.
/// </para>
/// </summary>
public sealed class AgentAppHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-host-" + Guid.NewGuid().ToString("N"));
    private AgentAppHost? _host;
    private HttpClient? _client;

    private AgentPaths Paths => new(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

    private AgentAppHost Start(Action<SettingsStore>? configure = null)
    {
        if (configure is not null)
        {
            AgentPaths paths = Paths;
            paths.EnsureCreated();
            configure(new SettingsStore(paths.SettingsFile));
        }

        _host = new AgentAppHost(Paths);
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri(_host.Server.BaseUrl) };
        _client.DefaultRequestHeaders.Add("Cookie", $"{Core.Hosting.LoopbackServer.TokenCookie}={_host.Server.Token}");
        return _host;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<JsonElement> Get(string path)
        => JsonSerializer.Deserialize<JsonElement>(await _client!.GetStringAsync(path));

    [Fact]
    public void ItCreatesEverythingItNeedsAndSeparatesBackedUpDataFromRefetchableFiles()
    {
        AgentAppHost host = Start();
        Assert.True(Directory.Exists(host.Paths.ConversationsDirectory));
        Assert.True(Directory.Exists(host.Paths.ModelsDirectory));
        Assert.True(Directory.Exists(host.Paths.ScratchDirectory));

        // Weights can always be downloaded again; a transcript cannot. They must not
        // share a root, or the backup either carries gigabytes or loses the chats.
        Assert.StartsWith(host.Paths.CacheRoot, host.Paths.ModelsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(host.Paths.DataRoot, host.Paths.ConversationsDirectory, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Paths.DataRoot, host.Paths.ModelsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeRunnerRunsInProcessBecauseNothingCanBeSpawned()
    {
        AgentAppHost host = Start();
        Assert.NotNull(host.CodeRunner);
        Assert.True(host.CodeRunner!.CanRun);
        Assert.Equal("in-process", host.Backend.Name);
        Assert.Equal("sh", host.Backend.Shell!.Name);
    }

    [Fact]
    public void TurningCodeExecutionOffLeavesNoRunnerAtAll()
    {
        AgentAppHost host = Start(settings =>
        {
            AppSettings off = settings.Load();
            off.AllowCodeExecution = false;
            settings.Save(off);
        });

        Assert.Null(host.CodeRunner);
        Assert.False(host.CodeExec.Enabled);
        Assert.Contains("code execution off", host.DescribeEngine(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNetworkSwitchReachesTheSandboxAndNotJustTheSettingsFile()
    {
        AgentAppHost allowed = Start(settings =>
        {
            AppSettings on = settings.Load();
            on.AllowNetwork = true;
            settings.Save(on);
        });

        Assert.True(allowed.CodeExec.AllowNetwork);
        Assert.Contains("network on", allowed.DescribeEngine(), StringComparison.Ordinal);
    }

    [Fact]
    public void ByDefaultTheNetworkIsOffAndACommandThatReachesForItIsRefused()
    {
        AgentAppHost host = Start();
        Assert.False(host.CodeExec.AllowNetwork);

        string work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        ConfinedResult result = ((IShellBackend)host.Backend).Run(new ShellLaunch
        {
            Argv = new[] { "sh", "-c", "curl https://example.com" },
            WorkingDirectory = work,
            WriteDirectory = work,
            ReadOnlyDirectory = work,
            Timeout = TimeSpan.FromSeconds(10),
        });
        Assert.False(result.Ok);
        Assert.Contains("network", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheEngineDescriptionReachesThePageAndNamesWhatIsMissing()
    {
        Start();
        JsonElement body = await Get("/api/agent/engine");
        string engine = body.GetProperty("engine").GetString()!;
        Assert.Contains("sh (in-process)", engine, StringComparison.Ordinal);
        Assert.Contains("no python", engine, StringComparison.Ordinal);
        Assert.Contains("network off", engine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingASessionMintsAConversationAndSaysWhichOne()
    {
        AgentAppHost host = Start();
        JsonElement created = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());

        string sessionId = created.GetProperty("sessionId").GetString()!;
        string conversationId = created.GetProperty("conversationId").GetString()!;
        Assert.NotEmpty(sessionId);
        Assert.NotEmpty(conversationId);
        Assert.Equal(conversationId, host.Recorder.ConversationFor(sessionId));
        Assert.NotNull(host.Conversations.Load(conversationId));
    }

    [Fact]
    public async Task ResumingASessionHandsBackTheSavedTranscript()
    {
        AgentAppHost host = Start();
        Conversation saved = host.Conversations.Create();
        saved.Messages.Add(new StoredMessage { Role = "user", Content = "what is a tensor" });
        saved.Messages.Add(new StoredMessage { Role = "assistant", Content = "an array with a shape" });
        host.Conversations.Save(saved);

        JsonElement resumed = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync($"/api/sessions?conversation={saved.Id}", null)).Content.ReadAsStringAsync());

        Assert.Equal(saved.Id, resumed.GetProperty("conversationId").GetString());
        JsonElement messages = resumed.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("what is a tensor", messages[0].GetProperty("content").GetString());
        Assert.Equal("what is a tensor", resumed.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ATurnIsWrittenToDiskSoTheChatSurvivesTheAppBeingKilled()
    {
        AgentAppHost host = Start();
        JsonElement created = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = created.GetProperty("sessionId").GetString()!;
        string conversationId = created.GetProperty("conversationId").GetString()!;

        // The recorder is what /api/chat calls once a request is accepted. Driving it
        // directly is deliberate: no model is loaded here, so a real chat request would
        // be refused before it ever reached this hook.
        host.Recorder.Record(sessionId, JsonSerializer.Deserialize<JsonElement>("""
            {
              "model": "gemma-4-e2b-q8",
              "think": true,
              "skills": ["pdf"],
              "messages": [
                { "role": "user", "content": "summarise this", "textFileNames": ["report.pdf"] },
                { "role": "assistant", "content": "here is the summary", "thinking": "reading it" }
              ]
            }
            """));

        // Read it back through a second store over the same directory, which is what a
        // relaunch does.
        var reopened = new ConversationStore(host.Paths.ConversationsDirectory);
        Conversation? conversation = reopened.Load(conversationId);
        Assert.NotNull(conversation);
        Assert.Equal(2, conversation!.Messages.Count);
        Assert.Equal("summarise this", conversation.Messages[0].Content);
        Assert.Equal(new List<string> { "report.pdf" }, conversation.Messages[0].TextFileNames);
        Assert.Equal("reading it", conversation.Messages[1].Thinking);
        Assert.Equal("gemma-4-e2b-q8", conversation.ModelId);
        Assert.True(conversation.Think);
        Assert.Equal(new List<string> { "pdf" }, conversation.Skills);
        Assert.Equal("summarise this", conversation.Title);
    }

    [Fact]
    public async Task TheAnswerIsSavedWhenTheTurnEndsAndNotOnlyWhenTheNextOneStarts()
    {
        // The gap that loses exactly the message the user is reading. Recording the
        // request saves the history the page sent, which does not yet contain the
        // reply being generated; if the app is killed after the answer appears and
        // before another question is asked, that answer is gone.
        AgentAppHost host = Start();
        JsonElement created = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = created.GetProperty("sessionId").GetString()!;
        string conversationId = created.GetProperty("conversationId").GetString()!;

        host.Recorder.Record(sessionId, JsonSerializer.Deserialize<JsonElement>(
            """{ "messages": [ { "role": "user", "content": "what is 2 + 2" } ] }"""));
        host.Recorder.Complete(sessionId, "It is 4.", "adding them");

        Conversation saved = new ConversationStore(host.Paths.ConversationsDirectory).Load(conversationId)!;
        Assert.Equal(2, saved.Messages.Count);
        Assert.Equal("assistant", saved.Messages[1].Role);
        Assert.Equal("It is 4.", saved.Messages[1].Content);
        Assert.Equal("adding them", saved.Messages[1].Thinking);
    }

    [Fact]
    public async Task RegeneratingAnAnswerReplacesItRatherThanAppendingASecond()
    {
        AgentAppHost host = Start();
        JsonElement created = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = created.GetProperty("sessionId").GetString()!;
        string conversationId = created.GetProperty("conversationId").GetString()!;

        host.Recorder.Record(sessionId, JsonSerializer.Deserialize<JsonElement>(
            """{ "messages": [ { "role": "user", "content": "hello" } ] }"""));
        host.Recorder.Complete(sessionId, "first attempt");
        host.Recorder.Complete(sessionId, "second attempt");

        Conversation saved = new ConversationStore(host.Paths.ConversationsDirectory).Load(conversationId)!;
        Assert.Equal(2, saved.Messages.Count);
        Assert.Equal("second attempt", saved.Messages[1].Content);
    }

    [Fact]
    public async Task AnAnswerForAnUnboundSessionIsIgnoredRatherThanMisfiled()
    {
        AgentAppHost host = Start();
        Conversation other = host.Conversations.Create();
        host.Recorder.Complete("a-session-nobody-bound", "this belongs to nothing");
        Assert.Empty(host.Conversations.Load(other.Id)!.Messages);
    }

    [Fact]
    public async Task OpeningTheAppRepeatedlyWithoutTypingLeavesOneEmptyChatRatherThanMany()
    {
        // The page creates a session on every load and a session binds a
        // conversation, so twenty launches used to leave twenty "Chat Sep 2, 07:38"
        // rows pushing the real conversations off the screen. An empty conversation
        // is not a chat the user had; it is one they were about to have.
        AgentAppHost host = Start();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 5; i++)
        {
            JsonElement created = JsonSerializer.Deserialize<JsonElement>(
                await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
            ids.Add(created.GetProperty("conversationId").GetString()!);
        }

        Assert.Single(ids);
        Assert.Single(host.Conversations.List(includeEmpty: true));
        // And an untouched one is not offered as a saved chat at all.
        Assert.Empty(host.Conversations.List());
    }

    [Fact]
    public async Task OnceAChatHasAMessageTheNextLaunchStartsAFreshOne()
    {
        AgentAppHost host = Start();
        JsonElement first = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string used = first.GetProperty("conversationId").GetString()!;
        host.Recorder.Record(first.GetProperty("sessionId").GetString()!, JsonSerializer.Deserialize<JsonElement>(
            """{ "messages": [ { "role": "user", "content": "hello" } ] }"""));

        JsonElement second = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        Assert.NotEqual(used, second.GetProperty("conversationId").GetString());

        // The one with a message is listed; the fresh empty one is not.
        IReadOnlyList<ConversationSummary> listed = host.Conversations.List();
        Assert.Single(listed);
        Assert.Equal(used, listed[0].Id);
    }

    [Fact]
    public async Task DisposingASessionUnbindsItWithoutDeletingTheConversation()
    {
        AgentAppHost host = Start();
        JsonElement created = JsonSerializer.Deserialize<JsonElement>(
            await (await _client!.PostAsync("/api/sessions?conversation=new", null)).Content.ReadAsStringAsync());
        string sessionId = created.GetProperty("sessionId").GetString()!;
        string conversationId = created.GetProperty("conversationId").GetString()!;

        await _client.DeleteAsync($"/api/sessions/{sessionId}");

        Assert.Null(host.Recorder.ConversationFor(sessionId));
        Assert.NotNull(host.Conversations.Load(conversationId));
    }

    [Fact]
    public async Task TheCatalogOfferedIsTheOneThisDeviceCanActuallyRun()
    {
        Start();
        JsonElement body = await Get("/api/agent/catalog");
        foreach (JsonElement model in body.GetProperty("models").EnumerateArray())
            Assert.True(model.GetProperty("minDeviceMemoryGB").GetInt32() <= 12,
                $"{model.GetProperty("id").GetString()} needs more memory than the device tier allows");
    }

    [Fact]
    public async Task TheModelsRouteAdvertisesMetalFirstBecauseThatIsWhyItIsOnAPhone()
    {
        Start();
        JsonElement body = await Get("/api/models");
        Assert.Equal("ggml_metal", body.GetProperty("defaultBackend").GetString());
        JsonElement backends = body.GetProperty("supportedBackends");
        Assert.Equal("ggml_metal", backends[0].GetProperty("Value").GetString());
        Assert.Contains(backends.EnumerateArray(), b => b.GetProperty("Value").GetString() == "ggml_cpu");
    }

    [Fact]
    public async Task ChangingASettingThroughTheApiIsWhatTheNextLaunchReads()
    {
        AgentAppHost host = Start();
        await _client!.PostAsJsonAsync("/api/agent/settings", new
        {
            allowCodeExecution = true,
            allowNetwork = true,
            maxTokens = 1024,
        });

        Assert.True(new SettingsStore(host.Paths.SettingsFile).Load().AllowNetwork);

        // A restart is what applies it: the running host built its options at startup.
        host.Dispose();
        using var restarted = new AgentAppHost(Paths);
        Assert.True(restarted.CodeExec.AllowNetwork);
        Assert.Equal(1024, restarted.Options.DefaultMaxTokens);
        _host = null;
    }

    [Fact]
    public async Task ShuttingDownStopsServingBeforeItReleasesTheEngine()
    {
        // The order is the whole test. A request in flight is usually inside the
        // model, so releasing the engine first unmaps weights that native compute
        // threads are still reading and the process dies with a segmentation fault in
        // an unrelated-looking kernel. This is what that cost, found by an end-to-end
        // test that stopped a generation partway.
        AgentAppHost host = Start();
        var entered = new TaskCompletionSource();
        var finished = new TaskCompletionSource();

        // A handler that does NOT stop when asked, which is what a native compute
        // already inside a graph looks like: cancellation is delivered between
        // tokens, and until the next one it keeps reading the weights.
        host.Server.MapGet("/api/agent/slow", async (_, _) =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            finished.TrySetResult();
            return LoopbackResponse.Json(new { ok = true });
        });

        Task pending = _client!.GetAsync("/api/agent/slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();
        clock.Stop();
        _host = null;

        Assert.True(finished.Task.IsCompleted, "shutdown returned while a request was still running");
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(1),
            $"shutdown returned in {clock.Elapsed.TotalMilliseconds:0}ms without waiting for the request");
        try { await pending; } catch { /* the connection closes with the server */ }
    }

    [Fact]
    public void AnAllowListInSettingsReachesThePolicyEveryRuntimeChecks()
    {
        // Three runtimes check ExecutionPolicy.NetworkHosts, and until this was wired
        // nothing in the app could ever set it: the list was always empty and
        // IsHostAllowed short-circuited to true, so the check read as enforcement and
        // enforced nothing.
        AgentAppHost host = Start(settings =>
        {
            AppSettings s = settings.Load();
            s.AllowNetwork = true;
            s.NetworkHosts = new List<string> { "pypi.org" };
            settings.Save(s);
        });

        string work = Path.Combine(_root, "hosts");
        Directory.CreateDirectory(work);
        ConfinedResult refused = ((IShellBackend)host.Backend).Run(new ShellLaunch
        {
            Argv = new[] { "sh", "-c", "curl https://example.com" },
            WorkingDirectory = work,
            WriteDirectory = work,
            ReadOnlyDirectory = work,
            AllowNetwork = true,
            Timeout = TimeSpan.FromSeconds(15),
        });

        Assert.False(refused.Ok);
        Assert.Contains(ExecutionPolicy.HostNotAllowedSuffix, refused.Stderr, StringComparison.Ordinal);
        // And it must say so rather than reporting the network as off, which is the
        // advice that sends someone to a switch that is already on.
        Assert.DoesNotContain(ExecutionPolicy.NetworkDisabledMessage, refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiIsClosedToAnythingWithoutTheLaunchToken()
    {
        AgentAppHost host = Start();
        using var bare = new HttpClient { BaseAddress = new Uri(host.Server.BaseUrl) };
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.GetAsync("/api/agent/settings")).StatusCode);
    }
    [Fact]
    public async Task ShuttingDownWaitsForTheEngineItselfAndNotOnlyForTheRequests()
    {
        // Draining the requests is not the same as draining the engine, which is what
        // the neighbouring test covers and where the segmentation fault came back from.
        // Cancellation reaches a generation between tokens, so the HTTP request can be
        // finished while the engine's own threads are still inside a graph compute, and
        // releasing the model at that moment unmaps the weights a ggml kernel is
        // reading. The engine's live counters exist only once a model is loaded, which
        // is not something a unit test can have: what the shutdown asks is substituted
        // here, what it does with the answer is the real thing.
        using var idle = new ManualResetEventSlim(initialState: false);
        // Both are completed from inside the shutdown itself, so their continuations
        // have to go elsewhere: run inline, the rest of this test would execute on the
        // thread that is trying to shut the host down.
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = new AgentAppHost(Paths, modelService: new ReleaseRecordingModelService(() => released.TrySetResult()));
        _host = host;
        host.EngineHasWorkInFlight = () =>
        {
            polled.TrySetResult();
            return !idle.IsSet;
        };
        host.Start();

        try
        {
            Task shutdown = Task.Run(host.Dispose);

            Task asked = await Task.WhenAny(polled.Task, shutdown);
            Assert.True(ReferenceEquals(asked, polled.Task),
                "the shutdown finished without ever asking the engine whether it was still working");

            // Long enough that a shutdown which is not waiting has finished by now.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(released.Task.IsCompleted, "the weights were freed while the engine was still computing");
            Assert.False(shutdown.IsCompleted, "the shutdown returned while the engine was still computing");

            idle.Set();
            Task finished = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(ReferenceEquals(finished, shutdown), "the engine went idle and the shutdown never returned");
            await shutdown;
            _host = null;

            Assert.True(released.Task.IsCompleted, "the engine went idle and the model was never released");
        }
        finally
        {
            // However this ends, the stand-in stops claiming work, so tearing the
            // fixture down cannot sit in the drain for the full timeout.
            idle.Set();
        }
    }

    private sealed class ReleaseRecordingModelService(Action onRelease) : ModelService, IDisposable
    {
        void IDisposable.Dispose()
        {
            onRelease();
            base.Dispose();
        }
    }

    /// <summary>
    /// Choosing a model repoints the engine, so the choice is real before the method
    /// returns.
    ///
    /// <para>
    /// The engine allows one hosted model per process because the desktop server is
    /// launched against one --model, and the app inherited that: picking a model in
    /// TensorAgent's own list saved a setting and nothing else, so the Models page said
    /// "selected" while /api/chat kept answering "No model is configured" until the app
    /// was restarted. On a phone that is indistinguishable from a broken button, and it
    /// is what a user reported. This pins the half that is testable without weights:
    /// after UseModel, the guard the chat route consults resolves the NEW file, and
    /// before it, it does not.
    /// </para>
    /// </summary>
    [Fact]
    public void ChoosingAModelRepointsTheHostedModelRatherThanWaitingForARestart()
    {
        AgentAppHost host = Start();

        // The guard the chat route consults. Before anything is chosen it holds the
        // placeholder path, so a request naming a real model is refused.
        const string wanted = "gemma-4-E2B-it-Q8_0.gguf";
        Assert.False(
            TensorSharp.Server.Hosting.HostedModelGuard.TryResolveHostedModelRequest(
                wanted, host.Options.StartupModelPath, out _, out string before),
            "nothing has been chosen yet, so this model must not resolve");
        Assert.Contains("not hosted", before, StringComparison.OrdinalIgnoreCase);

        // UseModel loads weights, which a unit test has none of; repointing is the part
        // that decides whether the choice is visible, and it is what is checked here.
        CatalogModel model = ModelCatalog.BuiltIn.Single(m => m.Id == "gemma-4-e2b-q8");
        AppSettings settings = host.Settings.Load();
        settings.SelectedModelId = model.Id;
        host.Settings.Save(settings);
        host.Options.RepointHostedModel(
            host.Paths.SelectedModelPath(settings), host.Paths.SelectedProjectorPath(settings));

        Assert.True(
            TensorSharp.Server.Hosting.HostedModelGuard.TryResolveHostedModelRequest(
                wanted, host.Options.StartupModelPath, out string resolved, out string after),
            $"after choosing {model.Id} the chat route must accept it: {after}");
        Assert.EndsWith(wanted, resolved, StringComparison.Ordinal);
    }

}
