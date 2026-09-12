# DeepSeek V4.1 Flash: Q4_K_M on 8x A40

What was run, what it measured, and the one thing that had to change. Every
number below is from `/root/dsv41-opt` on the 8x A40 box (46,068 MiB per card,
no NVLink, GPUs 0-2 and 3-7 on separate NUMA nodes).

Checkpoint: `vcruz305/DeepSeek-V4.1-Flash-GGUF`, `Q4_K_M`, 11 shards, 415 GiB.
Architecture `deepseek41`, 40 blocks, 384 experts. The Engram sidecar is not in
that repository and was regenerated with `eng/dsv41-prepare.py`, which reads the
tokenizer and config from `deepseek-ai/DeepSeek-V4.1-Flash` and writes the same
517,896-byte `deepseek41.engram.bin` the Q2_K checkpoint used.

## Placement

The two Engram tables are `Q4_K` at 51.5 GiB each. Neither fits a 46 GB card, so
the loader keeps both as host mappings and the graph reads rows over the bus:

```
[dsv41] Engram tables stay host mappings: they do not fit these devices even with every routed expert on the host
```

That leaves 311.2 GiB of weights for 353.3 GiB of free VRAM, which looks like it
fits and does not.

## The issue this found

With the default 2 GiB per-device reserve the model loads (248 s, all 40 layers
on GPUs) and answers short prompts correctly, then **aborts on a long prompt**:

```
CUDA error: out of memory
  current device: 7, in function alloc at ggml-cuda.cu:588
  ggml_cuda_pool_vmm::alloc <- argsort_f32_i32_cuda_cub <- ggml_cuda_op_top_k
```

The lightning indexer's top-k sorts every visible compressed row, and CUB takes
its workspace from the CUDA VMM pool at run time — it is not part of any layer's
weight budget, so the split never priced it. At a 1024-token ubatch over a 64k
context that one transient is about 768 MiB per device.

**Fix**: the per-device reserve now scales with the ubatch graph instead of being
a flat 2 GiB (`ggml_ops_deepseek4.cpp`, the `dev_budget` block). It prices the
indexer's scores and sort workspace plus one ubatch of hidden activations across
the hyper-connection streams, and prints what it held back:

```
[dsv4] VRAM reserve: N MiB per device (indexer X x4 + activations Y + 2048 headroom)
```

The factor on the indexer term is deliberately generous. CUB's segmented argsort
workspace is not a published function of its input, and the graph has other
transients that scale with the ubatch. Reserving too much costs a `--n-cpu-moe`
suggestion that the loader prints by name; reserving too little costs an abort in
the middle of a request, after the model has loaded and answered a short prompt.
`TS_DSV4_VRAM_RESERVE_MB` still overrides in both directions.

Verified after the change. The same command line that used to load all 40 layers
onto the GPUs and then abort now holds back 5,240 MiB per device and refuses at
load, naming what to do:

```
[dsv4] VRAM reserve: 5240 MiB per device (indexer 768 x4 + activations 120 + 2048 headroom)
[dsv41] Engram tables stay host mappings: they do not fit these devices even with every routed expert on the host
[dsv4] not enough VRAM: 311.2 GiB of weights plus this context's KV caches against
353.3 GiB free across 8 device(s). Re-run with --n-cpu-moe 3 (moves the routed
experts of the first 3 layer(s), 24.6 GiB, to system RAM) or --cpu-moe to offload every layer.
```

An actionable refusal at load, rather than a model that answers a short prompt
and then aborts on a long one. Re-run with the flag it names and the prompt that
used to abort goes through:

```
[dsv4] routed-expert CPU offload: 3 of 40 layer(s); 37 layer(s) on GPUs
load_s 400.4
  short_prompt   ok=True  ttft=0.44s    decode=18.0 tok/s  answer names Paris
  long_prompt    ok=True  ttft=170.84s  decode=16.2 tok/s  ~4000 row prompt
```

## Serving configuration that works

```
--backend ggml_cuda --tp 8 --n-cpu-moe 8
TS_DSV4_UBATCH=512 TS_SCHED_PREFILL_CHUNK=512
```

8 of 40 layers' routed experts on the host (166.5 GiB host-mapped), 32 layers on
GPUs. Load: 224.4 s.

## Scenarios

All six pass.

| scenario | result | TTFT | decode tok/s |
| --- | --- | ---: | ---: |
| short prompt | names Paris | 0.40 s | 19.1 |
| long prompt (~17k tokens) | answered | 82.2 s | 16.4 |
| JSON mode | parses as JSON | 0.97 s | 16.5 |
| function call | correct tool and arguments | - | - |
| code generation | generated code runs and is correct | 0.51 s | 20.5 |
| 4 parallel streams | 4 of 4 completed | 50.9 s | 2.2 aggregate |

## Rates

Cold-prompt protocol: a different prompt body per repeat, because Engram rows are
chosen by token n-gram hashes and re-sending one prompt measures only the
page-cache-warm path. 4,924-token prompt, 200 decoded tokens, three repeats.

| repeat | prefill tok/s | decode tok/s |
| --- | ---: | ---: |
| 1 | 225.3 | 22.75 |
| 2 | 214.6 | 23.03 |
| 3 | 234.3 | 23.20 |
| mean | 224.7 | 23.0 |

For scale, the same protocol and the same `--n-cpu-moe 8` on the Q2_K checkpoint
measured 245.3 tok/s prefill and 14.85 tok/s decode. Read the decode difference
with care: it is not a like-for-like quantization comparison, because Q2_K's
Engram tables are small enough to sit in VRAM and Q4_K_M's are not, so the two
runs differ in placement as well as in weight width.

## Q8_0

Not run. Q8_0 is 473 GiB and the volume holds one checkpoint of this size at a
time; testing moved to the MXFP4 checkpoint and its DSpark drafter next.
