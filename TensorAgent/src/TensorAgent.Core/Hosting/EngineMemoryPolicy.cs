using System;
using TensorAgent.Core.Catalog;
using TensorAgent.Core.Settings;

namespace TensorAgent.Core.Hosting;

/// <summary>
/// Hands the engine the memory budget a phone actually has, before a model loads.
///
/// <para>
/// The catalog has always carried a per-entry <see cref="CatalogModel.ContextLength"/>
/// and <see cref="CatalogModel.KvCacheDtype"/>, and until this existed neither reached
/// the engine. The context length's only reader was the JSON payload the page renders;
/// <see cref="AppSettings.ContextLength"/>, documented as "override of the catalog
/// entry's context length", was read by nothing at all; and
/// <c>KvCacheDtypeConfig.ConfigureFromEnvironment()</c> was called by TensorSharp.Server
/// and TensorSharp.Cli but never by the MAUI head. So the engine fell back to the GGUF's
/// own number, and for Qwen3.5 9B that is 262,144.
/// </para>
///
/// <para>
/// That is not a theoretical ceiling. Qwen3.5 9B spends 2 (K+V) x 8 attention layers x 4
/// KV heads x 256 head dim x F16 = 32 KiB of KV per token, and on Metal it is charged
/// TWICE: once for the host tensor (an anonymous mmap from GgmlMemoryPool) and once for
/// the Metal side, which refuses the zero-copy wrap for read-write tensors and allocates
/// its own buffer -- posix_memalign on iOS. 64 KiB per token against a jetsam budget of
/// roughly two thirds of the device's RAM. MEASURED on this model, backend ggml_metal,
/// with a 24,696-token prompt (a pasted document -- the ordinary case this is for):
///   no MAX_CONTEXT: cache expanded to 32768 tokens, peak footprint 5,679 MB
///   MAX_CONTEXT=8192:                                peak footprint   943 MB
/// The first number is what killed TensorAgent on a 12 GB iPhone.
/// </para>
///
/// <para>
/// Setting MAX_CONTEXT also switches the engine from growing the cache on demand to
/// reserving the whole window at load (ModelBase.ResolveInitialCacheAllocationLength
/// skips its GPU cap when the context is explicit). That is the behaviour to want here:
/// the reservation is bounded and paid once, instead of arriving as a geometric growth
/// mid-conversation whose superseded blocks GgmlMemoryPool then retains. A budget that
/// is wrong is discovered at load, where it can be reported, rather than as a kill.
/// </para>
///
/// <para>
/// A prompt longer than the window is not an error: ChatGenerationPipeline's
/// TruncatePromptToContext trims history to fit and reports it, which is what a chat
/// should do regardless of the device.
/// </para>
/// </summary>
public static class EngineMemoryPolicy
{
    /// <summary>The engine reads this at model construction (ModelBase.ResolveConfiguredContextLength).</summary>
    public const string MaxContextVariable = "MAX_CONTEXT";

    /// <summary>Read by KvCacheDtypeConfig.ConfigureFromEnvironment().</summary>
    public const string KvCacheDtypeVariable = "KV_CACHE_DTYPE";

    /// <summary>
    /// Apply <paramref name="model"/>'s budget for the load that is about to happen.
    /// Returns the context length handed to the engine, or 0 when the entry does not
    /// state one (the diffusion entries, which hold no KV cache) and the GGUF's own
    /// value is left alone.
    /// </summary>
    public static int Apply(CatalogModel model, AppSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The user's override wins where they set one; otherwise the catalog entry,
        // which is written per model against the device tier that is offered it.
        int context = settings?.ContextLength is int chosen && chosen > 0
            ? chosen
            : model.ContextLength;

        if (context > 0)
            Environment.SetEnvironmentVariable(MaxContextVariable, context.ToString());
        else
            Environment.SetEnvironmentVariable(MaxContextVariable, null);

        Environment.SetEnvironmentVariable(
            KvCacheDtypeVariable,
            string.IsNullOrWhiteSpace(model.KvCacheDtype) ? null : model.KvCacheDtype);

        // Managed-to-managed, and read when the model is constructed, which is after
        // this. The static config is what the model layer consults for the cache dtype;
        // without this call the variable above would be as inert as it was before.
        TensorSharp.Models.KvCacheDtypeConfig.ConfigureFromEnvironment();

        Console.WriteLine(
            $"TensorAgent: engine budget for {model.Id} -- context {(context > 0 ? context.ToString() : "from GGUF")}, " +
            $"KV cache {model.KvCacheDtype ?? "auto"}");

        return context;
    }
}
