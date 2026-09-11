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
/// A representative Qwen-Image entry used only by the test assembly.
///
/// <para>
/// TensorAgent intentionally no longer offers a diffusion checkpoint in its built-in
/// catalog. The companion publisher is still production infrastructure, though, and
/// needs a complete multi-file model to exercise it without making a removed download
/// appear to be supported. Live media tests also use this fixture to give explicitly
/// supplied local files the names and roles the pipeline expects.
/// </para>
/// </summary>
internal static class DiffusionModelFixture
{
    internal static CatalogModel ImageEdit { get; } = new()
    {
        Id = "test-qwen-image-edit-2511",
        DisplayName = "Qwen-Image-Edit 2511 test fixture",
        Family = CatalogFamily.QwenImage,
        Kind = CatalogArchitectureKind.Diffusion,
        Parameters = "20B DiT + 7B text encoder",
        Quantization = "Q2_K (DiT) / IQ2_XXS (text encoder)",
        Files = new[]
        {
            new CatalogFile(CatalogFileRole.Weights, "qwen-image-edit-2511-Q2_K.gguf", string.Empty,
                7_468_022_368, "a3d09042b64657970654941aa08d895de29b4d98edf3632a89e70d4d6e23c47c"),
            new CatalogFile(CatalogFileRole.TextEncoder, "Qwen2.5-VL-7B-Instruct-UD-IQ2_XXS.gguf", string.Empty,
                2_398_444_416, "9fdde01492c884464ec3713aa02993c7b56711ee392904f2a53dd92cbe9f1967"),
            new CatalogFile(CatalogFileRole.Vae, "Qwen_Image-VAE.safetensors", string.Empty,
                253_806_246, "a70580f0213e67967ee9c95f05bb400e8fb08307e017a924bf3441223e023d1f"),
            new CatalogFile(CatalogFileRole.Lora,
                "Qwen-Image-Edit-2511-Lightning-4steps-V1.0-bf16.safetensors", string.Empty,
                849_608_296, "22226e8d05d354bb356627d428809f5afd7819399b077238a2b70a82883a904f"),
            new CatalogFile(CatalogFileRole.VisionProjector,
                "Qwen2.5-VL-7B-Instruct-mmproj-BF16.gguf", string.Empty,
                1_354_163_040, "f0edf43c09b69d6e5dd24262f33b356a1e9dd978e7c3299b3e69141fcbb87553",
                Optional: true),
        },
        Modalities = CatalogModalities.Image | CatalogModalities.ImageOutput,
        MinDeviceMemoryGB = 24,
        ContextLength = 0,
        KvCacheDtype = "f16",
        Sampling = new CatalogSampling(1.0f, 0, 1.0f, 0.0f),
        Experimental = true,
        License = "Apache-2.0",
        Notes = "Test-only diffusion fixture; not a built-in TensorAgent model.",
    };
}

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
/// Whether a representative image-generation model's five files are the five files
/// the pipeline goes looking for, under names it will recognise.
///
/// <para>
/// A companion whose name the pipeline's directory scan does not match can be present
/// and valid yet still be silently ignored. Keeping a representative definition here
/// catches that integration failure without requiring a built-in catalog entry.
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
        DiffusionModelFixture.ImageEdit;

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
    public void TheImageEditFixtureCarriesEveryNetworkTheDiTDoesNotContain()
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
    public void EveryFixtureCompanionIsNamedSomethingThePipelinesOwnScanWillMatch()
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
    public void NoTwoFixtureCompanionsAnswerToTheSameScan()
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
        // QwenImageDiT.LoraPath reads TS_QWEN_IMAGE_LORA and nothing else. A supplied
        // 850 MB Lightning LoRA is therefore inert unless the host says where it is.
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
        // Companion files may be absent from a partial or deliberately minimal local
        // installation, and the environment is process-wide. Leaving a variable from
        // a previous selection would point the next load at unrelated state.
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
                 {
                     "TS_QWEN_IMAGE_VAE", "TS_QWEN_IMAGE_TE", "TS_QWEN_IMAGE_MMPROJ",
                     "TS_QWEN_IMAGE_LORA", "TS_QWEN_IMAGE_MAX_AREA",
                 })
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
    /// A remembered selection can outlive the catalog entry it names. Constructing a
    /// host for that state must clear process-wide companion paths rather than leave a
    /// previous diffusion run wired into an unrelated model.
    /// </summary>
    [Fact]
    public void BuildingTheHostClearsCompanionsForARemovedDiffusionSelection()
    {
        CatalogModel model = ImageEdit;
        const string removedId = "qwen-image-edit-2511-q2k";
        Assert.Null(ModelCatalog.Find(removedId));

        string root = Path.Combine(Path.GetTempPath(), "tensoragent-wired-" + Guid.NewGuid().ToString("N"));
        var paths = new AgentPaths(Path.Combine(root, "data"), Path.Combine(root, "cache")) { DeviceMemoryGB = 16 };
        paths.EnsureCreated();

        var settings = new SettingsStore(paths.SettingsFile);
        AppSettings chosen = settings.Load();
        chosen.SelectedModelId = removedId;
        settings.Save(chosen);

        using var installation = new FakeInstall(model, install: candidate => candidate.Files);
        Assert.NotEmpty(DiffusionCompanions.Publish(model, installation.Store));

        using (var host = new AgentAppHost(paths))
        {
            foreach (string variable in new[]
                     {
                         "TS_QWEN_IMAGE_VAE", "TS_QWEN_IMAGE_TE", "TS_QWEN_IMAGE_MMPROJ",
                         "TS_QWEN_IMAGE_LORA", "TS_QWEN_IMAGE_MAX_AREA",
                     })
            {
                Assert.Null(Environment.GetEnvironmentVariable(variable));
            }
        }

        try { Directory.Delete(root, true); } catch (Exception) { /* scratch */ }
    }
}
