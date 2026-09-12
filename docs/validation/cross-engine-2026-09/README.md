# TensorSharp against llama.cpp and vLLM, September 2026

Eight NVIDIA A40s (46 GB each, compute capability 8.6, no NVLink, GPUs 0-2 on
one NUMA node and 3-7 on the other), 96 cores, 503 GB RAM, CUDA 12.8. Models are
GGUF files on a MooseFS network mount. Both engines are driven through their
OpenAI-compatible endpoint by one client, with the same prompts, at temperature
0.

Prefill is the engine's own prompt-token count divided by its time to first
token. Decode is the streamed-token rate after the first token. Reasoning tokens
count toward decode: GLM and Qwen are reasoning models, and llama.cpp streams
their whole answer in `reasoning_content` until the thinking block closes, so
counting only `content` would record zero tokens for a request that generated
hundreds.

## What each engine can run at all

Architecture strings read from the GGUF headers and checked against each
engine's registered architectures.

| Model | GGUF arch | TensorSharp | llama.cpp | vLLM |
|---|---|---|---|---|
| DeepSeek V4.1 Flash Q2_K | `deepseek41` | yes | no | no |
| GLM-5.3-Flash UD-Q2_K_XL | `glm5next` | yes | no | not evaluated |
| GLM-5.3 UD-Q2_K_XL | `glm-dsa` | yes | yes | not evaluated |
| Qwen3.8 Flash Next Q8_0 | `qwen4exp` | yes | yes | not evaluated |

**llama.cpp cannot run DeepSeek V4.1 Flash.** `git grep -i deepseek41` on master
(`8172e6577ac2`) returns nothing, and `LLM_ARCH_DEEPSEEK4` is V4: it is
registered from `DeepseekV4ForCausalLM` and its tensor list contains no Engram
tensor. The only V4.1 work upstream is PR #28696, an open draft that changes
three Python files and no C++, so it produces a `deepseek41` GGUF no released
binary can load. A separate Engram PR (#19654) was closed without merging.

**llama.cpp cannot run GLM-5.3-Flash either**, and says so precisely:
`llama_model_load: error loading model: unknown model architecture: 'glm5next'`.

**vLLM cannot run DeepSeek V4.1 Flash on this hardware, and the reason is
attention, not quantization.** Its V4.1 implementation has three CUDA attention
classes, gated to compute capability major 9 or 10 (FlashMLA sparse) and 10 or
12 (FlashInfer sparse). A40 is major 8, in neither set, and the FlashMLA kernels
are not compiled for 8.6, so the call raises at kernel-dispatch time. The fp8
and fp4 weight formats are not the obstacle: vLLM falls back to Marlin W8A16 and
W4A16 dequant kernels, which are supported from SM75. Memory would not have been
the obstacle either.

So on this hardware TensorSharp is the only engine that runs DeepSeek V4.1
Flash, and the only one that runs GLM-5.3-Flash. Those two rows are reported as
TensorSharp-only rather than as a comparison.

## Head to head

10,537-token prompt (each engine's own count is shown), 300 decode tokens,
three repeats, median. A fresh prompt body per repeat, so no repeat is served
from a prefix cache.

| Model | Engine | Prompt tok | Prefill tok/s | Decode tok/s | Load s |
|---|---|---:|---:|---:|---:|
| Qwen3.8 Flash Next Q8_0 | TensorSharp | 10,537 | **1243.4** | **43.08** | **116** |
| Qwen3.8 Flash Next Q8_0 | llama.cpp | 10,578 | 936.9 | 25.48 | 382 |
| GLM-5.3 UD-Q2_K_XL | TensorSharp | 10,531 | 251.6 | **20.48** | **264** |
| GLM-5.3 UD-Q2_K_XL | llama.cpp | not recorded | see below | 20.28 | 753 |

The first repeat of a cell can be cold: GLM-5.3-Flash returned 33.3, 50.1 and
50.1 tok/s, and an independent earlier run of the same cell returned 50.1, 50.6
and 50.6. Medians are reported for that reason, and they agree across runs.

**Qwen 3.8 Flash Next: TensorSharp is 1.33x on prefill and 1.69x on decode**, and
loads the 175 GiB checkpoint 3.3x faster.

**GLM-5.3: decode is a tie** (20.48 against 20.28, within a percent), and
TensorSharp loads the 236 GiB checkpoint 2.9x faster.

Prefill is where TensorSharp is behind on this model, and the comparison is by
time to first token rather than tokens per second, because that llama.cpp cell
ran before the client asked for usage in the stream and so has no prompt-token
count of its own. On the same prompt TensorSharp reaches first token in 41.9 s
(41.85, 41.86, 42.07) against llama.cpp's 29.0 s (29.01, 29.05, 31.23), about
1.4x slower. Dividing by TensorSharp's 10,531 tokens would put llama.cpp near
363 tok/s, but the engines tokenize slightly differently (llama.cpp read 10,578
tokens of the Qwen prompt where TensorSharp read 10,537), so that figure is not
recorded as measured. A re-measure of this one cell is queued.

This is the clearest actionable gap the comparison found.

Both engines use whole-layer placement across the eight GPUs here. TensorSharp
selects it with `--tp 1`; `--tp 8` for GLM means genuine tensor parallelism,
where every rank runs every layer, which needs 41.7 GiB per rank and does not
fit a 46 GB card. That is a real property of the mode, not a capacity limit of
the engine, and TensorSharp's load-time message names both the arithmetic and
the remedy.

## TensorSharp-only rows

| Model | Prompt tok | Prefill tok/s | Decode tok/s | Load s |
|---|---:|---:|---:|---:|
| GLM-5.3-Flash UD-Q2_K_XL | 10,537 | 718.0 | 50.07 | 480 |
| DeepSeek V4.1 Flash Q2_K | 4,924 | 536 | 41.1 | 302 |

The DeepSeek row uses a shorter prompt and its own harness; see
[the optimization report](../deepseek41/device-engram-and-scheduler/README.md)
for the before-and-after, which is 2.5x prefill and 1.44x decode against the
state this work started from.

## Reproducing

Model provenance, pinned revisions and per-file sizes are in
`benchmarks/engine_comparison/benchmark_config_glm53_qwen38.json`. The
llama.cpp build is 0.4.0-dev with CUDA over all eight devices.

The wider scenario matrix (JSON mode, function calling, agentic tool loops,
code generation and edit, parallel requests, routed-expert CPU offload, tensor
parallelism) runs from `benchmarks/engine_comparison/run_matrix.py`; see that
directory's README for the axes.
