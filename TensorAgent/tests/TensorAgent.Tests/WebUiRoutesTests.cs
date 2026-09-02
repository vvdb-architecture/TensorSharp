using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Settings;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Chat;
using TensorSharp.Server.Hosting;
using TensorSharp.Server;

namespace TensorAgent.Tests;

/// <summary>
/// The API the WebView talks to, exercised over real HTTP against the real chat
/// service.
///
/// <para>
/// The page inside the app is TensorSharp.Server's index.html unchanged, so what
/// matters is not that these handlers do something reasonable but that they answer
/// the same paths with the same shapes as the desktop server. A test that called the
/// handlers directly would miss the parts that actually break: status codes, the
/// event-stream framing, and the token gate.
/// </para>
/// </summary>
public sealed class WebUiRoutesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-api-" + Guid.NewGuid().ToString("N"));
    private readonly LoopbackServer _server;
    private readonly HttpClient _client;
    private readonly WebUiChatService _chat;
    private readonly ConversationStore _conversations;
    private readonly SettingsStore _settings;
    private readonly ModelStore _models;

    public WebUiRoutesTests()
    {
        Directory.CreateDirectory(_root);
        var options = new ServerHostingOptions(
            startupModelPath: Path.Combine(_root, "models", "none.gguf"),
            startupMmProjPath: null,
            defaultBackend: "ggml_cpu",
            supportedBackends: new[] { new BackendOption("ggml_cpu", "GGML CPU") },
            defaultMaxTokens: 256,
            maxTokensPinned: false,
            defaultVideoFrames: 0, defaultVideoFps: 0, defaultVideoWidth: 0,
            defaultVideoHeight: 0, defaultVideoSteps: 0, defaultVideoMode: null,
            uploadDirectory: _root,
            logDirectory: Path.Combine(_root, "logs"),
            fileLoggingEnabled: false,
            samplingDefaults: null);

        _chat = new WebUiChatService(
            new ModelService(), new SessionManager(), options,
            new UploadStoragePolicy(_root), new SkillRegistry(new SkillRegistryOptions()),
            codeRunner: null, workspaces: null, codeArtifacts: null,
            NullLoggerFactory.Instance);

        _conversations = new ConversationStore(Path.Combine(_root, "chats"));
        _settings = new SettingsStore(Path.Combine(_root, "settings.json"));
        _models = new ModelStore(Path.Combine(_root, "weights"));

        _server = new LoopbackServer(NullLogger.Instance);
        _server.MapWebUi(_chat);
        _server.MapAgent(ModelCatalog.BuiltIn, _models, _conversations, _settings, () => "test engine");
        _server.Start();

        _client = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
        _client.DefaultRequestHeaders.Add("Cookie", $"{LoopbackServer.TokenCookie}={_server.Token}");
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    // ---- the token gate -------------------------------------------------------------

    [Fact]
    public async Task WithoutTheLaunchTokenTheApiIsClosed()
    {
        using var bare = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
        HttpResponseMessage response = await bare.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheEntryUrlHandsOutTheCookieSoTheWebViewNeedsNothingElse()
    {
        Assert.Contains($"?token={_server.Token}", _server.EntryUrl, StringComparison.Ordinal);
        using var bare = new HttpClient { BaseAddress = new Uri(_server.BaseUrl) };
        HttpResponseMessage response = await bare.GetAsync($"/api/models?token={_server.Token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), v => v.Contains(_server.Token, StringComparison.Ordinal));
    }

    // ---- the shared Web UI surface --------------------------------------------------

    [Fact]
    public async Task TheModelsRouteAnswersTheShapeThePageReads()
    {
        JsonElement body = await BodyOf(await _client.GetAsync("/api/models"));
        // The exact member names the page reads; renaming one of these silently
        // empties a control in the UI, which is why they are asserted by name.
        Assert.True(body.TryGetProperty("models", out _));
        Assert.True(body.TryGetProperty("mmProjModels", out _));
        Assert.True(body.TryGetProperty("supportedBackends", out JsonElement backends));
        Assert.Equal("ggml_cpu", backends[0].GetProperty("Value").GetString());
        Assert.True(body.TryGetProperty("loaded", out _));
        Assert.True(body.TryGetProperty("defaultBackend", out _));
        Assert.True(body.TryGetProperty("defaultMaxTokens", out _));
    }

    [Fact]
    public async Task TheQueueStatusRouteAnswers()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/queue/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ASessionCanBeCreatedAndDisposedThroughTheSameRoutesTheDesktopUses()
    {
        JsonElement created = await BodyOf(await _client.PostAsync("/api/sessions", null));
        string id = created.GetProperty("sessionId").GetString()!;
        Assert.NotEmpty(id);

        HttpResponseMessage disposed = await _client.DeleteAsync($"/api/sessions/{id}");
        Assert.Equal(HttpStatusCode.OK, disposed.StatusCode);
    }

    [Fact]
    public async Task DisposingASessionThatDoesNotExistIsA404AndNotAServerError()
    {
        HttpResponseMessage response = await _client.DeleteAsync("/api/sessions/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AChatRequestWithNoModelLoadedIsRefusedWithAStatusNotAnEmptyStream()
    {
        // This is the regression that matters most: an event stream whose headers went
        // out before the first frame turns every refusal into a 200 with no content,
        // and the page shows nothing at all rather than the reason.
        HttpResponseMessage response = await _client.PostAsync("/api/chat",
            new StringContent("""{"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("event-stream", response.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.Ordinal);
        JsonElement body = await BodyOf(response);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task WithNoSkillRegistryTheSkillsRouteStillAnswersThePage()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/skills");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await BodyOf(response);
        Assert.True(body.TryGetProperty("skills", out _));
    }

    // ---- the app's own surface ------------------------------------------------------

    [Fact]
    public async Task TheCatalogListsTheBuiltInModelsWithTheirInstallState()
    {
        JsonElement body = await BodyOf(await _client.GetAsync("/api/agent/catalog"));
        JsonElement models = body.GetProperty("models");
        Assert.True(models.GetArrayLength() >= 6, "the catalog carries Gemma 4 and Qwen dense + MoE entries");

        JsonElement first = models[0];
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("id").GetString()));
        Assert.Equal("NotInstalled", first.GetProperty("state").GetString());
        Assert.True(first.GetProperty("totalBytes").GetInt64() > 0);
        Assert.True(first.GetProperty("remainingBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task TheCatalogCoversBothFamiliesAndBothArchitectures()
    {
        JsonElement body = await BodyOf(await _client.GetAsync("/api/agent/catalog"));
        var families = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement model in body.GetProperty("models").EnumerateArray())
        {
            families.Add(model.GetProperty("family").GetString()!);
            kinds.Add(model.GetProperty("kind").GetString()!);
        }
        Assert.Contains("Gemma4", families);
        Assert.Contains("Qwen38", families);
        Assert.Contains("QwenImage", families);
        Assert.Contains("Dense", kinds);
        Assert.Contains("MixtureOfExperts", kinds);
        Assert.Contains("Diffusion", kinds);
    }

    [Fact]
    public async Task AnUnknownCatalogEntryIs404()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/agent/catalog/not-a-model");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConversationsAreCreatedListedRenamedAndDeleted()
    {
        JsonElement created = await BodyOf(await _client.PostAsync("/api/agent/conversations", null));
        string id = created.GetProperty("id").GetString()!;

        JsonElement listed = await BodyOf(await _client.GetAsync("/api/agent/conversations"));
        Assert.Contains(listed.GetProperty("conversations").EnumerateArray(),
            c => c.GetProperty("id").GetString() == id);

        JsonElement renamed = await BodyOf(await _client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}", new { title = "Trip planning" }));
        Assert.Equal("Trip planning", renamed.GetProperty("title").GetString());

        JsonElement loaded = await BodyOf(await _client.GetAsync($"/api/agent/conversations/{id}"));
        Assert.Equal("Trip planning", loaded.GetProperty("title").GetString());

        JsonElement deleted = await BodyOf(await _client.DeleteAsync($"/api/agent/conversations/{id}"));
        Assert.True(deleted.GetProperty("deleted").GetBoolean());

        HttpResponseMessage gone = await _client.GetAsync($"/api/agent/conversations/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task TheSandboxSwitchesRoundTripAndTheNetworkStaysOffUnlessAsked()
    {
        JsonElement initial = await BodyOf(await _client.GetAsync("/api/agent/settings"));
        Assert.True(initial.GetProperty("allowCodeExecution").GetBoolean());
        Assert.False(initial.GetProperty("allowNetwork").GetBoolean());

        JsonElement saved = await BodyOf(await _client.PostAsJsonAsync("/api/agent/settings", new
        {
            allowCodeExecution = false,
            allowNetwork = true,
            confirmBeforeRunning = true,
            maxTokens = 4096,
        }));
        Assert.False(saved.GetProperty("allowCodeExecution").GetBoolean());
        Assert.True(saved.GetProperty("allowNetwork").GetBoolean());
        Assert.Equal(4096, saved.GetProperty("maxTokens").GetInt32());

        // The store, not just the response, has to have changed: this is what the
        // sandbox reads on the next command.
        Assert.True(_settings.Load().AllowNetwork);
        Assert.False(_settings.Load().AllowCodeExecution);
    }

    [Fact]
    public async Task TheEngineRouteSaysWhatIsActuallyRunning()
    {
        JsonElement body = await BodyOf(await _client.GetAsync("/api/agent/engine"));
        Assert.Equal("test engine", body.GetProperty("engine").GetString());
        Assert.Equal(_models.Root, body.GetProperty("modelRoot").GetString());
    }

    // ---- the page itself -------------------------------------------------------------

    [Fact]
    public async Task TheServersOwnPageIsServedUnchangedApartFromOneAppendedScriptTag()
    {
        string root = Path.Combine(_root, "webui");
        Directory.CreateDirectory(root);
        const string page = "<html><head><title>TensorSharp</title></head><body><div id=\"chat\"></div></body></html>";
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), page);
        _server.StaticRoot = root;

        string served = await _client!.GetStringAsync("/");

        // Everything the desktop page contains is still there, in order.
        Assert.Contains("<div id=\"chat\"></div>", served, StringComparison.Ordinal);
        Assert.Contains("<title>TensorSharp</title>", served, StringComparison.Ordinal);
        // And exactly one thing was added, before the closing tag so the page's own
        // top-level bindings already exist when it runs.
        Assert.Contains("<script src=\"/tensoragent.js\"></script>", served, StringComparison.Ordinal);
        Assert.True(served.IndexOf("tensoragent.js", StringComparison.Ordinal) < served.IndexOf("</body>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCompanionScriptIsServedFromTheAssemblyAndCarriesTheAppsAdditions()
    {
        _server.StaticRoot = Path.Combine(_root, "webui");
        Directory.CreateDirectory(_server.StaticRoot);

        string script = await _client!.GetStringAsync("/tensoragent.js");

        Assert.Contains("window.TensorAgent", script, StringComparison.Ordinal);
        Assert.Contains("addAttachment", script, StringComparison.Ordinal);
        Assert.Contains("insertText", script, StringComparison.Ordinal);
        Assert.Contains("/api/sessions?conversation=", script, StringComparison.Ordinal);
        // The routes it calls must be the ones this server actually maps.
        Assert.Contains("/api/agent/conversations/", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/tensoragent/", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownRouteIs404WithJsonRatherThanAnEmptyBody()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement body = await BodyOf(response);
        Assert.True(body.TryGetProperty("error", out _));
    }
}
