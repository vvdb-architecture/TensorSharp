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
            // f16, not q8_0: Gemma 4 declines a block-quantized cache
            // (Gemma4Model.SupportsBlockQuantizedKvCache). Its sliding-window layers
            // use a circular cache whose managed helpers are float-only, and the 26B
            // MoE reaches them on an ordinary prompt: setting q8_0 here crashed with
            // "Requires a Float32 tensor, but found Q8_0" out of CopyToCacheCircular.
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            License = GemmaLicense,
            Notes = "Fastest option. Sees images and video frames, hears audio, thinks when asked.",
        },
        new CatalogModel
        {
            Id = "gemma-4-e4b-iq4xs",
            DisplayName = "Gemma 4 E4B",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "4B effective (8B with per-layer embeddings)",
            Quantization = "IQ4_XS",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gemma-4-E4B-it-IQ4_XS.gguf",
                    Hf("unsloth/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-IQ4_XS.gguf"),
                    4_715_416_704, "0847f7300471e9a61abaeb46b45b3d61e393af8fa6d6aa70c563623773670dd9"),
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
            Id = "gemma-4-12b-iq2m",
            DisplayName = "Gemma 4 12B",
            Family = CatalogFamily.Gemma4,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "12B",
            Quantization = "UD-IQ2_M",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gemma-4-12b-it-UD-IQ2_M.gguf",
                    Hf("unsloth/gemma-4-12b-it-GGUF", "gemma-4-12b-it-UD-IQ2_M.gguf"),
                    4_213_353_280, "4bd2461d35398dbcf5f3d5f0c9ad91cac78ae35b556e3a81f315a0cc0815ae8c"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/gemma-4-12b-it-GGUF", "mmproj-F16.gguf"),
                    175_115_840, "91f086971e56d7a7d8d39e271873fccdb49541bd259d6e02c401a4f1cb7a219e", Optional: true),
                // The per-token assistant head, the same shape gemma-4-e4b-iq4xs carries.
                new CatalogFile(CatalogFileRole.Draft, "mtp-gemma-4-12b-it.gguf",
                    Hf("unsloth/gemma-4-12b-it-GGUF", "mtp-gemma-4-12b-it.gguf"),
                    465_109_248, "145db9094bc0f85f1701e255a2ed216dcc9800fc8bc8631ad00905b456bd451b", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            // 32768, not 8192. The window was small because the LOAD was expensive, not
            // because the cache was: fusing this model's 48 ffn_gate/ffn_up pairs cost
            // gigabytes of anonymous memory duplicating bytes already mapped from the
            // GGUF, which is what jetsam killed the app for. Gemma 4 now runs the pair
            // as two matmuls instead (Gemma4Model.SupportsSplitGateUpFfn), and the
            // window is affordable. MEASURED on the Q4_K_XL build of this same model,
            // ggml_metal, where the fusion was 3,108 MB:
            //   fused,  8192   peak footprint 4,534 MB   31.7 tok/s
            //   split,  8192                   1,423 MB   31.5 tok/s
            //   split, 32768                   2,187 MB   31.3 tok/s
            // Output is byte-identical between the first two, and a 14,294-token needle
            // prompt returns the planted value on both. THIS entry is the UD-IQ2_M
            // build, and it reaches the split path by the same route the measurement
            // above did: on iOS ModelBase.AllowWeightFusionCopies is false, so the
            // fused copy is never made whatever the types are. Nothing here depends on
            // ggml declining to requantize -- VERIFIED against the pinned file, whose
            // 48 ffn_gate/ffn_up pairs match on BOTH sides (43 IQ2_S pairs and five
            // IQ3_XXS pairs), so the mismatch branch that would call for a requantize
            // is never entered at all.
            ContextLength = 32768,
            // f16, like every Gemma entry: Gemma 4 refuses a block-quantized cache
            // (Gemma4Model.SupportsBlockQuantizedKvCache) because its sliding-window
            // layers use a circular cache whose managed helpers are float-only.
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            License = GemmaLicense,
            Notes = "The dense Gemma between E4B and the 26B mixture of experts, in the 2.7 bpw IQ2_M recipe. "
                + "4.2 GB of weights, mapped from the file rather than copied, so the model itself "
                + "costs the phone almost nothing and the 32k window is the larger part of its "
                + "footprint. Vision and a speculative draft head are optional downloads.",
        },
        new CatalogModel
        {
            Id = "qwen3.5-9b-iq4xs",
            DisplayName = "Qwen3.5 9B",
            Family = CatalogFamily.Qwen35,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "9B",
            Quantization = "IQ4_XS",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "Qwen3.5-9B-IQ4_XS.gguf",
                    Hf("unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-IQ4_XS.gguf"),
                    5_168_653_536, "7e918aeca06c52bcb528ea6b04b4ec957e75ee8c0a73138854c0dfcf371ea429"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/Qwen3.5-9B-GGUF", "mmproj-F16.gguf"),
                    918_166_080, "f70dc3509053962b0d0d3ee8a7eacebf5d60aa560cad78254ae8698516ae029f", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            // 32768, not 8192: the reply-length setting is bounded by the CONTEXT
            // (ChatGenerationPipeline.ClampGenerationReserve trims the generation
            // reserve to what the window leaves after the prompt, and the thinking
            // budget is 75% of THAT), so an 8192 window capped a reply at ~7.7k
            // tokens however high the user set the limit -- and a reasoning model
            // that spent it produced no answer at all. A quantized cache pays for
            // the bigger window: MEASURED on Qwen3.5-9B UD-Q4_K_XL, ggml_metal,
            // peak physical footprint is 1697 MB at q8_0/32768 against the 1118 MB
            // that f16/8192 already cost, and q8_0/16384 (1152 MB) is a wash.
            ContextLength = 32768,
            // q8_0, not f16: the KV cache is the only thing that grows with the
            // conversation, and on Metal it is charged twice. MEASURED on ggml_metal
            // after the fused graphs learned block-quantized K/V: 22.4 KiB/token
            // against f16's 41.8, at decode parity (Qwen3.6-35B-A3B 75.5 vs 76.0
            // tok/s, within noise), with a two-needle recall test at 7,490 tokens
            // returning both planted values. Qwen3.5/3.6 take the fused graph, whose
            // native side is dtype-generic; Gemma 4 does not and stays on f16.
            KvCacheDtype = "q8_0",
            Sampling = new CatalogSampling(0.7f, 20, 0.8f, 0.0f),
            SupportsThinking = true,
            License = ApacheLicense,
            Notes = "Strong general model with vision; the projector is optional and costs ~1.8 GB of memory when loaded.",
        },
        new CatalogModel
        {
            Id = "qwen3.8-27b-iq1s",
            DisplayName = "Qwen3.8 27B",
            Family = CatalogFamily.Qwen38,
            Kind = CatalogArchitectureKind.Dense,
            Parameters = "27B",
            Quantization = "UD-IQ1_S",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "Qwen3.8-27B-UD-IQ1_S.gguf",
                    Hf("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8-27B-UD-IQ1_S.gguf"),
                    6_192_222_208, "3895b6eaa91e705c06ad1938d16c22e86f073c6a67df86260a1da79be3d1f887"),
                new CatalogFile(CatalogFileRole.Projector, "mmproj-F16.gguf",
                    Hf("unsloth/Qwen3.8-27B-GGUF", "mmproj-F16.gguf"),
                    927_607_488, "cbb841a9ee0636b2ec172f5bb8df2ea8dfeb01e90fe7c6126581d662a0b4e43e", Optional: true),
            },
            Modalities = CatalogModalities.Image | CatalogModalities.Video,
            MinDeviceMemoryGB = 12,
            // 32768, not 8192: the reply-length setting is bounded by the CONTEXT
            // (ChatGenerationPipeline.ClampGenerationReserve trims the generation
            // reserve to what the window leaves after the prompt, and the thinking
            // budget is 75% of THAT), so an 8192 window capped a reply at ~7.7k
            // tokens however high the user set the limit -- and a reasoning model
            // that spent it produced no answer at all. A quantized cache pays for
            // the bigger window: MEASURED on Qwen3.5-9B UD-Q4_K_XL, ggml_metal,
            // peak physical footprint is 1697 MB at q8_0/32768 against the 1118 MB
            // that f16/8192 already cost, and q8_0/16384 (1152 MB) is a wash.
            // 16384 and not 32768 like the hybrids: this one is DENSE, so all 64 layers
            // hold a KV cache and a token costs 68 KiB even at q8_0. MEASURED on
            // ggml_metal: 1820 MB at 8192, 3455 MB at 32768 -- over half the ~6.4 GB a
            // 12 GB phone grants, on top of weights already at 81% of the Metal
            // working-set ceiling. 16384 buys double the window for a third of that.
            ContextLength = 16384,
            KvCacheDtype = "q8_0",
            Sampling = new CatalogSampling(0.7f, 20, 0.8f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = ApacheLicense,
            Notes = "The only Qwen 3.8 that fits a phone, at 1 bit -- 6.2 GB for 27B parameters, "
                + "which is a real quality cost rather than a rounding one. Try it against the "
                + "Qwen3.5 9B at 4 bits before keeping it. Text only unless the projector is "
                + "downloaded; expect slow decoding.",
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
            MinDeviceMemoryGB = 12,
            ContextLength = 4096,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 64, 0.95f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = GemmaLicense,
            Notes = "Mixture of experts, 9.9 GB of weights. Offered to a 12 GB phone because the "
                + "weights are a FILE MAPPING and Darwin charges mapped clean pages almost nothing: "
                + "measured on Metal, a same-family 11.3 GB MoE peaked at 1.21 GB resident / 1.09 GB "
                + "physical footprint against the ~8.5 GB a 12 GB phone grants; this entry itself "
                + "measured 1,691 MB at 4096 tokens and decoded at 70 tok/s on an M5 Pro. What a phone this size "
                + "cannot do is hold 9.9 GB in the page cache, so expect every token to fault expert "
                + "weights from flash and decode far below a desktop's. Only 8 of 128 experts run per "
                + "token, which is what makes that survivable at all.",
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
            MinDeviceMemoryGB = 12,
            // 32768, not 8192: the reply-length setting is bounded by the CONTEXT
            // (ChatGenerationPipeline.ClampGenerationReserve trims the generation
            // reserve to what the window leaves after the prompt, and the thinking
            // budget is 75% of THAT), so an 8192 window capped a reply at ~7.7k
            // tokens however high the user set the limit -- and a reasoning model
            // that spent it produced no answer at all. A quantized cache pays for
            // the bigger window: MEASURED on Qwen3.5-9B UD-Q4_K_XL, ggml_metal,
            // peak physical footprint is 1697 MB at q8_0/32768 against the 1118 MB
            // that f16/8192 already cost, and q8_0/16384 (1152 MB) is a wash.
            ContextLength = 32768,
            KvCacheDtype = "q8_0",
            Sampling = new CatalogSampling(0.7f, 20, 0.8f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = ApacheLicense,
            Notes = "The nearest Qwen mixture of experts that fits any Apple device (Qwen 3.8's MoE "
                + "checkpoints start at 72 GB). 10.0 GB of weights, offered to a 12 GB phone for the "
                + "same measured reason as the Gemma MoE: mapped weights cost almost no physical "
                + "footprint (11.3 GB of this family measured at 1.09 GB), and only 3B of 35B "
                + "parameters are active per token. At IQ1_M this is a one-bit quantization -- try it "
                + "against the 9B at Q4 before keeping it, and expect flash paging to dominate decode.",
        },
        new CatalogModel
        {
            Id = "gpt-oss-20b-q8",
            DisplayName = "GPT-OSS 20B (MoE)",
            Family = CatalogFamily.GptOss,
            Kind = CatalogArchitectureKind.MixtureOfExperts,
            Parameters = "20B (3.6B active, 32 experts)",
            // Q8_0 rather than a smaller quant because for THIS model it is nearly
            // free: the experts ship as MXFP4 and every published quant keeps them
            // that way, so the whole Q2_K..Q8_0 range spans 11.5 GB to 12.1 GB -- the
            // quantization only touches the ~1.9B dense parameters. Paying 0.6 GB for
            // the best attention and embedding weights available is the obvious trade.
            Quantization = "Q8_0 (MXFP4 experts)",
            Files = new[]
            {
                new CatalogFile(CatalogFileRole.Weights, "gpt-oss-20b-Q8_0.gguf",
                    Hf("unsloth/gpt-oss-20b-GGUF", "gpt-oss-20b-Q8_0.gguf"),
                    12_109_567_168, "bcd455d4034ec02f71a875b46cb17df44a97911d7258291973be4d21f98329f3"),
            },
            Modalities = CatalogModalities.Text,
            // 16, not 12, and this is the one number in the entry that was decided ON A
            // PHONE rather than on a laptop. MEMORY is not the constraint -- the load
            // costs 1.2 GB and the app is nowhere near jetsam. STORAGE READ BANDWIDTH
            // is: 11.5 GB of expert weights are file-backed, a 12 GB device cannot hold
            // that in its page cache, and every token faults some of it back from flash.
            // MEASURED on a 12 GB iPhone 17 Pro Max: loads in 2.1 s, then did not finish
            // answering "Say hello." in six minutes, while Gemma 4 12B answers the same
            // prompt on the same phone in seconds. Nothing about the entry can fix that:
            // every published gpt-oss-20b GGUF is 11.4 GB or larger, because the experts
            // are MXFP4 in all of them, so there is no smaller build to offer instead.
            // It is a good model on a 16 GB iPad and an unusable one on this phone.
            MinDeviceMemoryGB = 16,
            // 32768: this is a reasoning model, and the reply length is bounded by the
            // CONTEXT (ChatGenerationPipeline.ClampGenerationReserve trims the
            // generation reserve to what the window leaves, and the thinking budget is
            // 75% of THAT), so a small window is a model that thinks and never answers.
            // MEASURED, ggml_metal, peak physical footprint at 128 decoded tokens:
            //   8192  1,222 MB    16384  1,606 MB    32768  2,377 MB
            // all at 81 tok/s, which is what makes the largest of the three the right
            // one to take.
            ContextLength = 32768,
            // f16 is the only choice: both fused GPT-OSS graphs refuse a
            // block-quantized cache outright and the managed fallback walks the cache
            // as a flat float buffer (GptOssModel.SupportsBlockQuantizedKvCache).
            KvCacheDtype = "f16",
            // The model card's own numbers: temperature 1.0 with nucleus sampling
            // disabled. Harmony's analysis channel is where the reasoning happens and
            // it is not improved by truncating the tail.
            Sampling = new CatalogSampling(1.0f, 0, 1.0f, 0.0f),
            SupportsThinking = true,
            Experimental = true,
            License = ApacheLicense,
            Notes = "OpenAI's open-weight reasoning model, with its native MXFP4 experts. Only "
                + "3.6B of its 20B parameters run per token and the weights are mapped rather "
                + "than copied, so it costs about 2.4 GB of memory -- but it is 12.1 GB on disk, "
                + "and a device that cannot keep that in its page cache re-reads the experts from "
                + "flash for every token. Measured unusable on a 12 GB iPhone for that reason, "
                + "which is why this needs 16 GB. Text only.",
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
            MinDeviceMemoryGB = 24,
            ContextLength = 0,
            KvCacheDtype = "f16",
            Sampling = new CatalogSampling(1.0f, 0, 1.0f, 0.0f),
            Experimental = true,
            License = ApacheLicense,
            Notes = "Image editing from a picture plus a prompt. MEASURED at 16.0 GB of Metal allocation for one edit with this Q2_K DiT -- the smallest published -- against the ~11.3 GB a 16 GB iPhone grants one app, so it does not fit any current iPhone and is not offered on one. Reducing the output area saves about 1.2 GB per halving and the CPU-offload path does not lower allocation on unified memory, so neither closes the gap. It runs correctly where the memory exists.",
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
