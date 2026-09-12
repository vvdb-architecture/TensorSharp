# Phase 1: bounded Engram decode fanout

This completed stage uses native **`1d9209883c7275e545cfb18c4fd53e7ab4eb51bcef6c8d3bc83c328d5c817835`**, built from `ggml_ops_deepseek4.cpp` SHA **`24246c67729630b9e6c95c8f660068d06f0b53f8b523e7c935c1e5c0e5d6609d`**. It belongs to the actual `/root/TensorSharp/TensorSharp.Cli/bin` deployment, separately from historical native 6b3 and any later optimization. [Build/deployment provenance](cli-gpu-provenance.json), [reported hashes](deployed-sha256.txt), source snapshots and original logs remain unchanged.

The [independent PERF2 audit](audit.json) passes all five completed requests: two identical 41-token arithmetic replies, prompted JSON sum/difference, the dependent product follow-up, and a 1,557-token attached-document retrieval. The JSON cases do not use a constrained grammar. Every one of the 116 native microbatches is retained. Both arithmetic replies exactly match the deterministic original-binary transcript, including thinking text.

| Arithmetic observation | Median decode inputs / compute | CLI-reported rate |
|---|---:|---:|
| Before, PERF3 | 39.64 / 33.08 ms | 2.9 tokens/s |
| After, first PERF2 request | 13.27 / 27.10 ms | 23.4 tokens/s |
| After, repeated PERF2 request | 0.79 / 27.07 ms | 33.5 tokens/s |

These are diagnostic observations, **not an isolated whole-model speedup**: PERF3 prints extensive node transitions outside the compute stopwatch, and filesystem cache state was uncontrolled. The [original audit](../before/audit.json) confirms that the original graph already used CUDA/TSDSV4 on all eight devices with no CPU-assigned span; host input preparation remained separate.

The separate [uninstrumented audit](untraced/audit.json) passes 3/3 exact input-script requests. The default random reply uses 25 tokens at 18.7 tokens/s; controlled/repeated replies use 41 tokens at 21.7/32.1 tokens/s. Their full deterministic outputs match before and PERF2. The original uninstrumented random reply had 97 tokens, so it is not a matched throughput denominator. The final GPU samples show zero memory on all eight devices after exit. Startup was 225,280.3 ms under uncontrolled storage/cache conditions; no startup ratio is claimed.

The [scratch-file controls](engram-io-filesystem.jsonl) pass all 24 exact-output checks. Every one of the 18 cold cases confirms zero initially resident selected client pages. Three alternating pairs give serial → 16-worker median lookup times of 33.894926 → 4.845843 ms for one token and 106.969162 → 11.470738 ms for three tokens. The source uses its own 64 MiB scratch file and synthetic int16 rows; remote NFS server cache state is not controlled. [Warm-memory overhead](engram-io-warm-memory.jsonl) remains: 1.669 → 36.518 µs and 5.009 → 52.800 µs respectively. No universal lookup improvement is claimed.

The [stage manifest](artifact-manifest.json) pins every file. The full [investigation](../README.md) also preserves local normal/sanitizer tests, the final combined-source 41/41 CPU oracle and earlier strict-fixture failures. All copies are byte-preserving; compressed telemetry expands to the original hash recorded by its audit. Later changes must use another stage rather than rewriting these results.
