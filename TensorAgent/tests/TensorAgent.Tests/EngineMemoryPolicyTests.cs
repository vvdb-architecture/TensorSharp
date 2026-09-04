using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Settings;

namespace TensorAgent.Tests;

/// <summary>
/// The catalog's context length has to REACH the engine.
///
/// <para>
/// It did not, and that is what killed the app. Every entry carries a ContextLength,
/// and its only reader was the JSON the page renders; AppSettings.ContextLength,
/// documented as the user's override, was read by nothing at all. So the engine used
/// the GGUF's own number -- 262,144 for Qwen3.5 9B -- and a pasted document grew the
/// KV cache until jetsam killed the process. Measured on ggml_metal with a
/// 24,696-token prompt: 5,679 MB of physical footprint unbounded, 943 MB with the
/// budget applied.
/// </para>
/// </summary>
public sealed class EngineMemoryPolicyTests : IDisposable
{
    private readonly string? _savedContext = Environment.GetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable);
    private readonly string? _savedDtype = Environment.GetEnvironmentVariable(EngineMemoryPolicy.KvCacheDtypeVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable, _savedContext);
        Environment.SetEnvironmentVariable(EngineMemoryPolicy.KvCacheDtypeVariable, _savedDtype);
    }

    private static CatalogModel Entry(string id) =>
        ModelCatalog.Find(id) ?? throw new InvalidOperationException($"catalog entry {id} is gone");

    [Fact]
    public void TheCatalogEntrysContextLengthReachesTheEngine()
    {
        CatalogModel qwen = Entry("qwen3.5-9b-q4kxl");
        int applied = EngineMemoryPolicy.Apply(qwen, new AppSettings());

        Assert.Equal(qwen.ContextLength, applied);
        Assert.Equal(
            qwen.ContextLength.ToString(),
            Environment.GetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable));
    }

    [Fact]
    public void TheUsersOwnOverrideWinsOverTheCatalog()
    {
        CatalogModel qwen = Entry("qwen3.5-9b-q4kxl");
        int applied = EngineMemoryPolicy.Apply(qwen, new AppSettings { ContextLength = 4096 });

        Assert.Equal(4096, applied);
        Assert.Equal("4096", Environment.GetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable));
    }

    [Fact]
    public void AnEntryThatStatesNoContextLeavesTheGgufsOwnValueAlone()
    {
        // The diffusion entries hold no KV cache and set ContextLength = 0. Applying a
        // budget there would be meaningless; what matters is that a STALE variable from
        // a previous load does not leak into this one.
        Environment.SetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable, "8192");
        CatalogModel diffusion = ModelCatalog.BuiltIn.First(m => m.ContextLength == 0);

        Assert.Equal(0, EngineMemoryPolicy.Apply(diffusion, new AppSettings()));
        Assert.Null(Environment.GetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable));
    }

    [Fact]
    public void SwitchingModelsReplacesTheBudgetRatherThanKeepingTheOldOne()
    {
        EngineMemoryPolicy.Apply(Entry("qwen3.5-9b-q4kxl"), new AppSettings());
        string? first = Environment.GetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable);

        EngineMemoryPolicy.Apply(Entry("qwen3.8-27b-iq2xxs"), new AppSettings());
        string? second = Environment.GetEnvironmentVariable(EngineMemoryPolicy.MaxContextVariable);

        // Against the catalog, not literals: the point of the test is that switching
        // REPLACES the budget, and hardcoding the numbers only breaks it when a
        // context is legitimately retuned.
        Assert.Equal(Entry("qwen3.5-9b-q4kxl").ContextLength.ToString(), first);
        Assert.Equal(Entry("qwen3.8-27b-iq2xxs").ContextLength.ToString(), second);
        Assert.NotNull(first);
    }

    [Fact]
    public void TheKvCacheDtypeReachesTheEngineToo()
    {
        CatalogModel qwen = Entry("qwen3.5-9b-q4kxl");
        EngineMemoryPolicy.Apply(qwen, new AppSettings());

        Assert.Equal(qwen.KvCacheDtype, Environment.GetEnvironmentVariable(EngineMemoryPolicy.KvCacheDtypeVariable));
    }

    /// <summary>
    /// A catalog entry must not ask for a K/V dtype its family cannot read.
    ///
    /// <para>
    /// Gemma 4 declines a block-quantized cache (Gemma4Model.SupportsBlockQuantizedKvCache):
    /// its sliding-window layers use a CIRCULAR cache whose managed helpers are float-only,
    /// and the 26B-A4B MoE reaches them on an ordinary prompt. Setting q8_0 on a Gemma entry
    /// therefore did not merely fall back -- before the load-time refusal it crashed the app
    /// with "Requires a Float32 tensor, but found Q8_0" out of CopyToCacheCircular the moment
    /// a user typed. Qwen3.5/3.6 take the fused graph, whose native side is dtype-generic,
    /// and keep the memory win.
    /// </para>
    /// </summary>
    [Fact]
    public void NoEntryAsksForAKvDtypeItsFamilyCannotRead()
    {
        foreach (CatalogModel m in ModelCatalog.BuiltIn)
        {
            bool blockQuant = m.KvCacheDtype is "q8_0" or "q4_0";
            if (m.Family == CatalogFamily.Gemma4)
            {
                Assert.False(blockQuant,
                    $"{m.Id} asks for {m.KvCacheDtype}, but Gemma 4 refuses a block-quantized cache; "
                    + "its circular sliding-window helpers are float-only and the managed path is "
                    + "reachable from a plain prompt");
            }
        }
    }

    /// <summary>
    /// The other direction: the families that CAN take it should still be taking it, so the
    /// memory win is not quietly reverted along with a Gemma fix.
    /// </summary>
    [Fact]
    public void TheQwenEntriesStillAskForTheQuantizedCache()
    {
        List<CatalogModel> qwen = ModelCatalog.BuiltIn
            .Where(m => m.Family is CatalogFamily.Qwen35 or CatalogFamily.Qwen36 or CatalogFamily.Qwen38)
            .Where(m => m.Kind != CatalogArchitectureKind.Diffusion)
            .ToList();

        Assert.NotEmpty(qwen);
        foreach (CatalogModel m in qwen)
        {
            Assert.True(m.KvCacheDtype == "q8_0",
                $"{m.Id} is on {m.KvCacheDtype}: Qwen3.5/3.6 run the fused graph, which reads a "
                + "block-quantized cache at decode parity for ~46% less KV memory");
        }
    }

    /// <summary>
    /// The drift guard, and the point of the whole file: a new text model added to the
    /// catalog without a context length would silently inherit the GGUF's, which is how
    /// this bug existed in the first place.
    /// </summary>
    [Fact]
    public void EveryModelThatHoldsAKvCacheStatesItsContextLength()
    {
        foreach (CatalogModel m in ModelCatalog.BuiltIn)
        {
            if (m.Kind == CatalogArchitectureKind.Diffusion)
                continue;
            Assert.True(
                m.ContextLength > 0,
                $"{m.Id}: a text model must state ContextLength, or the engine falls back to the " +
                "GGUF's own window (262,144 for Qwen3.5) and the KV cache grows until jetsam");
            Assert.True(
                m.ContextLength <= 32768,
                $"{m.Id}: ContextLength {m.ContextLength} is beyond what a phone's jetsam budget holds");
        }
    }
}
