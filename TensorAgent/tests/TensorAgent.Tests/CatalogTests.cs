using System.Text.RegularExpressions;
using TensorAgent.Core.Catalog;

namespace TensorAgent.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void EveryEntryIsWellFormed()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogModel m in ModelCatalog.BuiltIn)
        {
            Assert.True(ids.Add(m.Id), $"duplicate id {m.Id}");
            Assert.Matches("^[a-z0-9.-]+$", m.Id);
            Assert.NotEmpty(m.DisplayName);
            Assert.NotEmpty(m.Files);
            Assert.Single(m.Files, f => f.Role == CatalogFileRole.Weights);
            Assert.False(m.Weights.Optional, $"{m.Id}: weights cannot be optional");
            foreach (CatalogFile f in m.Files)
            {
                Assert.StartsWith("https://huggingface.co/", f.Url);
                Assert.EndsWith("/resolve/main/" + f.Url.Split("/resolve/main/")[1], f.Url);
                Assert.True(f.Bytes > 1_000_000, $"{m.Id}/{f.FileName}: size {f.Bytes}");
                Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                Assert.False(f.FileName.Contains('/'), $"{m.Id}: file names are bare ({f.FileName})");
            }
            // 24 is not a phone that exists; it is how an entry says "no current device
            // grants enough memory for this", which is a measured fact about
            // Qwen-Image-Edit rather than a placeholder. ForDevice then never offers it.
            Assert.Contains(m.MinDeviceMemoryGB, new[] { 6, 8, 12, 16, 24 });
            Assert.NotEmpty(m.License);
        }
    }

    [Fact]
    public void ProjectorsAreDistinctFromWeights()
    {
        foreach (CatalogModel m in ModelCatalog.BuiltIn)
        {
            var names = m.Files.Select(f => f.FileName).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    /// <summary>
    /// What a device of a given size actually grants the app. A 12 GB iPhone gives
    /// about 8.5 GB with <c>com.apple.developer.kernel.increased-memory-limit</c>, and
    /// the fraction holds well enough across the range to gate a catalog with.
    /// </summary>
    private static double JetsamBudget(int deviceGB) => deviceGB * 1e9 * (8.5 / 12.0);

    /// <summary>
    /// Metal wires the mmap'd weights, so resident memory is roughly the GGUF plus the
    /// F32 projector (about twice its file), the KV cache and half a gigabyte of
    /// compute buffers.
    /// </summary>
    private static double EstimatedResident(CatalogModel model) =>
        model.Weights.Bytes + 0.5e9 + (model.Projector is { Optional: false } p ? 2.0 * p.Bytes : 0);

    [Fact]
    public void EveryTierStaysUnderTheJetsamBudgetOfTheSmallestDeviceItIsOfferedOn()
    {
        // Checking only the 12 GB tier let two entries through that could never load
        // on the tier they advertised: Gemma 4 E2B claimed 6 GB and needs 6.6, and
        // E4B Q4_K_XL claimed 8 and needs 6.8. A phone would have offered a five
        // gigabyte download and then been killed opening it — the worst outcome the
        // catalog can produce, because the user pays for it twice.
        foreach (CatalogModel m in ModelCatalog.BuiltIn.Where(m => !m.IsImageGenerator))
        {
            double resident = EstimatedResident(m);
            double budget = JetsamBudget(m.MinDeviceMemoryGB);
            Assert.True(resident < budget,
                $"{m.Id} is offered at {m.MinDeviceMemoryGB} GB, which grants about "
                + $"{budget / 1e9:F1} GB, but needs about {resident / 1e9:F1} GB resident");
        }
    }

    [Fact]
    public void EveryModelADeviceIsOfferedFitsThatDevice()
    {
        // The other direction: whatever ForDevice hands back must fit the device that
        // asked, for every size a real iPhone comes in.
        foreach (int deviceGB in new[] { 6, 8, 12, 16 })
        {
            foreach (CatalogModel m in ModelCatalog.ForDevice(deviceGB).Where(m => !m.IsImageGenerator))
            {
                Assert.True(EstimatedResident(m) < JetsamBudget(deviceGB),
                    $"a {deviceGB} GB device is offered {m.Id}, which needs about "
                    + $"{EstimatedResident(m) / 1e9:F1} GB against a {JetsamBudget(deviceGB) / 1e9:F1} GB budget");
            }
        }
    }

    [Fact]
    public void DeviceTiersGateTheLargeEntries()
    {
        Assert.Empty(ModelCatalog.ForDevice(8));
        Assert.Contains(ModelCatalog.ForDevice(12), m => m.Id == "qwen3.8-27b-iq2xxs");
        Assert.DoesNotContain(ModelCatalog.ForDevice(12), m => m.Kind == CatalogArchitectureKind.MixtureOfExperts);
        Assert.Contains(ModelCatalog.ForDevice(16), m => m.Kind == CatalogArchitectureKind.MixtureOfExperts);
        Assert.Contains(ModelCatalog.BuiltIn, m => m.Family == CatalogFamily.Gemma4 && m.Kind == CatalogArchitectureKind.Dense);
        Assert.Contains(ModelCatalog.BuiltIn, m => m.Family == CatalogFamily.Qwen38 && m.Kind == CatalogArchitectureKind.Dense);
        Assert.Contains(ModelCatalog.BuiltIn, m => m.IsImageGenerator);
    }

    [Theory]
    [InlineData(11_560_000_000L, 12)]
    [InlineData(12_000_000_000L, 12)]
    [InlineData(8_100_000_000L, 8)]
    [InlineData(7_700_000_000L, 8)]
    [InlineData(16_700_000_000L, 16)]
    [InlineData(5_800_000_000L, 6)]
    public void MemoryTierRoundsToTheMarketingNumber(long bytes, int tier)
    {
        Assert.Equal(tier, ModelCatalog.DeviceMemoryTier(bytes));
    }

    [Fact]
    public void FindIsCaseInsensitive()
    {
        Assert.NotNull(ModelCatalog.Find("GEMMA-4-E4B-Q4KXL"));
        Assert.Null(ModelCatalog.Find("nope"));
    }
}
