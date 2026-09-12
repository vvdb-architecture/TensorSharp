# Final placement measurements

These runs use the verified seven-shard Q2_K checkpoint on the requested eight-A40 VM. Each profile records its exact managed/native binaries, commands, environment, source hashes, raw report hashes, native log ranges and sampled GPU telemetry. Startup and source-page warming are excluded from qualified inference measurements.

## Four CPU-MoE layers

Profile `layer8-context65536-ubatch1024-cpumoe4-cputhreads48-sparse1-compact1-chunk1024-6b3-final` ran from 2026-09-11 11:09:58 to 11:19:51 UTC. It uses native `6b3b5ab3…`, the 3,635-test managed host, four leading routed-expert layers on CPU, 48 explicitly configured native CPU threads, layer placement on eight visible GPUs, F16 KV, context 65,536, microbatch 1,024, four request slots and scheduler chunks of 1,024. Sparse attention and raw-window compaction are explicitly enabled. Shared experts stay on their layer GPUs.

| Check | Passed / planned |
|---|---:|
| Short, JSON, schema, generated history and default-parallel tools | 28/30 |
| Sustained decode, exactly 512 tokens per request | 15/15 |
| Long retrieval at 7,706 / 30,585 input tokens | 2/2 |
| Separate explicitly serial tool workflows | 10/10 |
| Invalid-image HTTP rejection, chat and Responses, streaming on/off | 8/8 |

The 57-case inference plan completed; it did **not** pass all cases. Both failing agent requests (`agentic-c4-r0-i0` and `agentic-c4-r0-i2`) produced `read_invoice` and a premature `calculate_total` call with zero placeholder arguments in the same response. The strict checker rejected both. Compared with the [historical CPU4 baseline](../cpu4-historical-baseline/README.md), default-parallel quality changed from 29/30 to 28/30: one additional failure remains. The separate `parallel_tool_calls: false` workflows passed 10/10 in both builds and do not erase that regression or establish a numerical fix.

| Measurement | Historical 3b88 | Shared-placement fix 6b3 |
|---|---:|---:|
| Single-request decode, median tokens/s | 23.9825 | 29.7361 |
| Four concurrent requests, median decode tokens/s per request | 6.7343 | 7.1519 |
| Four-request whole-wave throughput, tokens/s | 26.4029 | 27.9956 |
| 7,706-token time to first token | 28.533 s | 27.317 s |
| 30,585-token time to first token | 110.334 s | 105.169 s |

All 57 initial request hashes match their corresponding historical cases, including the separately named serial-policy run. Every sustained-decode request completed 512 generated tokens. Single-request decode increased 24.0%; per-request concurrent decode increased 6.2%. These are profile before/after measurements, not a same-binary isolated experiment or a llama.cpp comparison. The Runtime assembly is identical across these two profiles; the managed image-validation follow-up changed Chat/Server binaries. The shared-placement fix changes the execution backend and can change generated output.

The independent [audit](cpu4-report.json) validates the [intended manifest](cpu4-manifest.json), complete plans and case coverage, native byte-range hashes, prompt/generated/EOS accounting and telemetry coverage. All four inference windows have zero logged errors and zero cache preemptions. The decode window contains 683 prefill tokens and 7,682 single-token forwards, including warmup and EOS work. Both long cases account for exactly 38,314 prefill tokens including warmup.

Raw and curated per-case reports are under [full-checkpoint](../full-checkpoint/); the retained VM originals are `/workspace/deepseek41-work/PROFILE-*`. The image rejection report retains all eight request/response bodies and hashes separately from inference quality and timing.

## Eight-rank routed-expert tensor parallelism

Profile `tp8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final` ran from 2026-09-11 12:12:57 to 12:30:03 UTC. It uses native `6b3b5ab3…`, the 3,651-test managed host, zero CPU-MoE layers and 32 explicitly configured native CPU threads. Context, microbatch, request slots, scheduler chunks and attention options match the final CPU4 profile. Routed experts span all eight GPUs; attention and shared experts remain layer placed, and routed-expert reduction uses host staging.

| Check | Passed / planned |
|---|---:|
| Short, JSON, schema, generated history and default-parallel tools | 29/30 |
| Sustained decode, exactly 512 tokens per request | 15/15 |
| Long retrieval at 7,706 / 30,585 input tokens | 2/2 |
| Required/named/none/serial/parallel tool policies | 30/30 |
| Thinking / blocking protocol | 4/4 / 4/4 |
| Images, image history and sampled video frames | 25/25 |
| Multilingual arithmetic and exact Unicode JSON | 10/10 |
| Separate explicitly serial tool workflows | 10/10 |
| Separate image / Responses audio HTTP rejection checks | 8/8 / 4/4 |

All 130 planned inference cases completed, with **129 passing**. Default-parallel case `agentic-c4-r0-i2` returned `read_invoice` and a premature `calculate_total` with zero placeholder arguments together. The historical TP profile passed 30/30 standard quality cases, so one additional failure remains. The previously failing named-thinking and reasoning-agent cases now pass; this is final-build verification, not an isolated grammar A/B. Explicitly serial workflows pass without establishing a fix for default-parallel behavior.

A [local diagnosis](../parallel-tool-dependency/README.md) verifies that the initial prompt matches vLLM's V4.1 encoder byte-for-byte, native output already contains both calls and zero arguments, and the grammar also permits stopping after the first call. It found no parser or grammar defect forcing this failure; numerical-path attribution remains unresolved.

| Measurement | Historical b26 | Final 6b3 |
|---|---:|---:|
| Single-request decode, median tokens/s | 15.4166 | 19.5159 |
| Four concurrent requests, median decode tokens/s per request | 3.7091 | 4.9986 |
| Four-request whole-wave throughput, tokens/s | 14.704 | 19.7381 |
| Four-request median whole-wave time | 139.284 s | 103.759 s |
| 7,706-token time to first token | 33.988 s | 21.291 s |
| 30,585-token time to first token | 137.273 s | 86.135 s |

The 65 initial requests shared with the historical quality, decode, long, thinking, blocking and multilingual suites have matching hashes. All 15 decode requests completed 512 tokens. Single-request decode increased 26.6%; concurrent per-request decode increased 34.8%. Native placement, synchronization and managed binaries changed between these profiles, and the old native thread count of 32 is inferred rather than observed. These comparisons cannot isolate any one change or establish llama.cpp parity.

The independent [audit](tp8-report.json) validates the [manifest](tp8-manifest.json), outer phase completion and numerical accounting for placement and serial-tool phases. Those audited windows have zero logged errors or cache preemptions. Other phase reports retain their own results and telemetry; the generic native log parser does not count multimodal prefill forwards, so its zero media-prefill counter is not a claim that media performs no prefill.

The original optional source-warming invocation omitted a required `--report` argument and failed before warming. The host nevertheless completed loading and Engram warming and passed endpoint checks before the qualified suite. This startup-helper failure is preserved separately from completed inference; startup is unqualified. See the [startup diagnostic](../tp-startup-diagnostic/README.md).

## Eight-GPU layer placement

Profile `layer8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final` ran from 2026-09-11 12:57:38 to 13:18:33 UTC. It uses the same final native and managed binaries as TP8, with routed-expert TP disabled, zero CPU-MoE layers and 32 explicit native CPU threads. Context 65,536, microbatch 1,024, four slots, scheduler chunks 1,024, F16 KV, sparse attention and raw-window compaction are unchanged. All weights assigned to a GPU layer execute on that layer's device; the Engram tables remain host mapped.

**All 138 planned inference cases passed**, and all seven phases completed successfully:

| Check | Passed / planned |
|---|---:|
| Short, JSON, schema, generated history and default-parallel tools | 30/30 |
| Sustained decode, exactly 512 tokens per request | 15/15 |
| Single-request long retrieval | 2/2 |
| Required/named/none/serial/parallel tool policies | 30/30 |
| Thinking / blocking protocol | 4/4 / 4/4 |
| Images, image history and sampled video frames | 25/25 |
| Multilingual arithmetic and exact Unicode JSON | 10/10 |
| Separate explicitly serial tool workflows | 10/10 |
| Four concurrent long requests at each of two lengths | 8/8 |

| Measurement | Final layer8 |
|---|---:|
| Single-request decode, median tokens/s | 34.8274 |
| Four concurrent requests, median decode tokens/s per request | 8.4580 |
| Four-request whole-wave throughput, tokens/s | 33.1292 |
| Four-request median whole-wave time, 2,048 generated tokens | 61.819 s |
| 7,706-token time to first token, concurrency 1 | 19.985 s |
| 30,585-token time to first token, concurrency 1 | 80.240 s |
| Four 7,706-token requests, whole-wave time | 86.295 s |
| Four 30,585-token requests, whole-wave time | 326.204 s |

The concurrent long phase accounts for exactly 153,187 native prefill tokens, including its warmup, and 282 decode forwards. There were no cache preemptions or recomputed prompt tokens. All eight outputs satisfy the unchanged exact-facts and raw-JSON checker. This covers the earlier long-concurrency failure without removing the failed historical case. The single-request long phase accounts for 38,314 prefill tokens; steady decode accounts for 683 prefill tokens and 7,682 decode forwards including warmup/EOS.

The independent [audit](layer8-report.json) checks the [manifest](layer8-manifest.json), complete outer plan and placement/serial numerical accounting. The [whole-group comparison](layer8-comparison.md) preserves historical settings and request matching. Final layer placement is faster than the final TP8 and CPU4 profiles on this VM, but those are different execution placements, and their default-parallel failures remain separate results. Neither this finite test matrix nor the unavailable compatible llama.cpp reference establishes blanket quality/performance parity.

The corrected preparation helper completed successfully before inference; its 40-layer source warming read 196,765,286,400 bytes in 332.350 seconds while the model loaded. Startup is diagnostic and excluded from the table. The owned host was stopped after all results were finalized, and no GPU compute client remained before the next numerical diagnostic.
