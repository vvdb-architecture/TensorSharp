using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        Assert.Equal("<html>ui</html>", await entry.Content.ReadAsStringAsync());
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

    [Fact]
    public void SseFramingMatchesTheServersSseWriter()
    {
        // The Server writes: "data: " + JsonSerializer.Serialize(payload) + "\n\n" with default options.
        object frame = new { token = "hi", n = (int?)null };
        Assert.Equal("data: " + JsonSerializer.Serialize(frame) + "\n\n", SseFraming.Format(frame));
    }
}
