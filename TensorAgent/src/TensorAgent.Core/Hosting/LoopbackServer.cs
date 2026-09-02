// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TensorAgent.Core.Hosting;

/// <summary>Everything a route handler needs about one request.</summary>
public sealed class LoopbackRequest
{
    internal LoopbackRequest(HttpListenerContext context, string path, IReadOnlyDictionary<string, string> routeValues)
    {
        Context = context;
        Path = path;
        RouteValues = routeValues;
    }

    public HttpListenerContext Context { get; }
    public HttpListenerRequest Raw => Context.Request;
    public string Method => Context.Request.HttpMethod;
    /// <summary>Decoded absolute path without the query string.</summary>
    public string Path { get; }
    public IReadOnlyDictionary<string, string> RouteValues { get; }
    public string? Query(string name) => Context.Request.QueryString[name];
    public CancellationToken Aborted { get; internal set; }

    /// <summary>
    /// This request's own cancellation source. A streaming handler passes it to
    /// <see cref="LoopbackResponse.Sse"/> so the producer is stopped the moment a
    /// write shows the client has gone.
    /// </summary>
    public CancellationTokenSource? Cancellation { get; internal set; }

    public async Task<JsonElement> ReadJsonAsync(CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(Context.Request.InputStream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    public async Task<string> ReadTextAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(Context.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public bool HasFormContentType =>
        Context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;

    public Task<MultipartForm> ReadFormAsync(CancellationToken ct) =>
        MultipartFormReader.ReadAsync(Context.Request.InputStream, Context.Request.ContentType ?? string.Empty, ct);
}

/// <summary>What a handler returns. Static helpers mirror ASP.NET's Results.* so route code
/// reads the same as the Server's endpoints.</summary>
public abstract class LoopbackResponse
{
    public abstract Task WriteAsync(HttpListenerResponse response, CancellationToken ct);

    public static LoopbackResponse Json(object payload, int status = 200) => new JsonResponse(payload, status);
    public static LoopbackResponse Text(string text, int status = 200, string contentType = "text/plain; charset=utf-8") => new TextResponse(text, status, contentType);

    /// <summary>Exact bytes, for content that must not be re-encoded on the way out.</summary>
    public static LoopbackResponse Bytes(byte[] body, string contentType, int status = 200) => new BytesResponse(body, contentType, status);
    public static LoopbackResponse File(string path, string contentType, bool attachment = false, string? downloadName = null) => new FileResponse(path, contentType, attachment, downloadName);
    public static LoopbackResponse Status(int status) => new TextResponse(string.Empty, status, "text/plain");
    public static LoopbackResponse NotFound(object? payload = null) => payload is null ? Status(404) : Json(payload, 404);
    /// <summary>A Server-Sent-Events stream: each yielded object becomes one 'data: {json}' frame.</summary>
    /// <param name="frames">The frames to write, pulled one at a time.</param>
    /// <param name="clientGone">
    /// Cancelled when a write fails, so the producer stops as soon as nobody is
    /// listening. Pass the request's own source; null leaves a dropped client
    /// generating to the end of its budget.
    /// </param>
    public static LoopbackResponse Sse(IAsyncEnumerable<object> frames, CancellationTokenSource? clientGone = null)
        => new SseResponse(frames, clientGone);

    private sealed class JsonResponse(object payload, int status) : LoopbackResponse
    {
        public override async Task WriteAsync(HttpListenerResponse response, CancellationToken ct)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, SseFraming.JsonOptions);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    private sealed class TextResponse(string text, int status, string contentType) : LoopbackResponse
    {
        public override async Task WriteAsync(HttpListenerResponse response, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    private sealed class BytesResponse(byte[] body, string contentType, int status) : LoopbackResponse
    {
        public override async Task WriteAsync(HttpListenerResponse response, CancellationToken ct)
        {
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    private sealed class FileResponse(string path, string contentType, bool attachment, string? downloadName) : LoopbackResponse
    {
        public override async Task WriteAsync(HttpListenerResponse response, CancellationToken ct)
        {
            var info = new FileInfo(path);
            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = info.Length;
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Cache-Control"] = "no-cache";
            if (attachment)
                response.Headers["Content-Disposition"] = "attachment; filename=\"" + (downloadName ?? info.Name).Replace("\"", "") + "\"";
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await stream.CopyToAsync(response.OutputStream, ct).ConfigureAwait(false);
        }
    }

    private sealed class SseResponse(IAsyncEnumerable<object> frames, CancellationTokenSource? clientGone = null) : LoopbackResponse
    {
        public override async Task WriteAsync(HttpListenerResponse response, CancellationToken ct)
        {
            // The first frame is pulled BEFORE any header goes out, which is the only
            // window in which a status code is still available. The chat service
            // refuses a bad request from its first MoveNextAsync — no model loaded, an
            // unknown session — and that refusal has to reach the page as a 400 with a
            // JSON body, not as a 200 event stream that ends immediately. The desktop
            // adapter does exactly this; a stream that opened its headers first would
            // turn every rejection into a silent empty reply.
            await using IAsyncEnumerator<object> enumerator = frames.GetAsyncEnumerator(ct);
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                SseFraming.ApplyHeaders(response);
                response.SendChunked = true;
                return;
            }

            SseFraming.ApplyHeaders(response);
            response.SendChunked = true;
            do
            {
                try
                {
                    await SseFraming.WriteFrameAsync(response.OutputStream, enumerator.Current, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                    // The reader has gone: the page was closed, or the user pressed
                    // stop, which aborts the fetch. There is no disconnect event on
                    // HttpListener, so a failed write is how it is learned — and it
                    // has to be learned, because otherwise the model keeps generating
                    // into nothing, holding the session and draining the battery for
                    // however many tokens were left in the budget.
                    clientGone?.Cancel();
                    throw;
                }
            }
            while (await enumerator.MoveNextAsync().ConfigureAwait(false));
        }
    }
}

/// <summary>The exact wire format TensorSharp.Server's SseWriter produces: 'data: ' + JSON +
/// a blank line, flushed per frame, default serializer options (nulls emitted, property
/// names verbatim). The Web UI keys on those property names.</summary>
public static class SseFraming
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private static readonly byte[] Prefix = "data: "u8.ToArray();
    private static readonly byte[] Suffix = "\n\n"u8.ToArray();

    public static void ApplyHeaders(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteFrameAsync(Stream output, object payload, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await output.WriteAsync(Prefix, ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.WriteAsync(Suffix, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public static string Format(object payload) => "data: " + JsonSerializer.Serialize(payload, JsonOptions) + "\n\n";
}

/// <summary>A handler for one route. Return null to fall through to the next route.</summary>
public delegate Task<LoopbackResponse?> LoopbackHandler(LoopbackRequest request, CancellationToken ct);

/// <summary>
/// A tiny HTTP server on 127.0.0.1 for the WebView to talk to, built on the managed
/// <see cref="HttpListener"/> (iOS has no ASP.NET Core). It knows how to do exactly what
/// the Web UI needs: JSON in/out, multipart uploads, static files, SSE streams, and a
/// per-launch secret so another app on the device cannot drive the model through the
/// loopback port.
///
/// <para>
/// The secret travels as a cookie set by the first <c>GET /?token=…</c> the app itself
/// navigates the WebView to; every <c>/api</c> request without it is refused with 403.
/// Static files and <c>/uploads</c> are served with the same check, because the page's
/// image previews carry the cookie automatically.
/// </para>
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(string method, RoutePattern pattern, LoopbackHandler handler)> _routes = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _log;
    private Task? _loop;

    public const string TokenCookie = "tensoragent_token";

    public LoopbackServer(ILogger? log = null, int port = 0)
    {
        _log = log ?? NullLogger.Instance;
        Port = port == 0 ? FreePort() : port;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.IgnoreWriteExceptions = true;
    }

    public int Port { get; }
    public string Token { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    /// <summary>The URL the app points the WebView at: carries the token once.</summary>
    public string EntryUrl => $"{BaseUrl}/?token={Token}";
    public bool RequireToken { get; set; } = true;
    /// <summary>Directory served for GET requests that no route claims (the Web UI bundle).</summary>
    public string? StaticRoot { get; set; }
    /// <summary>Paths (prefixes) that are exempt from the token check, e.g. "/health".</summary>
    public HashSet<string> Public { get; } = new(StringComparer.Ordinal) { "/health" };

    public void Map(string method, string pattern, LoopbackHandler handler) =>
        _routes.Add((method.ToUpperInvariant(), new RoutePattern(pattern), handler));

    public void MapGet(string pattern, LoopbackHandler handler) => Map("GET", pattern, handler);
    public void MapPost(string pattern, LoopbackHandler handler) => Map("POST", pattern, handler);
    public void MapDelete(string pattern, LoopbackHandler handler) => Map("DELETE", pattern, handler);

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
        _log.LogInformation("TensorAgent loopback server listening on {Url}", BaseUrl);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "loopback accept failed");
                continue;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private int _inFlight;

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var response = ctx.Response;
        Interlocked.Increment(ref _inFlight);
        try
        {
            string path = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            string method = ctx.Request.HttpMethod.ToUpperInvariant();

            // Token handshake: the entry URL carries it once, after which the cookie does.
            string? queryToken = ctx.Request.QueryString["token"];
            bool authorised = !RequireToken || IsPublic(path)
                || string.Equals(queryToken, Token, StringComparison.Ordinal)
                || string.Equals(ctx.Request.Cookies[TokenCookie]?.Value, Token, StringComparison.Ordinal);
            if (queryToken is not null && string.Equals(queryToken, Token, StringComparison.Ordinal))
            {
                var cookie = new Cookie(TokenCookie, Token, "/") { HttpOnly = true };
                response.AppendCookie(cookie);
                // HttpListener emits 'Set-Cookie: name=value; Path=/' via AppendCookie; add SameSite explicitly.
                response.Headers.Add("Set-Cookie", $"{TokenCookie}={Token}; Path=/; HttpOnly; SameSite=Strict");
            }
            if (!authorised)
            {
                await LoopbackResponse.Json(new { error = "forbidden" }, 403).WriteAsync(response, _cts.Token).ConfigureAwait(false);
                return;
            }

            LoopbackResponse? result = null;
            foreach (var (m, pattern, handler) in _routes)
            {
                if (m != method || !pattern.TryMatch(path, out var values))
                    continue;
                // One source per request, linked to the server's. A handler that
                // streams hands this to LoopbackResponse.Sse so that a reader walking
                // away stops the work being done for it.
                using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var request = new LoopbackRequest(ctx, path, values) { Aborted = perRequest.Token, Cancellation = perRequest };
                result = await handler(request, perRequest.Token).ConfigureAwait(false);
                if (result is not null)
                {
                    await result.WriteAsync(response, perRequest.Token).ConfigureAwait(false);
                    return;
                }
            }

            result ??= TryStatic(method, path);
            result ??= LoopbackResponse.Json(new { error = "not found" }, 404);
            await result.WriteAsync(response, _cts.Token).ConfigureAwait(false);
        }
        catch (LoopbackHttpException ex)
        {
            try { await LoopbackResponse.Json(ex.Payload, ex.StatusCode).WriteAsync(response, _cts.Token).ConfigureAwait(false); }
            catch { /* client gone */ }
        }
        catch (OperationCanceledException)
        {
            // client disconnected or server stopping
        }
        catch (HttpListenerException)
        {
            // client disconnected mid-write (an aborted SSE stream)
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "loopback request {Method} {Path} failed", ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath);
            try
            {
                if (!response.SendChunked && response.ContentLength64 == 0)
                    await LoopbackResponse.Json(new { error = "The server failed to handle the request." }, 500).WriteAsync(response, _cts.Token).ConfigureAwait(false);
            }
            catch { /* headers already sent */ }
        }
        finally
        {
            try { response.Close(); } catch { }
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private bool IsPublic(string path)
    {
        foreach (string p in Public)
            if (path.Equals(p, StringComparison.Ordinal) || path.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal))
                return true;
        return false;
    }

    private LoopbackResponse? TryStatic(string method, string path)
    {
        if (method != "GET" || StaticRoot is null)
            return null;
        string relative = path == "/" ? "index.html" : path.TrimStart('/');
        if (relative.Contains("..", StringComparison.Ordinal))
            return null;
        string full = Path.GetFullPath(Path.Combine(StaticRoot, relative));
        if (!full.StartsWith(Path.GetFullPath(StaticRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        // The app's own companion script, served from this assembly rather than from
        // the bundle so it can never drift from the code that expects it.
        if (relative == CompanionScriptName)
            return LoopbackResponse.Text(CompanionScript.Value, contentType: "text/javascript; charset=utf-8");

        if (!File.Exists(full))
            return null;

        // index.html is TensorSharp.Server's, byte for byte, and must stay that way:
        // forking it would mean every future change to the Web UI had to be made
        // twice. Everything the app needs on top of it — resuming a saved
        // conversation, native attachments, dictated text, the copy that only makes
        // sense on a server — is added by appending one script tag on the way out.
        if (relative.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            return LoopbackResponse.Bytes(WithCompanionScript(full), "text/html; charset=utf-8");

        return LoopbackResponse.File(full, ContentTypes.For(full));
    }

    private const string CompanionScriptName = "tensoragent.js";

    private static readonly Lazy<string> CompanionScript = new(() =>
    {
        using Stream? stream = typeof(LoopbackServer).Assembly
            .GetManifestResourceStream("TensorAgent.Core.WebUi.tensoragent.js");
        if (stream is null)
        {
            // A build that lost the resource would produce a page with no session
            // resume and no attachments, and nothing would say why. Fail loudly.
            throw new InvalidOperationException(
                "TensorAgent.Core.WebUi.tensoragent.js is not embedded in the assembly; "
                + "check the EmbeddedResource item in TensorAgent.Core.csproj.");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// The page's own bytes with one script tag spliced in before <c>&lt;/body&gt;</c>.
    ///
    /// <para>
    /// Bytes, not text. Reading the file into a string and writing it back re-encodes
    /// it — a byte-order mark is dropped, and any encoding the file uses is
    /// normalised — so the page the WebView receives would no longer be the Server's
    /// file. It has to be, because that identity is the reason there is no second
    /// copy of index.html to keep in step.
    /// </para>
    /// </summary>
    private static byte[] WithCompanionScript(string indexPath)
    {
        byte[] html = File.ReadAllBytes(indexPath);
        byte[] tag = Encoding.UTF8.GetBytes("\n<script src=\"/" + CompanionScriptName + "\"></script>\n");
        ReadOnlySpan<byte> close = "</body>"u8;

        int at = html.AsSpan().LastIndexOf(close);
        if (at < 0)
            at = html.Length;

        byte[] page = new byte[html.Length + tag.Length];
        html.AsSpan(0, at).CopyTo(page);
        tag.CopyTo(page, at);
        html.AsSpan(at).CopyTo(page.AsSpan(at + tag.Length));
        return page;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Stop accepting, cancel what is running, and wait for it to actually stop.
    ///
    /// <para>
    /// The wait is the part that matters. A request in flight is very often inside
    /// the inference engine, and the engine's weights are freed by whatever disposes
    /// the host next. Returning from here while a generation is still running hands
    /// that code a window in which it frees memory the native compute threads are
    /// still reading, and the process dies with a segmentation fault somewhere
    /// unrelated-looking. Cancellation is delivered between tokens, so this is a
    /// short wait in practice; the cap is there so a wedged request cannot stop the
    /// app from closing.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }

        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) > 0 && deadline.Elapsed < DrainTimeout)
            Thread.Sleep(20);
        if (Volatile.Read(ref _inFlight) > 0)
            _log.LogWarning("loopback shut down with {Count} request(s) still running", Volatile.Read(ref _inFlight));
    }

    /// <summary>How long <see cref="Dispose"/> waits for running requests before giving up on them.</summary>
    public static TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>A handler may throw this to answer with a status + JSON payload (the same
/// shape the Server's ApiExceptionMiddleware produces).</summary>
public sealed class LoopbackHttpException(int statusCode, object payload) : Exception($"HTTP {statusCode}")
{
    public int StatusCode { get; } = statusCode;
    public object Payload { get; } = payload;
}

/// <summary>Path templates with {name} and {*rest} segments, like Minimal APIs.</summary>
public sealed class RoutePattern
{
    private readonly string[] _segments;
    private readonly bool _catchAll;

    public RoutePattern(string pattern)
    {
        _segments = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        _catchAll = _segments.Length > 0 && _segments[^1].StartsWith("{*", StringComparison.Ordinal);
    }

    public bool TryMatch(string path, out IReadOnlyDictionary<string, string> values)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        values = dict;
        string[] parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (_segments.Length == 0)
            return parts.Length == 0;
        for (int i = 0; i < _segments.Length; i++)
        {
            string seg = _segments[i];
            if (_catchAll && i == _segments.Length - 1)
            {
                if (i >= parts.Length)
                    return false;
                dict[seg[2..^1]] = string.Join('/', parts.Skip(i));
                return true;
            }
            if (i >= parts.Length)
                return false;
            if (seg.StartsWith('{') && seg.EndsWith('}'))
                dict[seg[1..^1]] = parts[i];
            else if (!string.Equals(seg, parts[i], StringComparison.Ordinal))
                return false;
        }
        return parts.Length == _segments.Length;
    }
}

/// <summary>Content types for the Web UI bundle and served uploads; unknown extensions are
/// never served as HTML (the same rule the Server's upload policy applies).</summary>
public static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8", [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8", [".csv"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon",
        [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".m4v"] = "video/x-m4v", [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg", [".flac"] = "audio/flac", [".caf"] = "audio/x-caf",
        [".pdf"] = "application/pdf", [".woff"] = "font/woff", [".woff2"] = "font/woff2",
    };

    public static string For(string path) =>
        Map.TryGetValue(Path.GetExtension(path), out string? ct) ? ct : "application/octet-stream";
}
