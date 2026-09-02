using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TensorAgent.Core.Hosting;

namespace TensorAgent.Tests;

public sealed class LoopbackServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tensoragent-loop-" + Guid.NewGuid().ToString("N"));

    public LoopbackServerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static HttpClient Client() => new(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() });

    [Fact]
    public async Task ServesStaticFilesJsonRoutesAndSse_WithTheTokenHandshake()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<html>ui</html>");
        Directory.CreateDirectory(Path.Combine(_dir, "images"));
        File.WriteAllBytes(Path.Combine(_dir, "images", "logo.png"), new byte[] { 1, 2, 3 });
        using var server = new LoopbackServer { StaticRoot = _dir };
        server.MapGet("/api/models", (_, _) => Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new { loaded = (string?)null, models = Array.Empty<string>() })));
        server.MapGet("/api/sessions/{id}", (r, _) => Task.FromResult<LoopbackResponse?>(LoopbackResponse.Json(new { id = r.RouteValues["id"] })));
        server.MapGet("/api/code/artifacts/{run}/{*path}", (r, _) => Task.FromResult<LoopbackResponse?>(LoopbackResponse.Text(r.RouteValues["run"] + "|" + r.RouteValues["path"])));
        server.MapPost("/api/chat", (_, _) => Task.FromResult<LoopbackResponse?>(LoopbackResponse.Sse(Frames())));
        server.Start();

        using var http = Client();
        // No token, no cookie -> 403 even for static files.
        var denied = await http.GetAsync(server.BaseUrl + "/api/models");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var entry = await http.GetAsync(server.EntryUrl);
        Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
        // The page is served whole, with the app's companion script appended. That one
        // addition is what lets index.html stay byte-identical with the Server's copy.
        string served = await entry.Content.ReadAsStringAsync();
        Assert.StartsWith("<html>ui</html>", served, StringComparison.Ordinal);
        Assert.Contains("<script src=\"/tensoragent.js\"></script>", served, StringComparison.Ordinal);
        Assert.Contains("text/html", entry.Content.Headers.ContentType!.ToString());

        // Cookie now carries the token.
        string models = await http.GetStringAsync(server.BaseUrl + "/api/models");
        Assert.Equal("{\"loaded\":null,\"models\":[]}", models);
        var png = await http.GetAsync(server.BaseUrl + "/images/logo.png");
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await png.Content.ReadAsByteArrayAsync());
        Assert.Equal("{\"id\":\"abc\"}", await http.GetStringAsync(server.BaseUrl + "/api/sessions/abc"));
        Assert.Equal("r1|a/b/c.txt", await http.GetStringAsync(server.BaseUrl + "/api/code/artifacts/r1/a/b/c.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.BaseUrl + "/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.BaseUrl + "/../etc/passwd")).StatusCode);

        // SSE: 'data: {json}\n\n' frames, streamed.
        using var chat = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/api/chat") { Content = new StringContent("{}", Encoding.UTF8, "application/json") }, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", chat.Content.Headers.ContentType!.MediaType);
        string body = await chat.Content.ReadAsStringAsync();
        Assert.Equal("data: {\"token\":\"Hel\"}\n\ndata: {\"token\":\"lo\"}\n\ndata: {\"done\":true,\"error\":null}\n\n", body);
    }

    private static async IAsyncEnumerable<object> Frames()
    {
        yield return new { token = "Hel" };
        await Task.Delay(10);
        yield return new { token = "lo" };
        yield return new { done = true, error = (string?)null };
    }

    [Fact]
    public async Task HandlerExceptionsBecomeStatusPayloads()
    {
        using var server = new LoopbackServer { RequireToken = false };
        server.MapGet("/api/rejected", (_, _) => throw new LoopbackHttpException(404, new { error = "Session 'x' not found." }));
        server.MapGet("/api/boom", (_, _) => throw new InvalidOperationException("bad"));
        server.Start();
        using var http = Client();
        var rejected = await http.GetAsync(server.BaseUrl + "/api/rejected");
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        using var doc = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("Session 'x' not found.", doc.RootElement.GetProperty("error").GetString());
        var boom = await http.GetAsync(server.BaseUrl + "/api/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, boom.StatusCode);
    }

    [Fact]
    public async Task ParsesMultipartUploadsLikeTheWebUiSends()
    {
        using var server = new LoopbackServer { RequireToken = false };
        server.MapPost("/api/upload", async (r, ct) =>
        {
            Assert.True(r.HasFormContentType);
            using MultipartForm form = await r.ReadFormAsync(ct);
            MultipartFile file = Assert.Single(form.Files);
            byte[] bytes = await File.ReadAllBytesAsync(file.TempPath, ct);
            return LoopbackResponse.Json(new { file.FieldName, file.FileName, file.ContentType, file.Length, sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), overwrite = form["overwrite"] });
        });
        server.Start();

        byte[] payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        // Make sure the body contains boundary-like sequences to exercise the search.
        Encoding.ASCII.GetBytes("\r\n--").CopyTo(payload, 1000);
        using var http = Client();
        using var content = new MultipartFormDataContent("----TensorAgentBoundary");
        content.Add(new StringContent("true"), "overwrite");
        var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Add(fileContent, "file", "clip.mp4");
        var response = await http.PostAsync(server.BaseUrl + "/api/upload", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("file", doc.RootElement.GetProperty("FieldName").GetString());
        Assert.Equal("clip.mp4", doc.RootElement.GetProperty("FileName").GetString());
        Assert.Equal("video/mp4", doc.RootElement.GetProperty("ContentType").GetString());
        Assert.Equal(payload.Length, doc.RootElement.GetProperty("Length").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), doc.RootElement.GetProperty("sha").GetString());
        Assert.Equal("true", doc.RootElement.GetProperty("overwrite").GetString());
    }

    [Fact]
    public void RoutePatternsMatchMinimalApiShapes()
    {
        Assert.True(new RoutePattern("/api/sessions/{id}").TryMatch("/api/sessions/abc", out var v) && v["id"] == "abc");
        Assert.False(new RoutePattern("/api/sessions/{id}").TryMatch("/api/sessions", out _));
        Assert.False(new RoutePattern("/api/sessions/{id}").TryMatch("/api/sessions/a/b", out _));
        Assert.True(new RoutePattern("/api/skills/{name}/files/{*path}").TryMatch("/api/skills/pdf/files/scripts/a.py", out v) && v["path"] == "scripts/a.py");
        Assert.True(new RoutePattern("/").TryMatch("/", out _));
        Assert.False(new RoutePattern("/").TryMatch("/x", out _));
    }

    // ---- a reader that goes away ----------------------------------------------------

    [Fact]
    public async Task AClientThatDisconnectsStopsTheGenerationItStarted()
    {
        // Nobody is listening and the model generates to the end of its budget anyway:
        // minutes of a phone's battery and all of its memory bandwidth spent on an
        // answer no one will ever read. A failed write is the only place a disconnect
        // surfaces at all, so that is where the producer has to be cancelled.
        //
        // Asynchronous continuations, because this is completed inside the server's own
        // Cancel() call: completed synchronously there, it would run the rest of this
        // test on the request's own thread and the request would never end.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LoopbackServer { RequireToken = false };
        server.MapGet("/api/chat-stream", (request, ct) =>
            Task.FromResult<LoopbackResponse?>(LoopbackResponse.Sse(Generation(ct), request.Cancellation)));
        server.Start();

        using var reader = await RawStream.OpenAsync(server, "/api/chat-stream");
        string opening = await reader.ReadUntilAsync(body => body.Contains("data: ", StringComparison.Ordinal));
        Assert.Contains("text/event-stream", reader.Raw, StringComparison.Ordinal);
        Assert.Contains("data: ", opening, StringComparison.Ordinal);

        reader.Drop();

        await Completes(cancelled.Task,
            "the client is gone and the producer is still running: a dropped connection has to cancel the request it started");

        // A generation that keeps talking, so the disconnect is found by a frame that
        // cannot be written rather than by a keep-alive. The frame that already arrived
        // is what says it had started.
        async IAsyncEnumerable<object> Generation(CancellationToken ct)
        {
            using CancellationTokenRegistration stop = ct.Register(() => cancelled.TrySetResult());
            for (int i = 0; i < 100_000; i++)
            {
                yield return new { token = new string('t', 256) };
                await Task.Delay(5, ct);
            }
        }
    }

    [Fact]
    public async Task AStreamThatGoesQuietSaysSoWithCommentsRatherThanWithFrames()
    {
        // A prefill can hold a stream silent for minutes, and silence is the one state
        // in which a reader that left cannot be noticed: nothing is written, so nothing
        // fails. The comment exists to give the stream something to write.
        using var server = new LoopbackServer { RequireToken = false };
        server.MapGet("/api/chat-stream", (request, ct) =>
            Task.FromResult<LoopbackResponse?>(LoopbackResponse.Sse(
                OneFrameThenSilence(ct), request.Cancellation, keepAlive: TimeSpan.FromMilliseconds(120))));
        server.Start();

        using var reader = await RawStream.OpenAsync(server, "/api/chat-stream");
        string stream = await reader.ReadUntilAsync(text => Occurrences(text, ": keep-alive") >= 3);

        Assert.True(Occurrences(stream, ": keep-alive") >= 3,
            $"a stream quiet for three keep-alive periods wrote no keep-alive:\n{stream}");

        // And what the page reads is unchanged. Its reader takes 'data: ' lines and
        // ignores everything else, so a keep-alive that arrived as a frame would be
        // parsed as JSON and land in the answer.
        string[] frames = stream.Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        Assert.Equal(new[] { "data: {\"token\":\"first\"}" }, frames);

        // Every stream above passes its own interval, so the one the app actually runs
        // with is asserted here: a keep-alive quietly disabled in production would
        // leave all of these green.
        Assert.InRange(LoopbackResponse.DefaultKeepAlive, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task AClientThatLeavesDuringALongPrefillStopsTheGenerationRatherThanWaitingForATokenToFail()
    {
        // The same disconnect as the first test, in the state where the stream has
        // nothing to say. This is the case that hangs without a keep-alive: the first
        // token can be minutes away, and until something is written nothing tells the
        // server that the page it is generating for has closed.
        //
        // See the first test for why the continuations are asynchronous: completed on
        // the request's own thread, these would keep the request from ever finishing.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefillOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new RecordingLogger();
        using var server = new LoopbackServer(log) { RequireToken = false };
        server.MapGet("/api/chat-stream", (request, ct) =>
            Task.FromResult<LoopbackResponse?>(LoopbackResponse.Sse(
                Prefill(ct), request.Cancellation, keepAlive: TimeSpan.FromMilliseconds(120))));
        server.Start();

        using var reader = await RawStream.OpenAsync(server, "/api/chat-stream");
        string quiet = await reader.ReadUntilAsync(text => text.Contains(": keep-alive", StringComparison.Ordinal));
        Assert.Contains(": keep-alive", quiet, StringComparison.Ordinal);

        reader.Drop();

        await Completes(cancelled.Task,
            "a reader that left while the stream was quiet was never noticed: the phone is still generating for it");

        // And it ended as what it is, rather than as a server error. The prefill is let
        // go first and the server disposed second, because disposing waits for the
        // request: by the time this reads the log, everything that request had to say
        // has been said.
        prefillOver.TrySetResult();
        server.Dispose();
        Assert.True(log.Entries.Count == 0,
            "a reader that walked away was reported as a failed request: " + string.Join(" | ", log.Entries));

        async IAsyncEnumerable<object> Prefill(CancellationToken ct)
        {
            using CancellationTokenRegistration stop = ct.Register(() => cancelled.TrySetResult());
            yield return new { token = "first" };
            // Ended by the test, and not by the cancellation, on purpose. A prefill is
            // the work that notices a cancellation only when it next looks up — between
            // tokens, or when the graph compute it is inside returns — so the stream is
            // torn down here with a pull still running, which is the hard case. It also
            // means no token can arrive during the assertion above and pass it for the
            // wrong reason.
            await prefillOver.Task;
            ct.ThrowIfCancellationRequested();
            yield return new { done = true, error = (string?)null };
        }
    }

    /// <summary>One frame, then the silence of a model reading a long prompt.</summary>
    private static async IAsyncEnumerable<object> OneFrameThenSilence(CancellationToken ct)
    {
        yield return new { token = "first" };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield return new { done = true, error = (string?)null };
    }

    /// <summary>Wait for something that must happen, and say what it means when it does not.</summary>
    private static async Task Completes(Task task, string because)
    {
        Task first = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(first, task), because);
        await task;
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Whatever the server thought worth a warning or worse.
    ///
    /// <para>
    /// A reader that goes away is an ordinary ending, and the difference between
    /// handling one and merely surviving it shows up nowhere else: the producer is
    /// cancelled either way, and only the log says whether the request that was
    /// serving it ended as a client leaving or as a server error.
    /// </para>
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level))
                return;
            lock (Entries)
                Entries.Add($"{level}: {format(state, error)}{(error is null ? string.Empty : " / " + error.GetType().Name)}");
        }
    }

    /// <summary>
    /// One connection, driven by hand.
    ///
    /// <para>
    /// <see cref="HttpClient"/> is the wrong instrument for these tests: it decides for
    /// itself when a connection is really finished with, and what is under test is what
    /// the server does the instant it is not. This sends the request, reads the event
    /// stream the way the page does, and can vanish mid-stream the way a WebView does
    /// when the app is suspended — abruptly, so that the server's next write fails now
    /// rather than whenever the socket would otherwise time out.
    /// </para>
    /// </summary>
    private sealed class RawStream : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StringBuilder _received = new();

        private RawStream(TcpClient tcp) => _tcp = tcp;

        public static async Task<RawStream> OpenAsync(LoopbackServer server, string path)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            byte[] request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nAccept: text/event-stream\r\n\r\n");
            await tcp.GetStream().WriteAsync(request);
            return new RawStream(tcp);
        }

        /// <summary>Everything that arrived, headers and chunk framing included.</summary>
        public string Raw => _received.ToString();

        /// <summary>
        /// Read until <paramref name="enough"/> is satisfied by the body, then hand the
        /// body back — including when it never was, so the assertion can print what the
        /// server sent instead.
        /// </summary>
        public async Task<string> ReadUntilAsync(Func<string, bool> enough)
        {
            byte[] buffer = new byte[4096];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (!enough(Body()))
                {
                    int read = await _tcp.GetStream().ReadAsync(buffer, deadline.Token);
                    if (read == 0)
                        break;
                    _received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
            catch (OperationCanceledException)
            {
                // Out of time; the caller asserts on what did arrive.
            }
            return Body();
        }

        /// <summary>
        /// The bytes a reader of the event stream sees: past the headers and with the
        /// chunk framing removed.
        ///
        /// <para>
        /// It has to be undone by hand because the framing is an artefact of the
        /// transport, not of the protocol under test — the server flushes each piece of
        /// a frame separately, so 'data: ', the JSON and the blank line arrive as three
        /// chunks with a length line between them, and a test that asserted on those
        /// would be asserting on chunk boundaries. Counting characters as bytes is safe
        /// here and only here: every frame these tests send is ASCII.
        /// </para>
        /// </summary>
        private string Body()
        {
            string raw = _received.ToString();
            int headers = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headers < 0)
                return string.Empty;

            var body = new StringBuilder();
            int at = headers + 4;
            while (at < raw.Length)
            {
                int eol = raw.IndexOf("\r\n", at, StringComparison.Ordinal);
                if (eol < 0)
                    break;
                if (!int.TryParse(raw.AsSpan(at, eol - at), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size)
                    || size == 0)
                {
                    break;
                }
                int from = eol + 2;
                if (from + size > raw.Length)
                    break;
                body.Append(raw, from, size);
                at = from + size + 2;
            }
            return body.ToString();
        }

        /// <summary>
        /// Vanish. The zero linger makes it a reset rather than a polite close, which
        /// is what the server sees when the page holding the connection is gone.
        /// </summary>
        public void Drop()
        {
            _tcp.Client.LingerState = new LingerOption(true, 0);
            _tcp.Close();
        }

        public void Dispose() => _tcp.Dispose();
    }

    [Fact]
    public void SseFramingMatchesTheServersSseWriter()
    {
        // The Server writes: "data: " + JsonSerializer.Serialize(payload) + "\n\n" with default options.
        object frame = new { token = "hi", n = (int?)null };
        Assert.Equal("data: " + JsonSerializer.Serialize(frame) + "\n\n", SseFraming.Format(frame));
    }
}
