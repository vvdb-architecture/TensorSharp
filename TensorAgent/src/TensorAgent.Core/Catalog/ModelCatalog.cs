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

/// <summary>
/// The built-in model list. Sizes and hashes were read from the Hugging Face tree API
/// (LFS object ids) on 2026-09-01, so a download is verified against the exact bytes
/// the publisher uploaded.
///
/// <para>
/// Sizing rule (why these quantizations): on GGML Metal the quantized weights are wrapped
/// as MTLBuffers straight over the GGUF mmap, and Metal keeps those pages resident, so
/// the resident set is about the file size plus the KV cache, the projector (materialised
/// as F32, so roughly twice its file size) and ~0.5 GB of compute buffers. An iPhone with
/// 12 GB grants an app with the increased-memory entitlement roughly 8.5 GB, which is why
/// the 12 GB tier tops out around 7.5 GB of weights and everything larger is offered only
/// to 16 GB iPads.
/// </para>
/// <para>
/// Every Qwen 3.8 mixture-of-experts checkpoint (Flash-Next is 180B parameters, 72 GB at
/// 1-bit) and every Gemma 4 MoE quant under 9.9 GB does not exist, so the MoE entries here
/// are the smallest published builds and are gated to 16 GB devices; the dense Qwen 3.8
/// 27B fits a 12 GB phone only at 2-bit and is marked experimental.
/// </para>
/// </summary>
public static class ModelCatalog
{
    private const string GemmaLicense = "Gemma Terms of Use";
    private const string ApacheLicense = "Apache-2.0";

    private static string Hf(string repo, string file) => $"https://huggingface.co/{repo}/resolve/main/{file}";

    public static IReadOnlyList<CatalogModel> BuiltIn { get; } = new[]
    {
        new CatalogModel
        {
            Id = "gemma-4-e2b-q8",
            DisplayName = "Gemma 4 E2B",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "2B effective (5B with per-layer embeddings)",
            Quantization = "Q8_0",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gemma-4-E2B-it-Q8_0.gguf",
                    Hf("ggml-org/gemma-4-E2B-it-GGUF", "gemma-4-E2B-it-Q8_0.gguf"),
                    4_967_497_152, "996d08777aadc6bfd3c7375ef70ba25a0f55240075860754fdb18d6d860aa63a"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-gemma-4-E2B-it-Q8_0.gguf",
                    Hf("ggml-org/gemma-4-E2B-it-GGUF", "mmproj-gemma-4-E2B-it-Q8_0.gguf"),
                    557_368_064, "9406f99c16d68cda4f1f0552192dcc99021ea1fc6d2fd50b1dc3ccf30d04b292"),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Audio | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            ContextLength = 8192,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            License = GemmaLicense,
            Notes = "Fastest option. Sees images and video frames, hears audio, thinks when asked.",
        },
        new CatalogModel
        {
            Id = "gemma-4-e4b-q4kxl",
            DisplayName = "Gemma 4 E4B",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "4B effective (8B with per-layer embeddings)",
            Quantization = "UD-Q4_K_XL",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gemma-4-E4B-it-UD-Q4_K_XL.gguf",
                    Hf("unsloth/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-UD-Q4_K_XL.gguf"),
                    5_126_306_944, "3cf61de12daa015ee0f7b68e7b7c541405bf220e1e942bad8b47cab827d7df80"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-gemma-4-E4B-it-Q8_0.gguf",
                    Hf("ggml-org/gemma-4-E4B-it-GGUF", "mmproj-gemma-4-E4B-it-Q8_0.gguf"),
                    559_874_816, "197f49a93027f9843772bd24a6a9e0be2a32a788de5a3def330e9c585d86edd1"),
                new CatalogFile(CatalogFileRole.Draft, "mtp-gemma-4-E4B-it-Q8_0.gguf",
                    Hf("ggml-org/gemma-4-E4B-it-GGUF", "mtp-gemma-4-E4B-it-Q8_0.gguf"),
                    98_653_280, "f38ae62962657c7a6303c49bbb147e9ae23634e911cfa532fac0818c2e18b665", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Audio | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            ContextLength = 8192,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            License = GemmaLicense,
            Notes = "The recommended default: TensorSharp's verified fast-path tier, multimodal, with an optional speculative draft head.",
        },
        new CatalogModel
        {
            Id = "gemma-4-e4b-q8",
            DisplayName = "Gemma 4 E4B (high quality)",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "4B effective (8B with per-layer embeddings)",
            Quantization = "Q8_0",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gemma-4-E4B-it-Q8_0.gguf",
                    Hf("ggml-org/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q8_0.gguf"),
                    8_031_242_688, "34be82b17b4942d389b9b527170c4b058027abdd32531fda063d3d97dd8ce80a"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-gemma-4-E4B-it-Q8_0.gguf",
                    Hf("ggml-org/gemma-4-E4B-it-GGUF", "mmproj-gemma-4-E4B-it-Q8_0.gguf"),
                    559_874_816, "197f49a93027f9843772bd24a6a9e0be2a32a788de5a3def330e9c585d86edd1"),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Audio | CatalogModalities.Video,
            MinDeviceMemoryGB = 16,
            ContextLength = 8192,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            License = GemmaLicense,
            Notes = "8 GB of weights: iPad Pro (16 GB) only.",
        },
        new CatalogModel
        {
            Id = "qwen3.5-9b-q4kxl",
            DisplayName = "Qwen3.5 9B",
            Family = CatalogFamily.Qwen35,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "9B",
            Quantization = "UD-Q4_K_XL",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "Qwen3.5-9B-UD-Q4_K_XL.gguf",
                    Hf("unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-UD-Q4_K_XL.gguf"),
                    5_966_095_584, "6f5d30666c2d8ae16a306e616d95341dcf3cc46810df84d7e6f5a7d1e4c1b293"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/Qwen3.5-9B-GGUF", "mmproj-F16.gguf"),
                    918_166_080, "f70dc3509053962b0d0d3ee8a7eacebf5d60aa560cad78254ae8698516ae029f", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            ContextLength = 8192,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(0.7f, 20, 0.8f, 0.0f),
            SupportsThinking = true,
            License = ApacheLicense,
            Notes = "Strong general model with vision; the projector is optional and costs ~1.8 GB of memory when loaded.",
        },
        new CatalogModel
        {
            Id = "qwen3.8-27b-iq2xxs",
            DisplayName = "Qwen3.8 27B",
            Family = CatalogFamily.Qwen38,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "27B",
            Quantization = "UD-IQ2_XXS",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "Qwen3.8-27B-UD-IQ2_XXS.gguf",
                    Hf("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8-27B-UD-IQ2_XXS.gguf"),
                    7_266_070_528, "e792d8fb3142fe6d9171876d6da0f71f05a71028718debc72dbec93ff645e67d"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/Qwen3.8-27B-GGUF", "mmproj-F16.gguf"),
                    927_607_488, "cbb841a9ee0636b2ec172f5bb8df2ea8dfeb01e90fe7c6126581d662a0b4e43e", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            ContextLength = 4096,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(0.7f, 20, 0.8f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = ApacheLicense,
            Notes = "The only Qwen 3.8 that fits a 12 GB phone, at 2 bits. Text only unless the projector is downloaded; expect slow decoding.",
        },
        new CatalogModel
        {
            Id = "gemma-4-26b-a4b-iq2xxs",
            DisplayName = "Gemma 4 26B-A4B (MoE)",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.MixtureOfExperts,
            Parameters = "26B (4B active, 128 experts)",
            Quantization = "UD-IQ2_XXS",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gemma-4-26B-A4B-it-UD-IQ2_XXS.gguf",
                    Hf("unsloth/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-UD-IQ2_XXS.gguf"),
                    9_922_480_608, "52f98e6fc6df62438dff8f57ac049f60b4c36acf93786b6aeeedc129acc02343"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/gemma-4-26B-A4B-it-GGUF", "mmproj-F16.gguf"),
                    1_193_058_784, "418a6d8723067cd712235facbbc5cba6c8fbbd413fc1292d2aace5a027d5a42f", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 16,
            ContextLength = 4096,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = GemmaLicense,
            Notes = "Mixture of experts; 9.9 GB of weights, so 16 GB iPads only.",
        },
        new CatalogModel
        {
            Id = "qwen3.6-35b-a3b-iq1m",
            DisplayName = "Qwen3.6 35B-A3B (MoE)",
            Family = CatalogFamily.Qwen36,
            Kind = CatalogArchitectureKind.MixtureOfExperts,
            Parameters = "35B (3B active, 256 experts)",
            Quantization = "UD-IQ1_M",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "Qwen3.6-35B-A3B-UD-IQ1_M.gguf",
                    Hf("unsloth/Qwen3.6-35B-A3B-GGUF", "Qwen3.6-35B-A3B-UD-IQ1_M.gguf"),
                    10_047_749_088, "0dc2488c89d916c5599f7c03a286cd8f37a6a75a02bc13caf41c6bac26d70c9e"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/Qwen3.6-35B-A3B-GGUF", "mmproj-F16.gguf"),
                    899_283_680, "8971ee4f331ff0a4c609374f32984b3d4e6dc086c0aa35f1d637fad1829e887f", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 16,
            ContextLength = 4096,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(0.7f, 20, 0.8f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = ApacheLicense,
            Notes = "The nearest Qwen mixture of experts that fits any Apple device (Qwen 3.8's MoE checkpoints start at 72 GB). 16 GB iPads only.",
        },
        new CatalogModel
        {
            Id = "qwen-image-edit-2511-q2k",
            DisplayName = "Qwen-Image-Edit 2511",
            Family = CatalogFamily.QwenImage,
            Kind = CatalogArchitectureKind.Diffusion,
            Parameters = "20B DiT + 7B text encoder",
            Quantization = "Q2_K (DiT) / IQ2_XXS (text encoder)",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "qwen-image-edit-2511-Q2_K.gguf",
                    Hf("unsloth/Qwen-Image-Edit-2511-GGUF", "qwen-image-edit-2511-Q2_K.gguf"),
                    7_468_022_368, "a3d09042b64657970654941aa08d895de29b4d98edf3632a89e70d4d6e23c47c"),
                new CatalogFile(CatalogFileRole.TextEncoder, "Qwen2.5-VL-7B-Instruct-UD-IQ2_XXS.gguf",
                    Hf("unsloth/Qwen2.5-VL-7B-Instruct-GGUF", "Qwen2.5-VL-7B-Instruct-UD-IQ2_XXS.gguf"),
                    2_398_444_416, "9fdde01492c884464ec3713aa02993c7b56711ee392904f2a53dd92cbe9f1967"),
                new CatalogFile(CatalogFileRole.Vae, "Qwen_Image-VAE.safetensors",
                    Hf("QuantStack/Qwen-Image-Edit-GGUF", "VAE/Qwen_Image-VAE.safetensors"),
                    253_806_246, "a70580f0213e67967ee9c95f05bb400e8fb08307e017a924bf3441223e023d1f"),
                new CatalogFile(CatalogFileRole.Lora, "Qwen-Image-Edit-2511-Lightning-4steps-V1.0-bf16.safetensors",
                    Hf("lightx2v/Qwen-Image-Edit-2511-Lightning", "Qwen-Image-Edit-2511-Lightning-4steps-V1.0-bf16.safetensors"),
                    849_608_296, "22226e8d05d354bb356627d428809f5afd7819399b077238a2b70a82883a904f"),
                // Stored under the family's own name rather than the repository's bare
                // "mmproj-BF16.gguf": QwenImageModel finds the projector by scanning for a
                // GGUF whose name says both "mmproj" and which family it belongs to, and a
                // file called only "mmproj-BF16.gguf" downloads, verifies and is then never
                // looked at, leaving the edit with no image grounding and no complaint.
                new CatalogFile(CatalogFileRole.VisionProjector, "Qwen2.5-VL-7B-Instruct-mmproj-BF16.gguf",
                    Hf("unsloth/Qwen2.5-VL-7B-Instruct-GGUF", "mmproj-BF16.gguf"),
                    1_354_163_040, "f0edf43c09b69d6e5dd24262f33b356a1e9dd978e7c3299b3e69141fcbb87553", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.ImageOutput,
            MinDeviceMemoryGB = 12,
            ContextLength = 0,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 0, 1.0f, 0.0f),
            Experimental = true,
            License = ApacheLicense,
            Notes = "Image editing and generation from a picture plus a prompt, 4 Lightning steps at 512 px. The networks are loaded one at a time to fit; expect a minute or more per image.",
        },
    };

    public static CatalogModel? Find(string id) =>
        BuiltIn.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The entries a device with <paramref name="physicalMemoryGB"/> of RAM is offered.</summary>
    public static IReadOnlyList<CatalogModel> ForDevice(int physicalMemoryGB) =>
        BuiltIn.Where(m => m.MinDeviceMemoryGB <= physicalMemoryGB).ToList();

    /// <summary>
    /// Rounds a reported physical-memory figure (iOS reports slightly under the marketing
    /// number, e.g. 11.6 GB for a "12 GB" phone) to the marketing tier used by
    /// <see cref="CatalogModel.MinDeviceMemoryGB"/>.
    /// </summary>
    public static int DeviceMemoryTier(long physicalMemoryBytes)
    {
        double gb = physicalMemoryBytes / 1_000_000_000.0;
        int[] tiers = { 4, 6, 8, 12, 16, 24, 32, 48, 64, 128 };
        int best = tiers[0];
        foreach (int t in tiers)
        {
            if (gb + 0.75 >= t)
                best = t;
        }
        return best;
    }
}
