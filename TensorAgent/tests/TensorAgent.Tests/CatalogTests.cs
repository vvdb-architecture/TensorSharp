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
    /// The memory that is actually charged against the jetsam limit: ANONYMOUS memory
    /// only.
    ///
    /// <para>
    /// This used to be <c>Weights.Bytes + 0.5e9 + 2*projector</c>, on the stated premise
    /// that "Metal wires the mmap'd weights, so resident memory is roughly the GGUF".
    /// That premise is wrong, and measurably so. Weights are a file mapping, and Darwin
    /// charges mapped clean pages essentially nothing; ggml-metal wraps them with
    /// newBufferWithBytesNoCopy and the residency set does not fault them in. MEASURED on
    /// this repo, ggml_metal, Qwen3.6-35B-A3B UD-IQ2_XXS -- an 11,272 MB model at
    /// ctx=8192: peak RSS 1,211 MB, peak phys_footprint 1,090 MB. The old model
    /// overstated that entry by roughly ten times, and gated two MoE entries to 16 GB
    /// that a 12 GB phone holds with ~7 GB to spare.
    /// </para>
    ///
    /// <para>
    /// What IS anonymous: the KV cache (charged TWICE on Metal -- once for the host
    /// tensor and once for the Metal-side buffer, since the zero-copy wrap is refused for
    /// read-write tensors), the projector's dequantized copies (about twice its file),
    /// and the runtime plus graph scratch. 64 KiB/token is the per-token KV rate measured
    /// for Qwen3.5 9B and is used here as an upper bound; the 35B-A3B hybrids are cheaper
    /// still, at 40 KiB/token, because only 10 of their 40 layers hold a KV cache.
    /// </para>
    /// </summary>
    private static double EstimatedAnonymous(CatalogModel model) =>
        0.5e9
        + model.ContextLength * 64.0 * 1024.0
        + (model.Projector is { Optional: false } p ? 2.0 * p.Bytes : 0);

    /// <summary>
    /// The other half, and the one the weights really answer to: they are not charged to
    /// jetsam, but they still have to be READABLE at a usable speed, which means the file
    /// has to fit the DEVICE's RAM alongside iOS rather than the app's jetsam budget.
    /// Past this line every token faults expert weights from flash. It is a performance
    /// bound, not a kill bound, which is why it is a separate number from
    /// <see cref="JetsamBudget"/> instead of folded into it.
    /// </summary>
    private static double WeightsResidencyCeiling(int deviceGB) => deviceGB * 1e9 * 0.87;

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
            double anonymous = EstimatedAnonymous(m);
            double budget = JetsamBudget(m.MinDeviceMemoryGB);
            Assert.True(anonymous < budget,
                $"{m.Id} is offered at {m.MinDeviceMemoryGB} GB, which grants about "
                + $"{budget / 1e9:F1} GB, but charges about {anonymous / 1e9:F1} GB of anonymous memory");

            double ceiling = WeightsResidencyCeiling(m.MinDeviceMemoryGB);
            Assert.True(m.Weights.Bytes < ceiling,
                $"{m.Id} is offered at {m.MinDeviceMemoryGB} GB but its weights are "
                + $"{m.Weights.Bytes / 1e9:F1} GB, past the {ceiling / 1e9:F1} GB that device can "
                + "hold; it would fault every token from flash");
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
                Assert.True(EstimatedAnonymous(m) < JetsamBudget(deviceGB),
                    $"a {deviceGB} GB device is offered {m.Id}, which charges about "
                    + $"{EstimatedAnonymous(m) / 1e9:F1} GB against a {JetsamBudget(deviceGB) / 1e9:F1} GB budget");
                Assert.True(m.Weights.Bytes < WeightsResidencyCeiling(deviceGB),
                    $"a {deviceGB} GB device is offered {m.Id}, whose {m.Weights.Bytes / 1e9:F1} GB of "
                    + $"weights exceed the {WeightsResidencyCeiling(deviceGB) / 1e9:F1} GB it can hold");
            }
        }
    }

    [Fact]
    public void DeviceTiersGateTheLargeEntries()
    {
        Assert.Empty(ModelCatalog.ForDevice(8));
        Assert.Contains(ModelCatalog.ForDevice(12), m => m.Id == "qwen3.8-27b-iq2xxs");
        // A 12 GB phone IS now offered the mixture-of-experts entries. It was not, on the
        // premise that Metal wires the mapped weights; measurement says otherwise (see
        // EstimatedAnonymous), and both MoE entries charge about 0.8 GB of anonymous
        // memory against the ~8.5 GB such a phone grants. They stay Experimental and
        // their Notes say plainly that the weights will page from flash.
        Assert.Contains(ModelCatalog.ForDevice(12), m => m.Kind == CatalogArchitectureKind.MixtureOfExperts);
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
