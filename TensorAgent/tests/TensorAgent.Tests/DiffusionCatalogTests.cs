// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using TensorAgent.Core.Catalog;
using TensorAgent.Core.Hosting;
using TensorAgent.Core.Settings;

namespace TensorAgent.Tests;

/// <summary>
/// Tests that write the process's own environment, kept out of everything else's way.
///
/// <para>
/// <c>DiffusionCompanions</c> publishes to environment variables because that is the
/// only channel the Qwen-Image DiT reads, and an environment is one object shared by
/// every test in the assembly. Any class that constructs an <c>AgentAppHost</c>
/// publishes too, so without this the two race and the loser fails somewhere else
/// entirely — which is exactly what happened before it was added.
/// </para>
/// </summary>
[CollectionDefinition(ProcessEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process environment";
}

/// <summary>
/// Whether the image-generation entry's five files are the five files the pipeline
/// goes looking for, under names it will recognise.
///
/// <para>
/// This is the only failure in the catalog that costs a user eleven gigabytes before
/// it shows itself. Every other mistake — a wrong size, a wrong hash, a dead URL —
/// stops the download; a companion whose name the pipeline's directory scan does not
/// match downloads perfectly, verifies perfectly, and is then silently ignored, and
/// the user gets a worse picture with no explanation anywhere.
/// </para>
/// <para>
/// The scans are stated here rather than called, because they are private to
/// <c>QwenImageModel</c>. <see cref="TheScansThisFileMirrorsAreStillTheOnesTheModelPerforms"/>
/// is what keeps the two from drifting apart.
/// </para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DiffusionCatalogTests
{
    private static CatalogModel ImageEdit =>
        ModelCatalog.BuiltIn.Single(m => m.Kind == CatalogArchitectureKind.Diffusion);

    private static string NameOf(CatalogModel model, CatalogFileRole role) =>
        model.Files.Single(f => f.Role == role).FileName;

    /// <summary>The VAE scan: <c>QwenImageModel.ResolveVaeCompanion</c> — one of three
    /// preferred names, or any file whose name contains "vae".</summary>
    private static bool VaeScanFinds(string fileName)
    {
        string n = fileName.ToLowerInvariant();
        return n is "qwen_image_vae.gguf" or "qwen_image_vae.safetensors" or "qwen_image-vae.safetensors"
            || (n.Contains("vae") && (n.EndsWith(".gguf") || n.EndsWith(".safetensors")));
    }

    /// <summary>The text-encoder scan: <c>QwenImageModel.ResolveTeCompanion</c> — the
    /// largest GGUF naming a Qwen2.5-VL and not a projector.</summary>
    private static bool TextEncoderScanFinds(string fileName)
    {
        string n = fileName.ToLowerInvariant();
        return n.EndsWith(".gguf")
            && (n.Contains("qwen2.5-vl") || n.Contains("qwen2_5_vl") || n.Contains("qwen-image-te"))
            && !n.Contains("mmproj");
    }

    /// <summary>The vision-projector scan: <c>QwenImageModel</c>'s constructor —
    /// a GGUF naming both a projector and the family it belongs to.</summary>
    private static bool VisionProjectorScanFinds(string fileName)
    {
        string n = fileName.ToLowerInvariant();
        return n.EndsWith(".gguf") && n.Contains("mmproj") && (n.Contains("qwen2") || n.Contains("qwen-image"));
    }

    [Fact]
    public void TheImageEditEntryCarriesEveryNetworkTheDiTDoesNotContain()
    {
        CatalogModel model = ImageEdit;
        foreach (CatalogFileRole role in new[]
                 {
                     CatalogFileRole.Weights, CatalogFileRole.TextEncoder,
                     CatalogFileRole.Vae, CatalogFileRole.Lora, CatalogFileRole.VisionProjector,
                 })
        {
            Assert.True(model.Files.Any(f => f.Role == role), $"{model.Id} has no {role}");
        }

        // The VAE and the text encoder are not optional: QwenImageModel's constructor
        // throws FileNotFoundException without them, so an entry that marks either one
        // optional is an entry that can finish downloading and then refuse to load.
        Assert.False(model.Files.Single(f => f.Role == CatalogFileRole.Vae).Optional);
        Assert.False(model.Files.Single(f => f.Role == CatalogFileRole.TextEncoder).Optional);
    }

    [Fact]
    public void EveryCompanionIsNamedSomethingThePipelinesOwnScanWillMatch()
    {
        CatalogModel model = ImageEdit;

        Assert.True(VaeScanFinds(NameOf(model, CatalogFileRole.Vae)),
            $"the VAE '{NameOf(model, CatalogFileRole.Vae)}' is not a name ResolveVaeCompanion looks for");

        Assert.True(TextEncoderScanFinds(NameOf(model, CatalogFileRole.TextEncoder)),
            $"the text encoder '{NameOf(model, CatalogFileRole.TextEncoder)}' is not a name ResolveTeCompanion looks for");

        Assert.True(VisionProjectorScanFinds(NameOf(model, CatalogFileRole.VisionProjector)),
            $"the vision projector '{NameOf(model, CatalogFileRole.VisionProjector)}' is not a name the mmproj scan "
            + "looks for — it needs 'mmproj' AND 'qwen2' (or 'qwen-image') in it, or the image grounding is "
            + "silently dropped and the edit runs on the prompt alone");
    }

    [Fact]
    public void NoTwoCompanionsAnswerToTheSameScan()
    {
        // The three scans run over one directory, so a name that satisfies two of them
        // hands one network to the wrong loader. The text-encoder scan in particular
        // takes the LARGEST matching GGUF, which is what the projector would be if its
        // name did not say "mmproj".
        CatalogModel model = ImageEdit;
        string weights = NameOf(model, CatalogFileRole.Weights);
        string textEncoder = NameOf(model, CatalogFileRole.TextEncoder);
        string projector = NameOf(model, CatalogFileRole.VisionProjector);

        Assert.False(TextEncoderScanFinds(projector), $"the projector '{projector}' would be loaded as the text encoder");
        Assert.False(TextEncoderScanFinds(weights), $"the DiT '{weights}' would be loaded as the text encoder");
        Assert.False(VisionProjectorScanFinds(textEncoder), $"the text encoder '{textEncoder}' would be loaded as the projector");
        Assert.False(VaeScanFinds(weights));
        Assert.False(VaeScanFinds(textEncoder));
        Assert.False(VaeScanFinds(projector));
    }

    [Fact]
    public void TheScansThisFileMirrorsAreStillTheOnesTheModelPerforms()
    {
        // A weak guard and an honest one: it cannot tell that a predicate's logic
        // changed, only that the strings it matches on are still there. That is enough
        // to catch the rename this file exists to prevent, and the alternative — making
        // the resolvers public on a shared assembly so a phone test can call them — is
        // a worse trade.
        string model = ReadSource("TensorSharp.Models/Models/QwenImage/QwenImageModel.cs");
        foreach (string literal in new[]
                 {
                     "\"TS_QWEN_IMAGE_VAE\"", "\"TS_QWEN_IMAGE_TE\"", "\"TS_QWEN_IMAGE_MMPROJ\"",
                     "\"qwen_image_vae.safetensors\"", "\"Qwen_Image-VAE.safetensors\"",
                     "n.Contains(\"qwen2.5-vl\")", "n.Contains(\"mmproj\")",
                 })
        {
            Assert.Contains(literal, model, StringComparison.Ordinal);
        }

        string dit = ReadSource("TensorSharp.Models/Models/QwenImage/QwenImageDiT.cs");
        Assert.Contains("\"TS_QWEN_IMAGE_LORA\"", dit, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepDistillationLoraIsPublishedBecauseNothingScansForIt()
    {
        // QwenImageDiT.LoraPath reads TS_QWEN_IMAGE_LORA and nothing else. A LoRA the
        // user paid 850 MB for and that the entry's own notes promise ("4 Lightning
        // steps") is therefore inert unless the host says where it is.
        using var installation = new FakeInstall(ImageEdit, install: model => model.Files);

        IReadOnlyDictionary<string, string> published = DiffusionCompanions.Publish(ImageEdit, installation.Store);

        Assert.Equal(
            installation.PathOf(CatalogFileRole.Lora),
            Environment.GetEnvironmentVariable("TS_QWEN_IMAGE_LORA"));
        Assert.Equal(installation.PathOf(CatalogFileRole.Vae), published["TS_QWEN_IMAGE_VAE"]);
        Assert.Equal(installation.PathOf(CatalogFileRole.TextEncoder), published["TS_QWEN_IMAGE_TE"]);
        Assert.Equal(installation.PathOf(CatalogFileRole.VisionProjector), published["TS_QWEN_IMAGE_MMPROJ"]);
    }

    [Fact]
    public void ACompanionThatWasNeverDownloadedIsClearedRatherThanPointedAt()
    {
        // The projector and the LoRA are optional downloads, and the environment is
        // process-wide: leaving a variable set from a previous selection turns "you
        // chose not to download this" into a load that dies naming a file the user has
        // never heard of.
        Environment.SetEnvironmentVariable("TS_QWEN_IMAGE_LORA", "/somewhere/from/before.safetensors");
        using var installation = new FakeInstall(ImageEdit,
            install: model => model.Files.Where(f => f.Role != CatalogFileRole.Lora));

        IReadOnlyDictionary<string, string> published = DiffusionCompanions.Publish(ImageEdit, installation.Store);

        Assert.Null(Environment.GetEnvironmentVariable("TS_QWEN_IMAGE_LORA"));
        Assert.False(published.ContainsKey("TS_QWEN_IMAGE_LORA"));
        Assert.Equal(installation.PathOf(CatalogFileRole.Vae), published["TS_QWEN_IMAGE_VAE"]);
    }

    [Fact]
    public void SelectingSomethingThatIsNotADiffusionModelLeavesNothingBehind()
    {
        using var installation = new FakeInstall(ImageEdit, install: model => model.Files);
        DiffusionCompanions.Publish(ImageEdit, installation.Store);
        DiffusionCompanions.Publish(null, installation.Store);

        foreach (string variable in new[]
                 { "TS_QWEN_IMAGE_VAE", "TS_QWEN_IMAGE_TE", "TS_QWEN_IMAGE_MMPROJ", "TS_QWEN_IMAGE_LORA" })
        {
            Assert.Null(Environment.GetEnvironmentVariable(variable));
        }
    }

    /// <summary>The repo's own copy of a file, so a test can read the source it mirrors.</summary>
    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TensorSharp.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null,
            $"no repository root above {AppContext.BaseDirectory}; this test reads {relativePath} from the working tree");
        string full = Path.Combine(directory!.FullName, relativePath);
        Assert.True(File.Exists(full), $"{full} is missing");
        return File.ReadAllText(full);
    }

    /// <summary>
    /// A model directory holding a chosen subset of an entry's files, so the publisher
    /// can be exercised against what is really on disk. The files are empty: nothing
    /// here loads them, and the point is which paths exist.
    /// </summary>
    private sealed class FakeInstall : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tensoragent-diffusion-" + Guid.NewGuid().ToString("N"));
        private readonly CatalogModel _model;

        public FakeInstall(CatalogModel model, Func<CatalogModel, IEnumerable<CatalogFile>> install)
        {
            _model = model;
            Store = new ModelStore(_root);
            Directory.CreateDirectory(Store.DirectoryFor(model));
            foreach (CatalogFile file in install(model))
                File.WriteAllBytes(Store.PathFor(model, file), []);
        }

        public ModelStore Store { get; }

        public string PathOf(CatalogFileRole role) =>
            Store.PathFor(_model, _model.Files.Single(f => f.Role == role));

        public void Dispose()
        {
            // The publisher writes process-wide state; a test that left it set would
            // decide what the next one sees.
            DiffusionCompanions.Publish(null, Store);
            try { Directory.Delete(_root, true); } catch (Exception) { /* scratch */ }
        }
    }

    /// <summary>
    /// The wiring, not the helper. Every other test here calls
    /// <c>DiffusionCompanions.Publish</c> directly, which proves the helper works and
    /// says nothing about whether anything calls it — and a helper nobody calls is
    /// exactly the state this fix was made to correct. This builds a real host with a
    /// diffusion model selected and its files on disk, and asserts the variables the
    /// pipeline reads are set by the time the host is constructed.
    /// </summary>
    [Fact]
    public void BuildingTheHostPublishesTheCompanionsThePipelineWillLookFor()
    {
        CatalogModel model = ModelCatalog.BuiltIn.First(m => m.Kind == CatalogArchitectureKind.Diffusion);
        string root = Path.Combine(Path.GetTempPath(), "tensoragent-wired-" + Guid.NewGuid().ToString("N"));
        var paths = new AgentPaths(Path.Combine(root, "data"), Path.Combine(root, "cache")) { DeviceMemoryGB = 16 };
        paths.EnsureCreated();

        // Every companion has to exist: Publish names a file it can see, never one it
        // hopes for, so a half-downloaded model publishes nothing.
        string directory = Path.Combine(paths.ModelsDirectory, model.Id);
        Directory.CreateDirectory(directory);
        foreach (CatalogFile file in model.Files)
            File.WriteAllBytes(Path.Combine(directory, file.FileName), new byte[] { 1, 2, 3 });

        var settings = new SettingsStore(paths.SettingsFile);
        AppSettings chosen = settings.Load();
        chosen.SelectedModelId = model.Id;
        settings.Save(chosen);

        foreach (string variable in new[] { "TS_QWEN_IMAGE_VAE", "TS_QWEN_IMAGE_TE", "TS_QWEN_IMAGE_MMPROJ", "TS_QWEN_IMAGE_LORA" })
            Environment.SetEnvironmentVariable(variable, null);

        using var host = new AgentAppHost(paths);

        // The LoRA is the one that matters most: without it the pipeline runs 30 steps
        // with CFG instead of 4, which on a phone is the difference between a feature
        // and a stall.
        string? lora = Environment.GetEnvironmentVariable("TS_QWEN_IMAGE_LORA");
        Assert.False(string.IsNullOrEmpty(lora), "constructing the host did not publish the Lightning LoRA");
        Assert.Equal(Path.Combine(directory, model.Files.First(f => f.Role == CatalogFileRole.Lora).FileName), lora);

        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TS_QWEN_IMAGE_VAE")));
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TS_QWEN_IMAGE_TE")));
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TS_QWEN_IMAGE_MMPROJ")));

        try { Directory.Delete(root, true); } catch { }
    }
}
