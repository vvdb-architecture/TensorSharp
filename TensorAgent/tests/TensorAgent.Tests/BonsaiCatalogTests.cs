using System.Security.Cryptography;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Settings;

namespace TensorAgent.Tests;

public sealed class BonsaiCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tensoragent-bonsai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort test cleanup */ }
    }

    [Fact]
    public void CatalogPinsBothPublisherlessBonsaiArtifactsForLocalImport()
    {
        CatalogModel[] bonsai = ModelCatalog.BuiltIn
            .Where(model => model.Family == CatalogFamily.Bonsai)
            .OrderBy(model => model.Weights.Bytes)
            .ToArray();

        Assert.Collection(bonsai,
            eight =>
            {
                Assert.Equal("bonsai-8b-q1-0", eight.Id);
                Assert.Equal("Bonsai-8B-Q1_0.gguf", eight.Weights.FileName);
                Assert.Equal(1_158_654_496, eight.Weights.Bytes);
                Assert.Equal("284a335aa3fb2ced3b1b01fcb40b08aa783e3b70832767f0dd2e3fdfa134bd54",
                    eight.Weights.Sha256);
                Assert.Equal(new CatalogSampling(0.5f, 20, 0.85f, 0.0f), eight.Sampling);
                Assert.False(eight.SupportsThinking);
            },
            twentySeven =>
            {
                Assert.Equal("bonsai-27b-q1-0", twentySeven.Id);
                Assert.Equal("Bonsai-27B-Q1_0.gguf", twentySeven.Weights.FileName);
                Assert.Equal(3_803_452_480, twentySeven.Weights.Bytes);
                Assert.Equal("17ef842e47450caeb8eaa3ebfbbab5d2f2278b62b79be107985fb69a2f819aa0",
                    twentySeven.Weights.Sha256);
                Assert.Equal(new CatalogSampling(1.0f, 20, 0.95f, 0.0f), twentySeven.Sampling);
                Assert.True(twentySeven.SupportsThinking);
            });

        Assert.All(bonsai, model =>
        {
            Assert.True(model.SideloadOnly);
            Assert.Empty(model.Weights.Url);
            Assert.Equal("Q1_0", model.Quantization);
            Assert.Equal("q8_0", model.KvCacheDtype);
        });
    }

    [Fact]
    public async Task ImportStagesAndHashChecksBeforePublishingTheWeights()
    {
        byte[] expected = Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray();
        CatalogModel model = SideloadCard(expected);
        var store = new ModelStore(_root);
        string? notified = null;
        store.OnFileCreated = path => notified = path;

        await using var source = new MemoryStream(expected, writable: false);
        await store.ImportAsync(model, source);

        string destination = store.PathFor(model, model.Weights);
        Assert.Equal(InstallState.Installed, store.StateOf(model));
        Assert.Equal(destination, store.WeightsPath(model));
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Equal(destination, notified);
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryFor(model), "*.import-*"));
    }

    [Fact]
    public async Task WrongLocalFileCannotReplaceAPreviouslyVerifiedImport()
    {
        byte[] expected = Enumerable.Range(0, 4096).Select(i => (byte)(i * 17)).ToArray();
        byte[] wrong = Enumerable.Range(0, expected.Length).Select(i => (byte)(255 - i)).ToArray();
        CatalogModel model = SideloadCard(expected);
        var store = new ModelStore(_root);
        Directory.CreateDirectory(store.DirectoryFor(model));
        string destination = store.PathFor(model, model.Weights);
        await File.WriteAllBytesAsync(destination, expected);

        await using var source = new MemoryStream(wrong, writable: false);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ImportAsync(model, source));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryFor(model), "*.import-*"));
    }

    [Fact]
    public async Task SideloadCardCannotFallThroughToAnEmptyDownloadUrl()
    {
        CatalogModel model = SideloadCard(new byte[4096]);
        var store = new ModelStore(_root);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.DownloadAsync(model, progress: null, CancellationToken.None));

        Assert.Contains("Import", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(store.DirectoryFor(model)));
    }

    [Fact]
    public void OrphanSweepRetainsTheTwoSideloadDestinations()
    {
        var store = new ModelStore(_root);
        foreach (CatalogModel model in ModelCatalog.BuiltIn.Where(model => model.SideloadOnly))
        {
            Directory.CreateDirectory(store.DirectoryFor(model));
            File.WriteAllText(Path.Combine(store.DirectoryFor(model), "keep.marker"), model.Id);
        }
        Directory.CreateDirectory(Path.Combine(_root, "not-in-the-catalog"));
        File.WriteAllText(Path.Combine(_root, "not-in-the-catalog", "remove.marker"), "orphan");

        store.SweepOrphanedModels();

        Assert.All(ModelCatalog.BuiltIn.Where(model => model.SideloadOnly), model =>
            Assert.True(File.Exists(Path.Combine(store.DirectoryFor(model), "keep.marker"))));
        Assert.False(Directory.Exists(Path.Combine(_root, "not-in-the-catalog")));
    }

    [Theory]
    [InlineData("bonsai-8b-q1-0", "Bonsai-8B-Q1_0.gguf")]
    [InlineData("bonsai-27b-q1-0", "Bonsai-27B-Q1_0.gguf")]
    public void SavedSelectionResolvesToTheCatalogOwnedSideloadPath(string id, string fileName)
    {
        var paths = new AgentPaths(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var settings = new AppSettings { SelectedModelId = id };

        Assert.Equal(
            Path.Combine(paths.ModelsDirectory, id, fileName),
            paths.SelectedModelPath(settings));
    }

    private static CatalogModel SideloadCard(byte[] expected) => new()
    {
        Id = "bonsai-test",
        DisplayName = "Bonsai test fixture",
        Family = CatalogFamily.Bonsai,
        Kind = CatalogArchitectureKind.Dense,
        Parameters = "test",
        Quantization = "Q1_0",
        Files = new[]
        {
            new CatalogFile(
                CatalogFileRole.Weights,
                "bonsai-test.gguf",
                string.Empty,
                expected.LongLength,
                Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant()),
        },
        Modalities = CatalogModalities.Text,
        MinDeviceMemoryGB = 12,
        ContextLength = 4096,
        KvCacheDtype = "q8_0",
        Sampling = new CatalogSampling(0.5f, 20, 0.85f, 0.0f),
        SupportsThinking = true,
        SideloadOnly = true,
        License = "test",
    };
}
