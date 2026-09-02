// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text;
using System.Text.Json;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Sessions;
using TensorAgent.Core.Settings;
using TensorSharp.Chat;

namespace TensorAgent.Core.Hosting;

/// <summary>
/// Every route the Web UI calls, bound to the same services the desktop server
/// binds them to.
///
/// <para>
/// This is the whole reason the chat pipeline was pulled out of the ASP.NET host:
/// the page in the WebView is <c>TensorSharp.Server/wwwroot/index.html</c> byte for
/// byte, so the API underneath it has to answer the same paths, with the same
/// payload shapes and the same server-sent-event frames, or the page silently
/// misbehaves in ways no compiler catches. What changes here is only the plumbing —
/// <see cref="LoopbackServer"/> instead of minimal APIs, because iOS has no ASP.NET
/// Core runtime pack — and never the contract.
/// </para>
/// <para>
/// The routes the app adds on top all live under <c>/api/agent</c>, so the shared
/// surface stays exactly the shared surface and there is no chance of a name
/// colliding with something the desktop adds later.
/// </para>
/// </summary>
public static class WebUiRoutes
{
    /// <summary>
    /// Bind the shared Web UI API. <paramref name="chat"/> is required; the rest are
    /// optional and their routes answer 503 with a reason when absent, which is what
    /// lets the app start before a model has been chosen.
    /// </summary>
    public static void MapWebUi(
        this LoopbackServer server,
        WebUiChatService chat,
        SkillsService? skills = null,
        ConversationRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(chat);

        // ---- chat ---------------------------------------------------------------
        server.MapGet("/api/queue/status", (_, _) => Ok(chat.GetQueueStatus()));
        server.MapGet("/api/models", (_, _) => Ok(chat.GetModels()));
        server.MapPost("/api/models/load", async (request, ct) =>
        {
            JsonElement body = await request.ReadJsonAsync(ct);
            return Json(await Guarded(() => chat.LoadModelAsync(body, ct)));
        });
        server.MapPost("/api/chat", async (request, ct) =>
            LoopbackResponse.Sse(
                Recording(Guarded(chat.ChatStreamAsync(await request.ReadJsonAsync(ct), ct)), recorder),
                request.Cancellation));

        // ---- sessions -----------------------------------------------------------
        //
        // The desktop's route creates an engine session and says so. The app's page
        // also asks, in the query string, which saved conversation that session is
        // for, and needs the answer back so it can render the transcript it is
        // resuming. The extra member is additive: the desktop page ignores it.
        server.MapPost("/api/sessions", (request, _) =>
        {
            object created = chat.CreateSession();
            if (recorder is null)
                return Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(created));

            string sessionId = SessionIdOf(created);
            Conversation conversation = recorder.Bind(sessionId, request.Query("conversation"));
            return Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new
            {
                sessionId,
                conversationId = conversation.Id,
                title = conversation.Title,
                messages = conversation.Messages,
                think = conversation.Think,
                skills = conversation.Skills,
                modelId = conversation.ModelId,
            }));
        });
        server.MapDelete("/api/sessions/{id}", async (request, ct) =>
        {
            string id = request.RouteValues["id"];
            object disposed = await Guarded(() => chat.DisposeSessionAsync(id, ct));
            recorder?.Release(id);
            return Json(disposed);
        });

        // ---- uploads and generation --------------------------------------------
        server.MapPost("/api/upload", async (request, ct) =>
        {
            using MultipartForm form = await request.ReadFormAsync(ct);
            MultipartFile? file = form.Files.FirstOrDefault();
            if (file is null)
                return LoopbackResponse.Json(new { error = "no file was uploaded" }, 400);
            await using FileStream content = File.OpenRead(file.TempPath);
            return Json(await Guarded(() => chat.UploadAsync(content, file.FileName, file.Length, ct)));
        });
        server.MapPost("/api/image-edit", async (request, ct) =>
        {
            JsonElement body = await request.ReadJsonAsync(ct);
            return Json(await Guarded(() => chat.ImageEditAsync(body, ct)));
        });
        server.MapPost("/api/image-edit/stream", async (request, ct) =>
            LoopbackResponse.Sse(Guarded(chat.ImageEditStreamAsync(await request.ReadJsonAsync(ct), ct)), request.Cancellation));
        server.MapPost("/api/video-generate", async (request, ct) =>
        {
            JsonElement body = await request.ReadJsonAsync(ct);
            return Json(await Guarded(() => chat.VideoGenerateAsync(body, ct)));
        });
        server.MapPost("/api/video-generate/stream", async (request, ct) =>
            LoopbackResponse.Sse(Guarded(chat.VideoGenerateStreamAsync(await request.ReadJsonAsync(ct), ct)), request.Cancellation));

        // ---- skills -------------------------------------------------------------
        if (skills is null)
        {
            server.MapGet("/api/skills", (_, _) => Ok(new { skills = Array.Empty<object>(), canInstall = false }));
            return;
        }

        server.MapGet("/api/skills", (_, _) => OkGuarded(() => skills.ListForUi()));
        server.MapGet("/api/skills/{name}", (request, _) => OkGuarded(() => skills.GetForUi(request.RouteValues["name"])));
        server.MapGet("/api/skills/{name}/files/{*path}", (request, _) =>
            skills.TryGetFile(request.RouteValues["name"], request.RouteValues["path"], out string text, out object error)
                ? Task.FromResult<LoopbackResponse?>(LoopbackResponse.Text(text, contentType: "text/markdown; charset=utf-8"))
                : Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(error, 404)));
        server.MapPost("/api/skills/rescan", (_, _) => OkGuarded(() => skills.Rescan()));
        server.MapDelete("/api/skills/{name}", (request, _) => OkGuarded(() => skills.Remove(request.RouteValues["name"])));
        server.MapPost("/api/skills", async (request, ct) =>
        {
            EnsureGuarded(skills.EnsureInstallable);
            using MultipartForm form = await request.ReadFormAsync(ct);
            MultipartFile? file = form.Files.FirstOrDefault();
            if (file is null)
                return LoopbackResponse.Json(new { error = "no skill archive was uploaded" }, 400);
            await using FileStream zip = File.OpenRead(file.TempPath);
            bool overwrite = string.Equals(form["overwrite"], "true", StringComparison.OrdinalIgnoreCase);
            return Json(GuardedValue(() => skills.Install(zip, file.FileName, file.Length, overwrite)));
        });
    }

    /// <summary>
    /// The routes that exist only in the app: the model catalog and its downloads,
    /// the saved conversations, and the sandbox switches.
    ///
    /// <para>
    /// The desktop server has no equivalent because its models are files an operator
    /// put on disk and its chat history lives in the page. On a phone both have to be
    /// managed by the app, so these are additions rather than replacements, and they
    /// sit under their own prefix to keep that obvious.
    /// </para>
    /// </summary>
    public static void MapAgent(
        this LoopbackServer server,
        IReadOnlyList<CatalogModel> catalog,
        ModelStore models,
        ConversationStore conversations,
        SettingsStore settings,
        Func<string>? describeEngine = null,
        Action<string, JsonElement>? onPageEvent = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(models);
        CatalogModel? Find(string id) => catalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(settings);

        server.MapGet("/api/agent/catalog", (_, _) => Ok(new
        {
            models = catalog.Select(m => Describe(m, models)).ToArray(),
        }));

        server.MapGet("/api/agent/catalog/{id}", (request, _) =>
        {
            CatalogModel? model = Find(request.RouteValues["id"]);
            return model is null
                ? Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new { error = "no such model" }, 404))
                : Ok(Describe(model, models));
        });

        // Downloads are long and resumable, so this is a stream rather than a request
        // that blocks for gigabytes: the page shows progress from these frames and a
        // dropped connection leaves the .part file to be resumed, not restarted.
        server.MapPost("/api/agent/catalog/{id}/download", (request, ct) =>
        {
            CatalogModel? model = Find(request.RouteValues["id"]);
            if (model is null)
                return Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new { error = "no such model" }, 404));
            // The optional files are the multimodal projector and the step-distilled
            // LoRA: worth their bytes on a phone, but the user pays for them, so the
            // setting decides rather than the catalog.
            IReadOnlyCollection<CatalogFileRole>? optional = settings.Load().DownloadOptionalFiles
                ? new[] { CatalogFileRole.Projector, CatalogFileRole.Lora, CatalogFileRole.TextEncoder, CatalogFileRole.Vae }
                : null;
            return Task.FromResult<LoopbackResponse?>(LoopbackResponse.Sse(DownloadFrames(models, model, optional, ct), request.Cancellation));
        });

        server.MapDelete("/api/agent/catalog/{id}", (request, _) =>
        {
            CatalogModel? model = Find(request.RouteValues["id"]);
            if (model is null)
                return Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new { error = "no such model" }, 404));
            models.Delete(model);
            return Ok(Describe(model, models));
        });

        server.MapGet("/api/agent/conversations", (_, _) => Ok(new { conversations = conversations.List() }));
        server.MapPost("/api/agent/conversations", (_, _) => Ok(conversations.Create()));
        server.MapGet("/api/agent/conversations/{id}", (request, _) =>
        {
            Conversation? conversation = conversations.Load(request.RouteValues["id"]);
            return conversation is null
                ? Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new { error = "no such conversation" }, 404))
                : Ok(conversation);
        });
        server.MapPost("/api/agent/conversations/{id}", async (request, ct) =>
        {
            Conversation? conversation = conversations.Load(request.RouteValues["id"]);
            if (conversation is null)
                return LoopbackResponse.Json(new { error = "no such conversation" }, 404);
            JsonElement body = await request.ReadJsonAsync(ct);
            if (body.TryGetProperty("title", out JsonElement title) && title.GetString() is { Length: > 0 } text)
                conversations.Rename(conversation.Id, text);
            return LoopbackResponse.Json(conversations.Load(conversation.Id)!);
        });
        server.MapDelete("/api/agent/conversations/{id}", (request, _) =>
            Ok(new { deleted = conversations.Delete(request.RouteValues["id"]) }));

        server.MapGet("/api/agent/settings", (_, _) => Ok(settings.Load()));
        server.MapPost("/api/agent/settings", async (request, ct) =>
        {
            AppSettings updated = JsonSerializer.Deserialize<AppSettings>(
                (await request.ReadJsonAsync(ct)).GetRawText(), SseFraming.JsonOptions) ?? new AppSettings();
            settings.Save(updated);
            return LoopbackResponse.Json(settings.Load());
        });

        // The page tells the app what it just did — which conversation it bound, when
        // it finished loading, when a generation started or stopped — so the native
        // chrome around the WebView can follow along. A WebView message handler would
        // be the platform way; this is the same thing over the transport that already
        // exists, which keeps the injected script free of any iOS-specific API.
        server.MapPost("/api/agent/events", async (request, ct) =>
        {
            JsonElement message = await request.ReadJsonAsync(ct);
            string kind = message.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? string.Empty : string.Empty;
            onPageEvent?.Invoke(kind, message);
            return LoopbackResponse.Json(new { ok = true });
        });

        server.MapGet("/api/agent/engine", (_, _) => Ok(new
        {
            engine = describeEngine?.Invoke() ?? "unknown",
            modelRoot = models.Root,
            conversationRoot = conversations.Root,
        }));
    }

    private static async IAsyncEnumerable<object> DownloadFrames(
        ModelStore store, CatalogModel model, IReadOnlyCollection<CatalogFileRole>? optionalRoles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var frames = System.Threading.Channels.Channel.CreateUnbounded<object>();

        // The store reports progress on whatever thread the download is reading on,
        // and the response has to be written from this one, so the two are joined by
        // a channel rather than by a lock.
        var progress = new ChannelProgress(frames.Writer);
        Task download = Task.Run(async () =>
        {
            try
            {
                await store.DownloadAsync(model, progress, ct, optionalRoles).ConfigureAwait(false);
                frames.Writer.TryWrite(new { done = true, id = model.Id });
            }
            catch (OperationCanceledException)
            {
                frames.Writer.TryWrite(new { cancelled = true, id = model.Id });
            }
            catch (Exception ex)
            {
                frames.Writer.TryWrite(new { error = ex.Message, id = model.Id });
            }
            finally
            {
                frames.Writer.TryComplete();
            }
        }, ct);

        await foreach (object frame in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return frame;
        await download.ConfigureAwait(false);
    }

    private sealed class ChannelProgress(System.Threading.Channels.ChannelWriter<object> writer)
        : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => writer.TryWrite(new
        {
            file = value.FileName,
            fileIndex = value.FileIndex,
            fileCount = value.FileCount,
            phase = value.Phase,
            received = value.BytesReceived,
            total = value.TotalBytes,
            fraction = value.Fraction,
            bytesPerSecond = value.BytesPerSecond,
            etaSeconds = value.Eta?.TotalSeconds,
        });
    }

    private static object Describe(CatalogModel model, ModelStore store) => new
    {
        id = model.Id,
        name = model.DisplayName,
        family = model.Family.ToString(),
        parameters = model.Parameters,
        quantization = model.Quantization,
        notes = model.Notes,
        license = model.License,
        contextLength = model.ContextLength,
        modalities = model.Modalities.ToString(),
        kind = model.Kind.ToString(),
        experimental = model.Experimental,
        minDeviceMemoryGB = model.MinDeviceMemoryGB,
        totalBytes = model.TotalBytes,
        state = store.StateOf(model).ToString(),
        installedBytes = store.InstalledBytes(model),
        remainingBytes = store.RemainingBytes(model),
        path = store.WeightsPath(model),
    };

    /// <summary>
    /// Pass the turn's frames through, and write the answer down when it ends.
    ///
    /// <para>
    /// The frames are the only place the assistant's reply exists on this side: the
    /// service streams it and the page assembles it. Reassembling it here, from the
    /// same <c>token</c>, <c>replace</c> and <c>thinking</c> events the page reads,
    /// is what lets a chat survive the app being killed the moment after an answer
    /// appears. An aborted turn is still saved, because the partial answer is what
    /// the user is looking at.
    /// </para>
    /// </summary>
    private static async IAsyncEnumerable<object> Recording(IAsyncEnumerable<object> frames, ConversationRecorder? recorder)
    {
        if (recorder is null)
        {
            await foreach (object frame in frames.ConfigureAwait(false))
                yield return frame;
            yield break;
        }

        var content = new StringBuilder();
        var thinking = new StringBuilder();
        string? sessionId = null;

        await foreach (object frame in frames.ConfigureAwait(false))
        {
            yield return frame;

            // The frames are anonymous objects, so they are read the way the page
            // reads them: as JSON. Serialising each one costs a little, and it is the
            // only way to stay honest about what was actually sent.
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(frame, SseFraming.JsonOptions));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("token", out JsonElement token) && token.GetString() is { } piece)
                content.Append(piece);
            else if (root.TryGetProperty("replace", out JsonElement replace) && replace.GetString() is { } whole)
                content.Clear().Append(whole);
            else if (root.TryGetProperty("thinking", out JsonElement thought) && thought.GetString() is { } reasoning)
                thinking.Append(reasoning);
            if (root.TryGetProperty("sessionId", out JsonElement id) && id.GetString() is { Length: > 0 } value)
                sessionId = value;
        }

        if (sessionId is not null)
            recorder.Complete(sessionId, content.ToString(), thinking.ToString());
    }

    /// <summary>
    /// Read the session id out of whatever shape the chat service returned. It is an
    /// anonymous type, so a round trip through JSON is the only way to it — cheap, and
    /// it fails loudly if the service ever renames the member.
    /// </summary>
    private static string SessionIdOf(object created)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(created, SseFraming.JsonOptions));
        return document.RootElement.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("the chat service created a session with no id");
    }

    /// <summary>
    /// The chat service refuses a request by throwing, carrying the status and the JSON
    /// body it wants sent. The loopback transport has its own exception for exactly
    /// that, so this is the one place the two vocabularies meet — and it is a
    /// translation, never a reinterpretation: the status and the payload are passed
    /// through untouched so the page sees what the desktop's page sees.
    /// </summary>
    private static async Task<object> Guarded(Func<Task<object>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (WebUiRequestRejectedException ex) { throw new LoopbackHttpException(ex.StatusCode, ex.Payload); }
    }

    private static async IAsyncEnumerable<object> Guarded(IAsyncEnumerable<object> frames)
    {
        await using IAsyncEnumerator<object> enumerator = frames.GetAsyncEnumerator();
        while (true)
        {
            object current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    yield break;
                current = enumerator.Current;
            }
            catch (WebUiRequestRejectedException ex)
            {
                throw new LoopbackHttpException(ex.StatusCode, ex.Payload);
            }
            yield return current;
        }
    }

    private static object GuardedValue(Func<object> call)
    {
        try { return call(); }
        catch (WebUiRequestRejectedException ex) { throw new LoopbackHttpException(ex.StatusCode, ex.Payload); }
    }

    private static void EnsureGuarded(Action call)
    {
        try { call(); }
        catch (WebUiRequestRejectedException ex) { throw new LoopbackHttpException(ex.StatusCode, ex.Payload); }
    }

    private static Task<LoopbackResponse?> OkGuarded(Func<object> call) =>
        Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(GuardedValue(call)));

    private static Task<LoopbackResponse?> Ok(object payload) =>
        Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(payload));

    private static LoopbackResponse? Json(object payload) => LoopbackResponse.Json(payload);
}
