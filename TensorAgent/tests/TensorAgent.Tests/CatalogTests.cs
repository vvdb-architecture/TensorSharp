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
            Assert.Contains(m.MinDeviceMemoryGB, new[] { 6, 8, 12, 16 });
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

    [Fact]
    public void TwelveGigabyteTierStaysUnderTheJetsamBudget()
    {
        // Metal wires the mmap'd weights: resident ~ GGUF bytes + F32 projector (~2x its file)
        // + KV + ~0.5 GB compute. 8.5 GB is what a 12 GB iPhone grants with the entitlement.
        const double budget = 8.5e9;
        foreach (CatalogModel m in ModelCatalog.ForDevice(12).Where(m => !m.IsImageGenerator))
        {
            double resident = m.Weights.Bytes + 0.5e9
                + (m.Projector is { Optional: false } p ? 2.0 * p.Bytes : 0);
            Assert.True(resident < budget, $"{m.Id}: estimated resident {resident / 1e9:F1} GB exceeds {budget / 1e9:F1} GB");
        }
    }

    [Fact]
    public void DeviceTiersGateTheLargeEntries()
    {
        Assert.DoesNotContain(ModelCatalog.ForDevice(8), m => m.Weights.Bytes > 6e9);
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
