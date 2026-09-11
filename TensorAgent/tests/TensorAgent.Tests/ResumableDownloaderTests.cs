using System.Net;
using System.Security.Cryptography;
using System.Text;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Downloads;

namespace TensorAgent.Tests;

/// <summary>A loopback file server with Range support and fault injection.</summary>
internal sealed class RangeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public byte[] Body { get; }
    public string Url { get; }
    /// <summary>Close the connection after this many bytes of the first N responses.</summary>
    public int CutAfterBytes { get; set; } = -1;
    public int CutResponses { get; set; }
    public bool IgnoreRange { get; set; }
    /// <summary>Pause between 64 KB chunks so a client can cancel mid-transfer.</summary>
    public int DelayPerChunkMs { get; set; }
    public int Requests { get; private set; }
    public List<string> RangeHeaders { get; } = new();

    public RangeServer(byte[] body)
    {
        Body = body;
        int port = FreePort();
        Url = $"http://127.0.0.1:{port}/file.bin";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            Requests++;
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            string? range = ctx.Request.Headers["Range"];
            RangeHeaders.Add(range ?? "");
            long start = 0;
            if (range is not null && !IgnoreRange && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                start = long.Parse(range["bytes=".Length..].TrimEnd('-'));
                if (start >= Body.Length)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{Body.Length - 1}/{Body.Length}";
            }
            long length = Body.Length - start;
            ctx.Response.ContentLength64 = length;
            bool cut = CutAfterBytes >= 0 && CutResponses > 0;
            if (cut) CutResponses--;
            long toSend = cut ? Math.Min(length, CutAfterBytes) : length;
            const int chunk = 64 * 1024;
            for (long sent = 0; sent < toSend; sent += chunk)
            {
                int n = (int)Math.Min(chunk, toSend - sent);
                ctx.Response.OutputStream.Write(Body, (int)(start + sent), n);
                ctx.Response.OutputStream.Flush();
                if (DelayPerChunkMs > 0) Thread.Sleep(DelayPerChunkMs);
            }
            if (cut)
                ctx.Response.Abort();
            else
                ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}

internal sealed class SyncProgress(Action<DownloadProgress> handler) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => handler(value);
}

public sealed class ResumableDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tensoragent-dl-" + Guid.NewGuid().ToString("N"));

    public ResumableDownloaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Body(int size)
    {
        var b = new byte[size];
        new Random(42).NextBytes(b);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public async Task DownloadsAndVerifies()
    {
        byte[] body = Body(3 * 1024 * 1024 + 17);
        using var server = new RangeServer(body);
        var dl = new ResumableDownloader(maxAttempts: 1);
        string dest = Path.Combine(_dir, "a.bin");
        var reports = new List<DownloadProgress>();
        await dl.DownloadAsync(server.Url, dest, body.Length, Sha(body), new Progress<DownloadProgress>(reports.Add), CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(ResumableDownloader.PartPath(dest)));
    }

    [Fact]
    public async Task ResumesAfterAConnectionDrop()
    {
        byte[] body = Body(4 * 1024 * 1024);
        using var server = new RangeServer(body) { CutAfterBytes = 1_500_000, CutResponses = 1 };
        var dl = new ResumableDownloader(maxAttempts: 3);
        string dest = Path.Combine(_dir, "b.bin");
        await dl.DownloadAsync(server.Url, dest, body.Length, Sha(body), null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
        // Second request resumed from the kept .part.
        Assert.True(server.Requests >= 2);
        Assert.Contains(server.RangeHeaders, h => h.StartsWith("bytes=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumesAcrossRuns_KeepingThePartFile()
    {
        byte[] body = Body(2 * 1024 * 1024);
        using var server = new RangeServer(body) { CutAfterBytes = 700_000, CutResponses = 1 };
        string dest = Path.Combine(_dir, "c.bin");
        var first = new ResumableDownloader(maxAttempts: 1);
        await Assert.ThrowsAsync<IOException>(() => first.DownloadAsync(server.Url, dest, body.Length, Sha(body), null, CancellationToken.None));
        string part = ResumableDownloader.PartPath(dest);
        Assert.True(File.Exists(part));
        Assert.True(new FileInfo(part).Length > 0);

        var second = new ResumableDownloader(maxAttempts: 1);
        await second.DownloadAsync(server.Url, dest, body.Length, Sha(body), null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
        Assert.Equal("bytes=" + server.RangeHeaders[^1]["bytes=".Length..], server.RangeHeaders[^1]);
    }

    [Fact]
    public async Task RestartsWhenTheServerIgnoresRanges()
    {
        byte[] body = Body(1024 * 1024);
        using var server = new RangeServer(body) { IgnoreRange = true };
        string dest = Path.Combine(_dir, "d.bin");
        await File.WriteAllBytesAsync(ResumableDownloader.PartPath(dest), body[..1000]);
        var dl = new ResumableDownloader(maxAttempts: 2);
        await dl.DownloadAsync(server.Url, dest, body.Length, Sha(body), null, CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task RejectsACorruptFile()
    {
        byte[] body = Body(512 * 1024);
        using var server = new RangeServer(body);
        string dest = Path.Combine(_dir, "e.bin");
        var dl = new ResumableDownloader(maxAttempts: 1);
        var ex = await Assert.ThrowsAsync<DownloadIntegrityException>(() =>
            dl.DownloadAsync(server.Url, dest, body.Length, new string('0', 64), null, CancellationToken.None));
        Assert.Equal(Sha(body), ex.ActualSha256);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(ResumableDownloader.PartPath(dest)));
    }

    [Fact]
    public async Task ACompleteFileIsNotRedownloaded()
    {
        byte[] body = Body(100_000);
        using var server = new RangeServer(body);
        string dest = Path.Combine(_dir, "f.bin");
        await File.WriteAllBytesAsync(dest, body);
        var dl = new ResumableDownloader(maxAttempts: 1);
        await dl.DownloadAsync(server.Url, dest, body.Length, Sha(body), null, CancellationToken.None);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task CancellationKeepsThePartFile()
    {
        byte[] body = Body(8 * 1024 * 1024);
        using var server = new RangeServer(body) { DelayPerChunkMs = 5 };
        string dest = Path.Combine(_dir, "g.bin");
        var dl = new ResumableDownloader(maxAttempts: 1);
        using var cts = new CancellationTokenSource();
        // Progress<T> posts to the pool; a synchronous reporter cancels deterministically.
        var progress = new SyncProgress(p => { if (p.BytesReceived > 1_000_000) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dl.DownloadAsync(server.Url, dest, body.Length, null, progress, cts.Token));
        Assert.True(File.Exists(ResumableDownloader.PartPath(dest)));
        Assert.False(File.Exists(dest));
        long kept = new FileInfo(ResumableDownloader.PartPath(dest)).Length;
        Assert.InRange(kept, 1_000_000, body.Length - 1);
    }

    [Fact]
    public async Task ModelStoreDownloadsEveryRequiredFileAndReportsWholeEntryProgress()
    {
        byte[] weights = Body(600_000);
        byte[] proj = Body(200_000);
        using var s1 = new RangeServer(weights);
        using var s2 = new RangeServer(proj);
        var model = new CatalogModel
        {
            Id = "test-model",
            DisplayName = "Test",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "1",
            Quantization = "Q",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "w.gguf", s1.Url, weights.Length, Sha(weights)),
                new CatalogFile(CatalogFileRole.Projector, "mmproj.gguf", s2.Url, proj.Length, Sha(proj), Optional: true),
            },
            Modalities = CatalogModalities.Image,
            MinDeviceMemoryGB = 6,
            ContextLength = 1024,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1, 1, 1, 0),
            License = "test",
        };
        var store = new ModelStore(Path.Combine(_dir, "models"), new ResumableDownloader(maxAttempts: 1));
        var created = new List<string>();
        store.OnFileCreated = created.Add;
        Assert.Equal(InstallState.NotInstalled, store.StateOf(model));

        var reports = new List<ModelDownloadProgress>();
        await store.DownloadAsync(model, new Progress<ModelDownloadProgress>(reports.Add), CancellationToken.None);
        Assert.Equal(InstallState.Installed, store.StateOf(model));
        Assert.NotNull(store.WeightsPath(model));
        Assert.Null(store.CompanionPath(model, CatalogFileRole.Projector));   // optional, not requested
        Assert.Single(created);

        await store.DownloadAsync(model, null, CancellationToken.None, new[] { CatalogFileRole.Projector });
        Assert.NotNull(store.CompanionPath(model, CatalogFileRole.Projector));
        Assert.Equal(weights.Length + proj.Length, store.InstalledBytes(model));
        Assert.Equal(0, store.RemainingBytes(model, includeOptional: true));

        store.DeleteFile(model, model.Projector!);
        Assert.Null(store.CompanionPath(model, CatalogFileRole.Projector));
        Assert.Equal(InstallState.Installed, store.StateOf(model));
        store.Delete(model);
        Assert.Equal(InstallState.NotInstalled, store.StateOf(model));
    }
}
