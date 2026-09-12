# DeepSeek V4.1 on the direct-CUDA backend

Status: **runs end to end; early stages verified bit-exact against the managed
executor; full-model numerical parity not yet demonstrated.** This file records
exactly what was checked and what blocks the rest, so the next pass starts from
evidence rather than from scratch.

The direct-CUDA backend (`--backend cuda`) owns its own kernels and driver-API
plumbing and does not go through ggml. V4.1 support means: the trained cache
quantization, attention preparation without the query norm, the ratio-1/2
compressor, the shared compressed and indexer caches, candidate pruning, the
Engram gather and gate, and the delayed hyper-connection gates.

## What was added

* `tensorsharp_dsv4_kernels.cu` — eight V4.1 kernels: `ts_dsv41_attn_prep_f32`,
  `ts_dsv41_compress_f32`, `ts_dsv41_commit_f32`, `ts_dsv41_persist_f32`,
  `ts_dsv41_idx_prep_f32`, `ts_dsv41_idx_scores_f32`, `ts_dsv41_candidate_f32`,
  `ts_dsv41_engram_gate_f32`, plus the FP8/MXFP4/NVFP4 quantization the cache
  commits reproduce. Written out independently of any other backend.
* `Dsv4Kernels` — launchers, including a two-dimensional block form for the
  kernels that put one warp on the contracted dimension.
* `Dsv4CudaEngine` — `AttentionV41`, `CompressV41`, `BuildIndexerV41`,
  `EngramLayer`, the shared-cache sizing, the delayed hyper-connection gates and
  the V4.1 logits head.
* `IDsv41EngramSource` — the Engram tables stay host mappings (51.5 GiB each at
  Q4_K), so the executor that owns the GGUF hashes and dequantizes the selected
  rows and the engine only uploads them.
* `DeepSeek4CudaExecutor` — V4.1 hparams, the sidecar, the cache-sharing
  topology, the V4.1 layer tensors and the host-side gather.

## Testing it cheaply

`eng/dsv41-fixture.py` grew two options so the backend can be exercised without
the 415 GiB checkpoint:

* `--cuda-attn` — the 512-wide shared K(=V) head and 128-wide indexer the engine
  specializes to. Without it the engine refuses the fixture outright.
* `--q8` — every quantized weight as Q8_0 rather than Q2_K, because the CUDA MoE
  kernel takes Q8_0/Q6_K/Q2_K/IQ3_S/IQ2_XXS/MXFP4 but not F32.

`TS_DSV41_FIXTURE_CUDA=1` runs `Dsv41CpuExecutorTests.TheDirectCudaEngineMatchesTheSameOracle`;
`TS_DSV41_FIXTURE_ORACLE=csharp` compares against the managed executor's own
logits, dumped by `DumpsTheManagedExecutorsLogitsWhenAsked`.
`TS_DSV4_CUDA_TRACE_DIR` writes tensors under the same names
`TS_DSV4_CPU_TRACE_DIR` uses, so the two directories diff tensor by tensor.

## What the trace shows

Single token, layer 0 (compression ratio 0, so no compressor, indexer or
Engram), CUDA against the managed executor:

| tensor | max abs difference |
| --- | ---: |
| `embedding` | 0.0 |
| `blk00_attn_input` | 0.0 |
| `blk00_q` | 4.8e-07 |
| `blk00_raw_k` | 0.0 |
| `blk00_attn_out` | 7.4e-03 |

The K row is **bit-identical**, which is the strongest single result here: it
means the FP8 E4M3 path, the block scales, the RoPE on the tail and the F16
commit all agree with an implementation that is itself pinned to the PyTorch
reference.

From `blk00_attn_out` the difference grows smoothly across layers — 7.4e-3,
3.0e-2, 6.2e-2, 7.5e-2, 1.1e-1 — with **no jump at layer 1** (which carries the
Engram table and a ratio-2 compressor) or at **layer 3** (the candidate layer).
A wrong compressor, indexer or Engram would show as a step, not a ramp.

## What blocks a tight comparison

The two backends quantize ACTIVATIONS differently — the managed path uses
Q8_0/Q8_K, the CUDA path q8_1 blocks — so every matmul rounds differently. On a
five-layer model of random weights that compounds to the ramp above. The
fixture configuration where the tolerance is meaningful is `--f32`, and the CUDA
MoE kernel rejects F32 experts:

```
[dsv4-cuda] unsupported expert quant type 0 (supported: Q8_0, Q6_K, Q2_K, IQ3_S, IQ2_XXS, MXFP4)
```

So the next step is one of:

1. Accept F32 experts in the CUDA MoE kernel, purely so the F32 fixture runs —
   then the existing 2e-5 test applies unchanged.
2. Extend the trace comparison to every V4.1-specific tensor (`compress_latent`,
   `index_scores`, `engram_kv`, `engram_gate`) and assert per-stage rather than
   on logits.

Option 1 is the smaller change and gives a real pass/fail gate.

## Bugs found while getting this far

* The V4.1 quantized weights (`indexer.attn_k`, `engram_wkv`) were added to the
  descriptors but not to `EnumerateQuantWeights`, which both SIZES the weight
  arena and fills it — so the arena overflowed at load.
* The V4.1 out-projection passed position 0 to the inverse RoPE instead of the
  ubatch's `p0`, which would have un-rotated every token at position 0.
* The CUDA PTX is only rebuilt when `nvcc` is on PATH; otherwise the committed
  baseline is used and new kernels fail at load with "named symbol not found".
  On the A40 box that means `export PATH=/usr/local/cuda/bin:$PATH`.
