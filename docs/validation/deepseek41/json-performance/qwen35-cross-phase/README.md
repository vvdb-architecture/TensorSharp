# Qwen3.5 managed/native crossing: 60/60 pass, diagnostic timings

All four 15-case jobs completed and passed. Every measured request, full response, finish reason and prompt/completion count matches the completed r2 Qwen3.5 reference. The independent audit also verifies that all four owned processes mapped the intended native library. **These instrumented, fixed-order timings are diagnostic.** They do not replace the slower c1 latency measured in the qualified [alternating control](../qwen35-alternating/README.md), or establish a fix.

The four fresh hosts ran in this order: baseline managed/baseline native, baseline managed/final native, final managed/baseline native, then final managed/final native. Each reuses the unchanged r2 executor: a separate 512-token decode warmup, five JSON warmups, and 15 measured JSON requests across concurrency 1 and 4 with three repeats. The model, request tags, sampler settings, GPU 7, context 8,192, four slots and prefill chunk limit 256 remain the same. Every launch adds `TS_GGML_PHASE_TIMING=1`. Only `libGgmlOps.so` changes within each copied managed deployment; all other frozen files, including configuration, remain checked.

## Retained measurements

Each answer contains 16 completion tokens from a 92-token prompt. The table retains all complete groups; each c1 median uses three requests and each c4 median uses 12 requests. These are short-response JSON decode measurements. No performance-qualified ratios are calculated from this diagnostic run.

| Managed / native | Concurrency | Client TTFT median (ms) | Request wall median (ms) | JSON decode median (tokens/s/request) |
|---|---:|---:|---:|---:|
| Baseline / baseline | 1 | 67.24 | 127.31 | 267.38 |
| Baseline / final | 1 | 69.03 | 125.03 | 287.79 |
| Final / baseline | 1 | 76.83 | 134.32 | 279.98 |
| Final / final | 1 | 74.76 | 134.11 | 273.98 |
| Baseline / baseline | 4 | 264.85 | 459.89 | 88.08 |
| Baseline / final | 4 | 215.14 | 372.41 | 116.17 |
| Final / baseline | 4 | 263.62 | 408.18 | 97.86 |
| Final / final | 4 | 273.53 | 420.65 | 98.21 |

[audit.json](audit.json) retains all individual TTFT, request-wall and decode observations, plus every whole-wave wall time and token count. No outlier, repeated request or concurrent wave is discarded.

The separate [phase analysis](phase-analysis.json) correlates all 12 solo cases. Each association requires exactly one matching request start, one native verify phase and one completion before the next request starts. Structured file logs confirm the request ID, exact answer, 92 prompt tokens, 16 completion tokens and zero reused KV tokens. Concurrent native phase lines lack request IDs and remain aggregate.

| Managed / native | Native verify median (ms) | Pipeline TTFT median (ms) | Client TTFT median (ms) |
|---|---:|---:|---:|
| Baseline / baseline | 29.08 | 58 | 67.24 |
| Baseline / final | 26.86 | 61 | 69.03 |
| Final / baseline | 28.14 | 69 | 76.83 |
| Final / final | 28.93 | 66 | 74.76 |

At a fixed baseline native library, final managed client TTFT is 9.59 ms higher; at a fixed final native library, it is 5.73 ms higher. The native verify median changes by −0.94 and +2.07 ms, respectively. This points further investigation toward the timed managed/engine interval: the approximately 27–29 ms native verify medians do not show a consistent native-library increase matching the earlier roughly 15 ms latency gap. It does **not** identify a particular managed source change, prove a Unicode-grammar cause, or exclude native effects under other workloads.

There are only three solo observations per job, the order is fixed, and phase instrumentation changes execution timing. Large native bind observations remain in the raw data: 15.85 ms in baseline/baseline, 19.07 ms in final/baseline and 21.03 ms in final/final. The difference between integer pipeline TTFT and native phase time is an unresolved residual, not a separately measured grammar, scheduler, GC or transport cost. Asynchronous timing boundaries may overlap. The earlier qualified alternating latency regression remains unresolved.

The later [30-case EventPipe diagnostic](../qwen35-eventpipe/README.md) holds final native fixed and collects managed thread stacks after warmups. It passes all cases but does not reproduce the earlier solo TTFT gap at the median. Its raw-trace and phase analyses are preserved separately and do not establish a cause or fix for the qualified regression.

## Audit and reproducibility

The [independent audit](audit.json) verifies all 60 case identities and exact responses against [completed r2](../completed-r2/README.md), all planned jobs and warmups, child/source/harness/model hashes, deployment file maps, sampling/environment settings, wave counts, and unchanged-host reports. Every retained phase line is reparsed against its original stdout line. Original [run.json](run.json), child reports, stdout, file logs, phase reports and telemetry are copied byte for byte; [artifact-manifest.json](artifact-manifest.json) hashes the curated files. Telemetry is retained without promoting the diagnostic window to qualified performance evidence.

[loaded-libraries.json](loaded-libraries.json) records the actual `/proc` mapping from each of the four distinct owned processes and matches its full launch command to the relevant child report. Each mapping names that host's intended `bin/libGgmlOps.so`. The observer samples once per host and does not hash a live mapping; content identity comes from the producer's verified, unchanged deployment manifests. The local audit does not rehash VM files or model weights.

Baseline is managed HEAD `04a5faa9e641fc0b06de661be1191afbc6edaf50` with native SHA256 `e67e4c922138a7038bfc07d110d22eeec0f3f4cf10d545fae2971ca3f7dc3093`. Final is managed build 3651 with native `6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014`. Full per-component hashes are preserved for each crossing in the original reports and phase analysis.

The [runner](../../scripts/run-qwen35-cross-phase.py) SHA256 is `32219050b755a6b28a08de3c0255d929d53813e5378b022b00d435c4484104b2`; it reuses executor `10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5`. The [protocol](../../scripts/run-qwen35-cross-phase.md), [40 simulated guards](../../scripts/qwen35-cross-phase-checks.json), [mapping observer](../../scripts/observe-qwen35-cross-libraries.py), [local auditor](../../scripts/audit-qwen35-cross-phase.py) and [phase analyzer](../../scripts/analyze-qwen35-cross-phase.py) are snapshotted with exact hashes in [the source index](../../scripts/sources.json). These scripts use the recorded VM/local paths; restore the raw-artifact layout and the r2 executor under its expected sibling filename before replaying an offline analysis. No production source or validator was changed for this experiment.
