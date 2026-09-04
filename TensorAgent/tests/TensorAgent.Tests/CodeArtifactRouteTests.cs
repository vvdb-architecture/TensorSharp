using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TensorAgent.Core.Hosting;
using TensorSharp.AgentHost.CodeExec;

namespace TensorAgent.Tests;

/// <summary>
/// The link a model hands you for a file its own code produced has to resolve.
///
/// <para>
/// The runner emits <c>/api/code/artifacts/{runId}/{path}</c>, and the app mapped no
/// such route: TensorSharp.Server had the endpoint, the phone did not. The model
/// correctly said "here is your PDF", the link rendered, and tapping it produced
/// "error: not found" -- for every generated document, PDF and PPTX alike.
/// </para>
/// </summary>
public sealed class CodeArtifactRouteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ta-artifacts-" + Guid.NewGuid().ToString("n"));
    private readonly LoopbackServer _server;
    private readonly HttpClient _client;
    private readonly CodeArtifactStore _artifacts;

    public CodeArtifactRouteTests()
    {
        Directory.CreateDirectory(_root);
        _artifacts = new CodeArtifactStore(Path.Combine(_root, "artifacts"));
        _server = new LoopbackServer(NullLogger.Instance);
        _server.MapCodeArtifacts(_artifacts);
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

    /// <summary>Capture a file the way a finished code run does, and return its URL.</summary>
    private string CaptureOne(string runId, string name, byte[] bytes)
    {
        string work = Path.Combine(_root, "work-" + runId);
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, name), bytes);
        IReadOnlyList<CodeArtifact> captured = _artifacts.Capture(
            runId, work, (id, rel, _) => $"/api/code/artifacts/{id}/{rel}", out _);
        return captured.Single(a => a.Path.EndsWith(name, StringComparison.Ordinal)).Pointer;
    }

    [Fact]
    public async Task AGeneratedPdfIsDownloadableFromTheLinkTheModelShows()
    {
        // A real PDF header, so the content type is decided by extension rather than by
        // sniffing bytes a model's program chose.
        byte[] pdf = "%PDF-1.7\n%âãÏÓ\ntrailer\n"u8.ToArray();
        string url = CaptureOne("runpdf", "photo.pdf", pdf);

        HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AGeneratedPptxIsDownloadableToo()
    {
        // The other half of the report: "search the news and make me a deck" produces
        // this, and it was unreachable for exactly the same reason.
        byte[] pptx = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 };
        string url = CaptureOne("rundeck", "news.pptx", pptx);

        HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pptx, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TheFileArrivesAsAnAttachmentSoAWebViewSavesRatherThanRenders()
    {
        // Served as an attachment on purpose: this content came out of a program a
        // model wrote, and a WebView must never render it inline.
        string url = CaptureOne("runattach", "report.pdf", "%PDF-1.7\n"u8.ToArray());

        HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("report.pdf", response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);
    }

    [Fact]
    public async Task AnUnknownRunIsNotFoundRatherThanAServerError()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/code/artifacts/nope/missing.pdf");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EscapingTheStoreIsRefused()
    {
        // The path segment comes from a program a model wrote. Confinement is
        // re-checked by the store, not trusted from the route.
        CaptureOne("runesc", "ok.pdf", "%PDF-1.7\n"u8.ToArray());
        HttpResponseMessage response = await _client.GetAsync("/api/code/artifacts/runesc/..%2f..%2fsettings.json");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListingARunNamesEveryFileItLeft()
    {
        CaptureOne("runlist", "a.pdf", "%PDF-1.7\n"u8.ToArray());
        HttpResponseMessage response = await _client.GetAsync("/api/code/artifacts/runlist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("a.pdf", await response.Content.ReadAsStringAsync());
    }
}
