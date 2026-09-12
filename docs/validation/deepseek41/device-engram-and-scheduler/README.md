# GPU-resident Engram tables and one backend per GPU

Two changes to the DeepSeek V4.1 Flash native executor, measured on the eight
A40 VM against the Q2_K checkpoint. Every number below is a cold-prompt
measurement: each repeat sends a different prompt body, so its n-grams select
Engram rows that run has not touched. Re-sending one prompt measures the
page-cache-warm path only, which is not what a server sees.

All rows use the card's documented profile, not the defaults: `MAX_CONTEXT`
65536, `TS_DSV4_UBATCH` 1024 (the V4.1 default is 256), `TS_DSV41_SPARSE_FA=1`,
`TS_DSV41_COMPACT_RAW_GATHER=1`, `TS_DSV41_TP=0`, prefill chunk 1024, four
scheduler slots. Before and after use the same settings, so the ratios hold; the
absolute figures belong to that profile.

| Stage | Prefill tok/s | Decode tok/s |
|---|---:|---:|
| Before | 207 / 214 / 221 | 28.5 / 28.0 / 29.2 |
| GPU-resident Engram tables | 532 / 536 / 541 | 35.6 / 35.9 / 35.7 |
| plus one backend per GPU | 532 / 537 / 542 | 41.1 |

2.5x prefill and 1.44x decode. At temperature 0 all three implementations
produced identical text on all six prompts of the determinism check (three
prefill retrievals and three 400-token generations).

That is argmax stability, not bitwise reproducibility, and the distinction is
real: ggml's CPU and CUDA Q2_K dequantizers evaluate the same expression, but
nvcc may contract the trailing multiply-subtract into an FMA, so a value whose
2-bit quant is 3 can differ by up to one ulp, around 6e-8 relative. The F32 and
F16 table cases are exact on both sides. Nothing in the gate amplifies a
difference that small, but a run that needs bit-exact agreement with the CPU
oracle should use `TS_DSV41_ENGRAM_DEVICE=0`.

## Where the Engram tables live

`blk.1.engram_embd.weight` and `blk.14.engram_embd.weight` are Q2_K, 256 values
per row, 384,006,168 and 384,016,682 rows, 30.04 GiB each. A Q2_K row of 256
values is 84 bytes, so both tables together are 60.2 GiB of the 246.3 GiB
checkpoint.

They used to be host mappings. Each token hashes 24 row ids per table, and a
worker pool read and dequantized those 48 rows before uploading them as F32.
The rows themselves are tiny, 4,032 bytes of payload per token, but they are
scattered across 60 GiB, so on this VM's MooseFS mount each one is a network
page fault. That is what the measurements above are really about: input
preparation was 13-76 ms per decode token cold and 0.79 ms warm.

Now each table is loaded onto the GPU that owns its layer and the graph gathers
rows with `ggml_get_rows` over the quantized table. Per-token input preparation
is 0.34-0.47 ms, and nothing depends on page-cache state.

The row ids are int32 and the largest table has 384,016,682 rows, comfortably
inside int32. ggml's CUDA `get_rows` computes the row address as `i01*nb01` with
`nb01` a `size_t`, so the 32 GB offset is formed in 64-bit.

This is what SGLang does by default and what vLLM cannot do: vLLM stores a row
as FP8 values plus per-32-element block scales, 264 bytes, making the same two
tables 189 GiB. TensorSharp's Q2_K rows are 84 bytes, so 60.2 GiB fits the spare
VRAM on this box.

### Placement

Table bytes are priced into the layer-split packer, so the devices that hold a
table are given fewer companion layers automatically. If that would force any
routed-expert CPU offload, the tables stay host mappings and startup says so:
paying for device tables with host expert matmuls on every token is a bad trade.
`TS_DSV41_ENGRAM_DEVICE=0` forces host mappings, `=1` requires GPU residency and
fails with the offload it would have cost.

The resulting split on this box is lopsided, because the packer balances bytes
and a 30 GiB table dominates its device's byte cost:

| device | layers | free after load |
|---|---|---:|
| 0 | 0..0 | 39.3 GiB |
| 1 | 1..1 + table | 9.4 GiB |
| 2 | 2..9 | 7.0 GiB |
| 3 | 10..13 | 25.6 GiB |
| 4 | 14..15 + table | 4.8 GiB |
| 5 | 16..23 | 7.0 GiB |
| 6 | 24..31 | 7.0 GiB |
| 7 | 32..39 | 6.5 GiB |

Total latency is unaffected, because a layer split executes the devices in
sequence, but the imbalance would cap any future overlap.

## One backend per GPU

The architecture-specific ops run as `GGML_OP_CUSTOM` nodes. The backend that
executes them used to be registered in `ggml_backend_sched` **alongside** the
CUDA backend for the same device. The scheduler splits a graph wherever the
backend changes, and a V4.1 layer alternates about fourteen times, so a decode
graph was cut into 565 or 577 splits of roughly six nodes each, with a blocking
host synchronization at every one of the 564 boundaries.

That backend now **wraps** its GPU's CUDA backend and replaces it in the
scheduler. It claims the CUDA device's ops and buffer types as well as its own
nodes, forwards each run of ordinary nodes to CUDA as a graph view, and launches
the fused kernels on the same stream. Cross-device copies, asynchronous tensor
access and events delegate to CUDA.

| | Splits per decode graph | Decode compute | Decode tok/s |
|---|---:|---:|---:|
| Two backends per GPU | 565 / 577 | 26.6-27.1 ms | 35.6 |
| One wrapping backend | 8 | 23.2-23.5 ms | 41.1 |

One split per GPU is the floor for a layer split. Prefill is unchanged, as
expected: a prefill split already does milliseconds of work, so a per-boundary
synchronization was noise there.

## What did not work

Three changes were measured and reverted, so the shipped code carries no inert
option.

**Prefill chunk pipelining for V4.1.** Prefill 533-542 tok/s against 532-541,
decode 40.8-41.0 against 41.1, and the per-chunk compute stopwatch stayed at
~1,780 ms instead of dropping toward the submit cost. A layer-split submission
does not become asynchronous, so there is nothing for the next chunk to overlap.

**Scheduler events** (`ggml_backend_sched_new(..., parallel=true, ...)`).
Also inert, on its own and combined with the above: prefill 531-542, decode
40.9-41.2. It additionally costs `GGML_SCHED_MAX_COPIES` staging copies of every
tensor that crosses a device boundary.

**GPU peer access** (`GGML_CUDA_P2P=1`). Actively harmful here: decode compute
went from 23.5 ms to 3,480-3,806 ms, about 150x slower. These eight A40s have no
NVLink and straddle two NUMA nodes (GPUs 0-2 on one, 3-7 on the other, SYS
between), so peer mapping of the VMM pools is far worse than staging through the
host. Do not set this variable on this topology.

## Utilization

Sampling all eight GPUs at 5 Hz through one request (4,924-token prefill plus
120 decode tokens) gives a sum of means of 91.2% against a possible 800%. Layer
split runs the GPUs strictly in sequence; single-stream decode on a pipeline
split cannot do better. Raising it needs either working chunk overlap, which the
interconnect blocks, or tensor parallelism, which on this topology pays the same
interconnect cost.

## Verification after the review fixes

Re-measured with every fix in place, same harness and same prompts:

| Configuration | Prefill tok/s | Decode tok/s | Placement |
|---|---:|---:|---|
| default | 533 / 535 / 539 | 40.31 / 40.71 / 40.72 | GPU-resident tables |
| `--n-cpu-moe 8` | 245.3 | 14.85 | GPU-resident tables, 8 of 40 layers' experts on the host |

All six determinism prompts still return text identical to the original
host-lookup baseline, so the fixes changed no output.

The `--n-cpu-moe 8` row matters twice over. It is the configuration that would
have aborted at graph build before the placement array was sized for the CPU
device index, and it is the one that shows the offload revert now respects an
operator's explicit request: the tables stay on the GPUs because the run was
already paying for that offload, where before they would have been pushed back
to host mappings. Its lower throughput is the expected cost of eight layers of
expert matmuls on the host, not a regression.

## What an adversarial review of these changes found

Four independent reviewers read the diff against distinct risk lenses. Nine
findings survived, and all nine are fixed. Three are worth recording because
nothing in the measurements above would have caught them.

**The placement array was indexed by GPU, but the graph builder pins with the
CPU index.** `graph_builder::pin()` is called with `n_gpu` for host-resident
routed experts, so every run with `--n-cpu-moe > 0` would have reached an
unfilled slot. The measurements all ran with zero CPU offload, so the whole
benchmark set was blind to it. The array is now sized and filled like
`backends[]`, with the CPU backend at index `n_gpu`.

**A 30 GiB table became a graph source that the scheduler was free to move.**
ggml's rule that a node runs where its weight lives only applies to buffers
marked `USAGE_WEIGHTS`, and device weight buffers here are not marked. The
gather is now pinned to the device that holds its table, so the table can never
become a cross-backend copy.

**The wrapper silently disabled two things the CUDA backend provides.** Taking
the CUDA backend's place in the scheduler without forwarding `graph_optimize`
turned off ggml-cuda's graph optimization for every split, and omitting
`offload_op` turned off the scheduler's host-weight offload. Both now delegate.

The rest: the offload revert ignored an operator's explicit `--n-cpu-moe`, so
requiring device tables could refuse a configuration that fits; device tables
were accepted with no memory margin, so a box where they just fit could fail to
admit a second concurrent sequence; two option-validation paths accepted a typo
silently; an error message named an impossible offload value when the model
simply did not fit; the per-device backend records were process-wide statics
that one model's teardown would leave dangling for another's; and a run of
nothing but views was handed to ggml-cuda for a full capture cycle over zero
work.

One finding is recorded but not fixed: the fused kernels select the CUDA device
by ggml's device index, which is wrong if ggml is configured with several
virtual devices mapped onto one physical GPU. That predates these changes, the
mapping helper is not exported from ggml-cuda, and TensorSharp does not use that
configuration.
