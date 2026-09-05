// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

namespace TensorAgent.Core.Catalog;

/// <summary>What a file in a catalog entry is for. The app loads the weights and hands the
/// companions to the engine by role (the projector to <c>--mmproj</c>, the Qwen-Image
/// companions to their environment variables).</summary>
public enum CatalogFileRole
{
    /// <summary>The GGUF the model is loaded from.</summary>
    Weights,
    /// <summary>Vision/audio projector (mmproj) for a multimodal model.</summary>
    Projector,
    /// <summary>Qwen-Image text encoder GGUF.</summary>
    TextEncoder,
    /// <summary>Qwen-Image vision mmproj (image-grounded conditioning).</summary>
    VisionProjector,
    /// <summary>Qwen-Image VAE safetensors.</summary>
    Vae,
    /// <summary>Step-distillation LoRA safetensors.</summary>
    Lora,
    /// <summary>Speculative-decoding draft head.</summary>
    Draft,
}

/// <summary>One downloadable artifact of a catalog entry.</summary>
/// <param name="Role">What the engine uses it for.</param>
/// <param name="FileName">The name it is stored under, inside the entry's folder.</param>
/// <param name="Url">Where it is fetched from (a plain Hugging Face resolve URL).</param>
/// <param name="Bytes">Exact size, from the Hugging Face tree API.</param>
/// <param name="Sha256">Lower-case hex SHA-256 (the LFS object id), verified after download.</param>
/// <param name="Optional">True when the model works without it (e.g. a draft head).</param>
public sealed record CatalogFile(
    CatalogFileRole Role,
    string FileName,
    string Url,
    long Bytes,
    string Sha256,
    bool Optional = false);

/// <summary>Model families the catalog knows; used for grouping in the UI and for
/// family-specific defaults (thinking, sampling).</summary>
public enum CatalogFamily { Gemma4, Qwen35, Qwen36, Qwen38, QwenImage, GptOss }

/// <summary>Dense or mixture-of-experts.</summary>
public enum CatalogArchitectureKind { Dense, MixtureOfExperts, Diffusion }

/// <summary>Input/output modalities an entry supports once its projector is installed.</summary>
[Flags]
public enum CatalogModalities
{
    Text = 0,
    Image = 1,
    Audio = 2,
    Video = 4,
    ImageOutput = 8,
}

/// <summary>Sampling defaults the model card recommends; the app sends them with every
/// chat request the way the Web UI sends the server's configured defaults.</summary>
public sealed record CatalogSampling(float Temperature, int TopK, float TopP, float MinP);

/// <summary>
/// A built-in model the user can pick. Everything the app needs to download, size and
/// load it lives here so the catalog is data, not code: the UI shows it, the store
/// downloads it, the engine host turns it into load arguments.
/// </summary>
public sealed record CatalogModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required CatalogFamily Family { get; init; }
    public required CatalogArchitectureKind Kind { get; init; }
    /// <summary>Human-readable parameter count, e.g. "4B effective" or "26B (4B active)".</summary>
    public required string Parameters { get; init; }
    public required string Quantization { get; init; }
    public required IReadOnlyList<CatalogFile> Files { get; init; }
    public required CatalogModalities Modalities { get; init; }
    /// <summary>Smallest device memory class this entry is offered on. Weights are wired by
    /// Metal, so resident memory is roughly the GGUF size plus KV cache, the F32 projector
    /// and compute buffers; the class keeps a model that cannot fit off the picker.</summary>
    public required int MinDeviceMemoryGB { get; init; }
    /// <summary>Context length the app configures (MAX_CONTEXT); bounds the KV cache.</summary>
    public required int ContextLength { get; init; }
    /// <summary>KV cache dtype to request ("f16", "q8_0"); block-quantised caches halve KV memory
    /// where the family's fused paths accept them.</summary>
    public required string KvCacheDtype { get; init; }
    public required CatalogSampling Sampling { get; init; }
    /// <summary>Whether the family has a thinking channel the app may enable.</summary>
    public bool SupportsThinking { get; init; }
    /// <summary>Marked in the UI: fits only with reduced context or has not been validated on
    /// a phone yet.</summary>
    public bool Experimental { get; init; }
    public string? Notes { get; init; }
    public required string License { get; init; }

    public long TotalBytes => Files.Where(f => !f.Optional).Sum(f => f.Bytes);
    public long TotalBytesWithOptional => Files.Sum(f => f.Bytes);
    public CatalogFile Weights => Files.First(f => f.Role == CatalogFileRole.Weights);
    public CatalogFile? Projector => Files.FirstOrDefault(f => f.Role == CatalogFileRole.Projector);
    public bool IsImageGenerator => Family == CatalogFamily.QwenImage;
}
