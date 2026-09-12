# CLI GPU execution and Engram input preparation

The original CLI already assigned its inference graph to the eight CUDA devices. The [independent before-run audit](before/audit.json) finds only `CUDA0..7` and `TSDSV4-0..7` across all 139 recorded microbatches, with no CPU or unknown backend span. CPU-side input preparation remained outside that graph. The original “CPU fallback/host-expert pool” startup line did not mean CPU-only model inference.

This investigation concerns the user's `/root/TensorSharp/TensorSharp.Cli/bin` deployment. The [deployment provenance](phase1/cli-gpu-provenance.json) binds original native `c68df1f3d0c70269be249c12a9eae9e7e6ae9144e232dda311d4e853c2d048d0` and phase-1 native `1d9209883c7275e545cfb18c4fd53e7ab4eb51bcef6c8d3bc83c328d5c817835` to that directory. Historical `/workspace` benchmarks and native `6b3…` retain their separate identities. **The before PERF3 / after PERF2 observations are diagnostic, not an isolated whole-model speedup measurement.**

The later [combined stage](phase2-final/README.md), native `fa5ac075…`, adds bounded joint table scheduling and scoped Linux RANDOM advice. Its CUDA, Linux and source-review evidence is archived separately. The original and phase1 files below remain unchanged controls.

## Original CLI and diagnostic requests

The original uninstrumented request answered `42` in 97 generated tokens, with CLI-reported 7.9 tokens/s. Its [full transcript](before/user-before-cli.log) is separate from the later `TS_DSV4_PERF=3` run. That run contains one native warmup, then two requests:

| PERF3 request | Prefill tokens | Decode calls | Median inputs (ms) | Median compute (ms) | Answer |
|---|---:|---:|---:|---:|---|
| 17 + 25, random sampling | 43 | 95 | 76.56 | 33.32 | 42 |
| 31 + 47, reset / temperature 0 / seed 42 / max 128 | 43 | 41 | 39.64 | 33.08 | 78 |

Warmup is excluded from both rows. [Every microbatch](before/microbatches.csv) and all backend-span counts remain in the audit. The `inputs` stopwatch includes host lookups, waits, mask preparation and uploads; it is not an exclusive Engram timer. `compute` includes synchronized graph execution, not exclusive GPU-kernel time. PERF3 prints thousands of backend transitions after that compute stopwatch; its whole-turn time cannot be compared directly with an uninstrumented or PERF2 run as an isolated speedup.

The [readable PERF3 transcript](before/user-before-perf-clean.txt) removes only native diagnostic lines and preserves generated token prefixes. Losslessly compressed [original PERF3 output](before/user-before-perf.log.gz) and [GPU samples](before/user-before-gpu.csv.gz) retain the exact raw bytes and hashes. All eight GPUs have nonzero utilization samples, but this capture also spans loading and idle time and has no compute-PID column. Whole-capture medians are not per-request utilization. The CLI reports `kvCacheDtype=f32`; the pinned native source allocates raw, compressed and index key caches as F16, so that CLI label must not be interpreted as the native cache dtype.

## Phase-1 CLI and filesystem results

The [phase-1 audit](phase1/audit.json) verifies all **5/5 CLI cases**, 116 native microbatches, the deployed/source hashes, eight selected CUDA devices and zero routed-expert CPU-offload layers. Both deterministic arithmetic runs produce the exact same 41-token thinking text and answer as the deterministic before run.

| After PERF2 case | Prompt tokens forwarded | Generated tokens | Answer | Median decode inputs / compute (ms) |
|---|---:|---:|---|---:|
| 31 + 47, first request | 43 | 41 | 78 | 13.27 / 27.10 |
| Same request after reset | 43 | 41 | 78 | 0.79 / 27.07 |
| Prompted JSON, sum/difference of 83 and 29 | 25 | 12 | `{"sum":112,"difference":54}` | 15.885 / 27.985 |
| Follow-up product | 18, with 37 retained | 7 | `{"product":2407}` | 15.69 / 27.55 |
| Attached 80-entry document, retrieve entry 47 | 1,557 | 3 | `amber-047` | 11.40 / 44.51 |

The JSON cases use prompting, **not constrained JSON mode**. The [transcript](phase1/user-after-perf2-clean.txt) and [attachment](phase1/cli-gpu-long-prompt.txt) preserve outputs and the 1,557-token retrieval scope. Root reports that an overlong unsent PTY line was cleared before attaching the file; the log records five completed model requests and no model failure from that blank prompt. The two arithmetic CLI reports are 23.4 and 33.5 tokens/s, but tracing level and uncontrolled filesystem cache prohibit a causal end-to-end ratio against the old run. Their large input-time difference also remains explicit. The separate [uninstrumented confirmation](phase1/untraced/audit.json) passes 3/3 requests: the default random-sampling arithmetic reply is 25 tokens at CLI-reported 18.7 tokens/s; the controlled 41-token reply is 21.7 tokens/s and its repeat is 32.1 tokens/s. Both controlled replies preserve the exact before/PERF2 thinking text and answer. The original random-sampling baseline had 97 tokens, and no matching uninstrumented old-binary/cache-controlled pair was measured, so these are observed rates rather than a paired speedup. The recorded 225,280.3 ms load is likewise not a paired startup comparison. Its final telemetry samples show zero GPU memory on all eight devices after process exit.

The VM [scratch-file control](phase1/engram-io-filesystem.jsonl) retains all **24/24 exact-output checks**. All 18 cold cases have zero initial resident selected pages confirmed by `mincore`; three serial/parallel pairs alternate order for each geometry. The benchmark advises away only its own 64 MiB file, without controlling remote NFS server caches.

| Lookup geometry | Serial cold median (ms) | 16-worker cold median (ms) |
|---|---:|---:|
| One token, 24 rows × 256 elements | 33.894926 | 4.845843 |
| Three tokens, 72 rows × 256 elements | 106.969162 | 11.470738 |
| Prior prefill control, 1,024 rows × 32 elements | 670.728223 | 71.270682 |

These controls use identical selected rows and synthetic int16 dequantization. They establish overlap for locally nonresident mapped pages, not full-checkpoint throughput or a universally faster lookup. The [VM warm-memory control](phase1/engram-io-warm-memory.jsonl) retains the opposite tradeoff: 1.669 → 36.518 µs for one token and 5.009 → 52.800 µs for three tokens. Single warmed-file observations and their separate warming costs also remain in the original report.

## Local fixes and checks

The phase1 source (`ggml_ops_deepseek4.cpp` SHA `24246c67729630b9e6c95c8f660068d06f0b53f8b523e7c935c1e5c0e5d6609d`) reports actual compute devices, labels the CPU pool as auxiliary, reports resolved routed-expert offload, and rejects unsupported V4.1 alternate-GPU fallback. It also uses the existing bounded Engram I/O pool for one-to-three-token batches; the prior path read those rows serially. One token selects 24 independent rows, so blocked mapped-file reads can overlap. A one-thread pool still runs serially.

The [local artifact audit](local-audit.json) preserves normal and ASan/UBSan test passes for 1/3/257-token row ordering, output bounds, invalid-row rejection, exceptions, recovery and concurrent submissions. Artificial blocked-read tests verify overlap within the configured worker bound. They do not measure the VM filesystem. The cached-memory control explicitly retains dispatch overhead:

| Local cached lookup | Serial median (µs) | 16-worker median (µs) |
|---|---:|---:|
| One token / 24 rows | 0.375 | 35.247 |
| Three tokens / 72 rows | 1.143 | 41.235 |

These synthetic macOS timings are not a universal speedup claim. All samples and exact-output assertions are retained in [the fanout report](local-engram-fanout/report.json) and logs.

The phase1 source separately passes [41/41 stable-fixture CPU oracle checks](local-final-fanout/README.md) at `atol=rtol=2e-5`, with maximum absolute error `5.163252e-6`. The [earlier backend controls](local-backend/README.md) show actual native load rejection of CUDA-to-Metal fallback and explicit Metal, while explicit CPU remains accepted. They also retain the unchanged 32/41 CUDA-index CPU fixture result and the separate 35/41 older F32 fixture result; no tolerance was widened or failure hidden. Those earlier controls use a different local library and source stage.

## Reproducibility

The [source snapshots and patch](sources/fanout-and-diagnostics.diff), original commands, logs and per-check results bind each local stage to its own hashes. Phase 1 additionally preserves the [native build log](phase1/native-decode-build.log), [deployed hashes](phase1/deployed-sha256.txt) and expanded filesystem benchmark source (`772fef5b…`) separately from the earlier local warm-memory source. The deployment uses HEAD `3347b06…` plus the recorded patch, Release CUDA/Vulkan build flags and CUDA architecture `86-real`; selected V4.1 execution is CUDA. Managed binaries were unchanged according to the deployment provenance. The [before-run auditor](audit-cli-gpu-before.py) and [local curator](curate-cli-gpu-local.py) perform no inference or VM work. The [phase-1 auditor](audit-cli-gpu-phase1.py) checks the later request/output and filesystem records without inference; the [separate uninstrumented auditor](audit-cli-gpu-untraced.py) checks its exact input script and three outputs. The [completed phase-1 snapshot](phase1/README.md) has its own manifest so later optimizations can be recorded separately. The [artifact manifest](artifact-manifest.json) pins each completed evidence stage. Original local native libraries and fixtures remain at the external paths recorded in their manifests; they are not substitutes for the deployed VM binary.
