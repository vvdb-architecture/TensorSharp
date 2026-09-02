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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TensorAgent.Maui.Hosting;

/// <summary>
/// In-process HTTP server on 127.0.0.1 that serves TensorSharp.Server's Web UI to
/// the app's WKWebView and answers the UI's <c>/api</c> calls.
/// </summary>
/// <remarks>
/// <para>ASP.NET Core has no iOS runtime pack, so this is System.Net.HttpListener
/// (its managed socket implementation ships in the iOS Mono runtime pack). The Web
/// UI itself is untouched: index.html expects real HTTP plus SSE-over-fetch, and
/// this host speaks exactly that so the file can stay byte-identical with the
/// desktop server's.</para>
/// <para>The port is loopback-only, but every app on the device can reach
/// loopback, so requests are gated by a per-launch secret: the WebView's first
/// navigation is <c>/?token=…</c>, which sets an HttpOnly cookie, and every
/// <c>/api</c> request must present that cookie (or the header/query form used by
/// the simulator E2E harness) or is refused with 403.</para>
/// <para>In this spike the API is a stub: <c>/api/chat</c> streams a canned reply
/// so the whole UI round trip renders with no model. The routes are the ones the
/// Web UI calls at load and per turn (see TensorSharp.Server/Endpoints).</para>
/// </remarks>
public sealed class LoopbackWebHost : IDisposable
{
    private const string TokenCookieName = "tensoragent_token";
    private const string TokenHeaderName = "X-TensorAgent-Token";
    private const string TokenQueryName = "token";
    private const int DemoTokenDelayMs = 35;

    /// <summary>What <c>/api/models</c> reports as loaded while the API is the demo stub.</summary>
    public const string DemoModelName = "demo (no model loaded)";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    // Same table the desktop server gets from ASP.NET's static file middleware
    // for the handful of types wwwroot actually contains.
    private static readonly Dictionary<string, string> s_contentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
    };

    private readonly string _webRoot;
    private readonly Func<EngineProbeResult> _engineProbe;
    private readonly Action<string> _log;
    private readonly byte[] _tokenBytes;
    private HttpListener? _listener;
    private CancellationTokenSource? _shutdown;

    /// <param name="webRoot">Directory holding index.html and the static assets (the bundled copy of wwwroot).</param>
    /// <param name="engineProbe">Producer for <c>GET /api/engine</c>; invoked per request so it reports the live state.</param>
    /// <param name="log">Sink for one-line request/lifecycle logs (stdout is what <c>simctl launch --console</c> shows).</param>
    public LoopbackWebHost(string webRoot, Func<EngineProbeResult> engineProbe, Action<string>? log = null)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _engineProbe = engineProbe;
        _log = log ?? Console.WriteLine;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _tokenBytes = Encoding.ASCII.GetBytes(Token);
    }

    /// <summary>The per-launch secret every <c>/api</c> request must carry.</summary>
    public string Token { get; }

    /// <summary>Loopback port the listener is bound to; 0 until <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary><c>http://127.0.0.1:{port}/</c></summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>The URL the WebView navigates to first: index.html plus the cookie-setting token.</summary>
    public string EntryUrl => $"{BaseUrl}?{TokenQueryName}={Token}";

    /// <summary>Where the Web UI is served from (for the startup log and the status bar).</summary>
    public string WebRoot => _webRoot;

    /// <summary>
    /// Bind a free loopback port and start accepting. Throws when the bundled
    /// Web UI is missing rather than serving an empty page: a bundle without
    /// webui/index.html is a build problem that must be visible immediately.
    /// </summary>
    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("LoopbackWebHost is already started.");
        }

        string index = Path.Combine(_webRoot, "index.html");
        if (!File.Exists(index))
        {
            throw new FileNotFoundException(
                $"The bundled Web UI is missing: expected {index}. The csproj links TensorSharp.Server/wwwroot/** into the bundle as webui/.",
                index);
        }

        _listener = BindFreePort();
        _shutdown = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_listener, _shutdown.Token));
        _log($"TensorAgent: Web UI listening on {BaseUrl} (webroot {_webRoot}, index.html {new FileInfo(index).Length} bytes)");
    }

    public void Dispose()
    {
        _shutdown?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _listener = null;
    }

    // Binding an explicit random port and retrying on collision avoids the
    // classic "find a free port with a probe socket, then bind it" race.
    private HttpListener BindFreePort()
    {
        HttpListenerException? last = null;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            int port = RandomNumberGenerator.GetInt32(20000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                Port = port;
                return listener;
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                listener.Close();
            }
        }

        throw new InvalidOperationException("Could not bind a loopback port for the Web UI after 32 attempts.", last);
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (shutdown.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"TensorAgent: accept failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // One task per request so a streaming /api/chat never blocks the 1 s
            // /api/queue/status poll the UI runs alongside it.
            _ = Task.Run(() => HandleSafelyAsync(context, shutdown), CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken shutdown)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        string path = request.Url?.AbsolutePath ?? "/";
        try
        {
            await HandleAsync(request, response, path, shutdown).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The client went away mid-response (the Web UI aborts /api/chat by
            // closing the stream); nothing to report.
        }
        catch (Exception ex)
        {
            _log($"TensorAgent: {request.HttpMethod} {path} failed: {ex}");
            try
            {
                await WriteJsonAsync(response, 500, new { error = ex.Message }, shutdown).ConfigureAwait(false);
            }
            catch
            {
                // The response may already be partially written; there is nothing
                // more useful to do than close it below.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Already closed by the client.
            }
        }

        // The UI polls /api/queue/status every second; logging that would bury
        // everything else (the desktop server keeps the same low-noise list).
        if (path != "/api/queue/status")
        {
            _log($"TensorAgent: {request.HttpMethod} {path} -> {response.StatusCode}");
        }
    }

    private async Task HandleAsync(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken shutdown)
    {
        string method = request.HttpMethod;

        if (path == "/" && method == "GET")
        {
            // First navigation from the WebView: exchange the query token for the
            // cookie every fetch() in index.html will then send automatically.
            string? presented = request.QueryString[TokenQueryName];
            if (presented is not null && TokenMatches(presented))
            {
                response.AppendHeader("Set-Cookie", $"{TokenCookieName}={Token}; Path=/; HttpOnly; SameSite=Strict");
            }

            await ServeFileAsync(response, Path.Combine(_webRoot, "index.html"), shutdown).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!IsAuthorized(request))
            {
                await WriteJsonAsync(response, 403, new { error = "Missing or invalid TensorAgent session token." }, shutdown).ConfigureAwait(false);
                return;
            }

            await HandleApiAsync(request, response, method, path, shutdown).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/uploads/", StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, 404, new { error = "Uploads are not served by the TensorAgent spike." }, shutdown).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && TryResolveStaticFile(path, out string file))
        {
            await ServeFileAsync(response, file, shutdown).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(response, 404, new { error = $"{method} {path} is not served by the TensorAgent spike." }, shutdown).ConfigureAwait(false);
    }

    private async Task HandleApiAsync(HttpListenerRequest request, HttpListenerResponse response, string method, string path, CancellationToken shutdown)
    {
        switch (method, path)
        {
            case ("GET", "/api/engine"):
                EngineProbeResult probe = _engineProbe();
                await WriteJsonAsync(response, 200, new
                {
                    backend = probe.Backend,
                    ggmlAvailable = new { cpu = probe.GgmlCpuAvailable, metal = probe.GgmlMetalAvailable },
                    mainProgramHandleResolved = probe.MainProgramHandleResolved,
                    ggmlVersionOrError = probe.GgmlVersionOrError,
                    engineAssemblies = probe.EngineAssemblies,
                    gpuName = probe.GpuName,
                    reason = probe.Reason,
                    webRoot = _webRoot,
                }, shutdown).ConfigureAwait(false);
                return;

            case ("GET", "/api/models"):
                // Shape of WebUiAdapter.GetModels. `loaded` is a demo placeholder
                // rather than null on purpose: index.html refuses to send anything
                // while `loaded` is null (sendMessage, "No model is configured"),
                // and the point of this spike is to watch the UI run a full
                // round trip against the canned /api/chat stream below. The demo
                // "architecture" keeps the UI on the chat route (only qwen_image
                // and the video families are routed elsewhere).
                await WriteJsonAsync(response, 200, new
                {
                    loaded = DemoModelName,
                    models = Array.Empty<string>(),
                    architecture = "demo",
                    loadedBackend = _engineProbe().Backend,
                    defaultMaxTokens = 4096,
                    skills = (object?)null,
                }, shutdown).ConfigureAwait(false);
                return;

            case ("GET", "/api/queue/status"):
                await WriteJsonAsync(response, 200, new
                {
                    busy = false,
                    processing = 0,
                    pending_requests = 0,
                    total_processed = 0,
                }, shutdown).ConfigureAwait(false);
                return;

            case ("POST", "/api/sessions"):
                await WriteJsonAsync(response, 200, new { sessionId = Guid.NewGuid().ToString("N") }, shutdown).ConfigureAwait(false);
                return;

            case ("DELETE", _) when path.StartsWith("/api/sessions/", StringComparison.Ordinal):
                await WriteJsonAsync(response, 200, new { ok = true }, shutdown).ConfigureAwait(false);
                return;

            case ("POST", "/api/chat"):
                await StreamDemoChatAsync(request, response, shutdown).ConfigureAwait(false);
                return;

            default:
                await WriteJsonAsync(response, 404, new { error = $"{method} {path} is not implemented in the TensorAgent spike." }, shutdown).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Demo round trip: stream a canned sentence as <c>token</c> frames and finish
    /// with a <c>done</c> frame, in the exact wire format SseWriter uses on the
    /// desktop server (<c>data: {json}\n\n</c>, flushed per frame) so index.html's
    /// parser runs unchanged.
    /// </summary>
    private async Task StreamDemoChatAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken shutdown)
    {
        string? sessionId = null;
        string userText = string.Empty;
        using (JsonDocument? body = await ReadJsonBodyAsync(request, shutdown).ConfigureAwait(false))
        {
            if (body is not null && body.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (body.RootElement.TryGetProperty("sessionId", out JsonElement sid) && sid.ValueKind == JsonValueKind.String)
                {
                    sessionId = sid.GetString();
                }

                if (body.RootElement.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement message in messages.EnumerateArray())
                    {
                        if (message.TryGetProperty("role", out JsonElement role) && role.ValueEquals("user") &&
                            message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                        {
                            userText = content.GetString() ?? string.Empty;
                        }
                    }
                }
            }
        }

        EngineProbeResult probe = _engineProbe();
        string reply =
            "Hello from TensorAgent. This reply is streamed by the in-process loopback server; " +
            "no model is loaded yet, so it is a canned demo. " +
            $"The engine backend selected on this device is {probe.Backend} " +
            $"and the native GgmlOps link is {(probe.MainProgramHandleResolved ? "working" : "NOT resolved")}. " +
            (userText.Length > 0 ? $"You wrote: \"{userText}\"" : "You sent an empty message.");
        string[] words = reply.Split(' ');

        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        Stream output = response.OutputStream;

        var stopwatch = Stopwatch.StartNew();
        await WriteSseFrameAsync(output, new { thinking = "Demo mode: no model is loaded, so this answer is canned." }, shutdown).ConfigureAwait(false);
        for (int i = 0; i < words.Length; i++)
        {
            string token = i + 1 < words.Length ? words[i] + " " : words[i];
            await WriteSseFrameAsync(output, new { token }, shutdown).ConfigureAwait(false);
            await Task.Delay(DemoTokenDelayMs, shutdown).ConfigureAwait(false);
        }

        double elapsed = stopwatch.Elapsed.TotalSeconds;
        await WriteSseFrameAsync(output, new
        {
            done = true,
            sessionId,
            tokenCount = words.Length,
            elapsed,
            tokPerSec = elapsed > 0 ? words.Length / elapsed : 0,
            promptTokens = 0,
        }, shutdown).ConfigureAwait(false);
    }

    private static async Task WriteSseFrameAsync(Stream output, object payload, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(payload, s_json);
        byte[] bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> ReadJsonBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        try
        {
            return await JsonDocument.ParseAsync(request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        string? presented =
            request.Cookies[TokenCookieName]?.Value ??
            request.Headers[TokenHeaderName] ??
            request.QueryString[TokenQueryName];
        return presented is not null && TokenMatches(presented);
    }

    private bool TokenMatches(string presented)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(bytes, _tokenBytes);
    }

    private bool TryResolveStaticFile(string path, out string file)
    {
        file = string.Empty;
        string relative = Uri.UnescapeDataString(path.TrimStart('/'));
        if (relative.Length == 0 || relative.Contains('\0'))
        {
            return false;
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment == ".." || segment == ".")
            {
                return false;
            }
        }

        string full = Path.GetFullPath(Path.Combine(_webRoot, relative));
        if (!full.StartsWith(_webRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            return false;
        }

        file = full;
        return true;
    }

    private static async Task ServeFileAsync(HttpListenerResponse response, string file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        response.StatusCode = 200;
        response.ContentType = s_contentTypes.TryGetValue(info.Extension, out string? contentType)
            ? contentType
            : "application/octet-stream";
        response.ContentLength64 = info.Length;
        using FileStream stream = File.OpenRead(file);
        await stream.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, s_json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
