# Qwen3.5 alternating JSON control: 60/60 pass, latency regression retained

Both complete pairs pass all 60 timed cases with identical requests, full outputs, finish reasons and prompt/completion counts. All four c1/c4 groups meet the original comparison rules. **Final c1 first-token and whole-request latency are slower in both version orders.** This repeated result is retained as an unresolved latency regression requiring investigation, not dismissed as an outlier or variance.

The [independent audit](audit.json) verifies all four ordered jobs, 60 unique pair/version/case identities, four separate 512-token decode warmups, 20 separate JSON warmups, source and child-report hashes, unchanged deployment reports, model/configuration identity, wave token counts and timing formulas. It recomputes each comparison and preserves every paired-case value. Original [run.json](run.json), four child reports and four telemetry streams are copied byte for byte. [Completed r2](../completed-r2/README.md) remains a separate experiment; its observations are not replaced.

## Complete pairs and measured latency

Pair 1 starts baseline then final; pair 2 starts final then baseline, with a fresh process for every 15-case job. Both reuse the unchanged r2 deployments and executor. All answers are 16 tokens. These are short-response JSON timings, not sustained 512-token decode. Latency ratio is baseline/final; throughput ratio is final/baseline. Values below 1 are slower.

| Pair | Concurrency | Metric | Baseline | Final | Final speed ratio |
|---|---:|---|---:|---:|---:|
| 1 | 1 | TTFT median (ms) | 77.2923 | 92.7236 | 0.8336 |
| 1 | 1 | Request wall median (ms) | 136.2801 | 157.8999 | 0.8631 |
| 1 | 1 | Whole-wave wall median (ms) | 137.4371 | 158.9524 | 0.8646 |
| 1 | 1 | JSON decode median (tokens/s) | 286.4795 | 267.6014 | 0.9341 |
| 2 | 1 | TTFT median (ms) | 71.5395 | 86.7024 | 0.8251 |
| 2 | 1 | Request wall median (ms) | 130.0134 | 151.6615 | 0.8573 |
| 2 | 1 | Whole-wave wall median (ms) | 131.1040 | 152.9442 | 0.8572 |
| 2 | 1 | JSON decode median (tokens/s) | 276.2610 | 245.2067 | 0.8876 |
| 1 | 4 | TTFT median (ms) | 312.6675 | 284.6902 | 1.0983 |
| 1 | 4 | Request wall median (ms) | 485.5808 | 422.6280 | 1.1490 |
| 1 | 4 | Whole-wave wall median (ms) | 523.4693 | 485.9149 | 1.0773 |
| 1 | 4 | JSON decode/request (tokens/s) | 95.3087 | 96.5127 | 1.0126 |
| 2 | 4 | TTFT median (ms) | 315.2035 | 354.9843 | 0.8879 |
| 2 | 4 | Request wall median (ms) | 478.9557 | 566.2894 | 0.8458 |
| 2 | 4 | Whole-wave wall median (ms) | 509.1518 | 598.7962 | 0.8503 |
| 2 | 4 | JSON decode/request (tokens/s) | 95.9101 | 73.1277 | 0.7625 |

Final c1 TTFT increases by 15.4314 and 15.1629 ms; median whole-request wall increases by 21.6198 and 21.6481 ms. C4 has opposite directions in the two complete pairs, including a substantial decline in pair 2. Neither pair nor any repeated request is dropped or pooled into a replacement speed ratio.

The complete c1 TTFT distributions, in original r0/r1/r2 request order, are:

| Pair | Baseline (ms) | Final (ms) |
|---|---|---|
| 1 | `[77.2923, 79.9350, 62.0450]` | `[66.9418, 92.7236, 104.4094]` |
| 2 | `[71.5395, 71.7306, 67.5340]` | `[85.7064, 244.0011, 86.7024]` |

The 244 ms final observation remains, as does the 161 ms baseline observation from the earlier r2 experiment. The audit and original run retain all c4 paired distributions too. Passing output-quality checks does not erase these measured latency differences.

## Evidence limits and investigation

Each job has two in-window telemetry samples, all with the owned GPU 7 client, sampled SM 1,740 MHz and memory 7,251 MHz, and no non-owned CPU tick increases between those samples. This passes the declared periodic policy; it cannot establish every in-flight clock, CPU frequency, or sub-second contention. First-use JSON warmups occur before telemetry and remain separate diagnostic observations.

The [source- and log-hashed stage evidence](stage-timing.json) matches all 60 cases by request ID and tag. Retained managed file logs also show a c1 model-pipeline first-token gap: median 68 → 85 ms in pair 1 and 62 → 77 ms in pair 2. That timer starts after prompt rendering/tokenization and request submission, while the OpenAI grammar factory runs before entering the pipeline. Therefore the observed gap is not explained solely by HTTP transport, initial tokenization or factory construction: it is present inside the timed engine/prefill/sampling interval. Grammar sampling still runs inside that interval and is not excluded as a cause. No native per-phase, GC or per-token-logit traces were enabled, so these logs cannot separate model kernels, scheduler waits and grammar sampling.

This compares the original baseline managed/native build with final 3651/native 6b3. Several binaries differ, and the current evidence does not isolate a Unicode-only or native-only cause. The [reproducible stage extractor](../../scripts/extract-qwen35-stage-timing.py) preserves the source snippets and per-case arrays; no production change, request retuning, validator weakening or benchmark replacement is credited as a fix here.

The subsequent [60-case managed/native crossing](../qwen35-cross-phase/README.md) retains the intended loaded native library paths and correlates all 12 solo native phases. Its fixed-order, instrumented timings are diagnostic: final managed TTFT remains higher with either native library, while native verify medians stay near 27–29 ms. This narrows the investigation without identifying a specific source-level cause or replacing the qualified latency regression above.

The following [30-case EventPipe diagnostic](../qwen35-eventpipe/README.md) also passes all requests and validates both raw traces, but its profiled solo TTFT medians do not reproduce the earlier gap. Its thread-stack observations remain diagnostic; the regression recorded in this alternating run remains unresolved.

## Reproduction and provenance

The [reviewed plan](../../scripts/qwen35-json-control.md) and [11 local guards](../../scripts/qwen35-json-control-checks.json) describe reuse and cleanup. Runner SHA256 is `d3cc10180fd03b14b7dc7a4d940203804e1b0d64230e15a9b2a5aceaf9dbf4a5`; reused executor is `10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5`; completed-r2 prerequisite is `7ba46725c2a73a7131b859855533b09cf864e4f5b6d64ecb494eb6f1eeb16763`. The [local auditor](../../scripts/audit-qwen35-json-control.py) performs no inference or VM rehash. Full source, raw log, telemetry, child-report and binary hashes are in [audit.json](audit.json).

The subsequent [72-case uninstrumented solo control](../qwen35-solo72/README.md)
passes all exact requests with final native held fixed. Its two complete 18-request
comparisons do not reproduce the earlier slower final medians. Both directions
and all outliers remain recorded; no new production change, causal warmup claim
or source-level fix is credited.
