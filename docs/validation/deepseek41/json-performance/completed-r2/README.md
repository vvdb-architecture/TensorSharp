# Completed JSON A/B r2: 90/90 executed, failures retained

All six planned jobs completed: **90 timed cases**, six separate 512-token decode warmups and 30 separate JSON warmups. Every recorded deployment-integrity check passed after redirecting mutable logs outside the frozen hosts. Completion is distinct from correctness or comparable timings: the final build has **one newly failed timed case**, and only Qwen3.5's complete c1/c4 groups qualify for timing ratios.

The [independent audit](audit.json) cross-checks child, telemetry, stdout, runner, shared-helper and portable-harness hashes; all request identities, case keys, wave token counts, semantic statuses, binary/configuration records, sampling and timing formulas. It recomputes all comparison results and qualified median ratios, with no selected successful subsets. Original [run.json](run.json), six child reports and six telemetry streams are retained byte for byte. This is a whole-build comparison across native and managed changes, not isolated Unicode-only causal attribution.

| Model | Baseline passed | Final passed | Newly failed | Timing eligibility |
|---|---:|---:|---:|---|
| Qwen3 | 5/15 | 4/15 | 1 | Both groups withheld: semantic failures; c4 also differs in output/token count |
| Qwen3.5 | 15/15 | 15/15 | 0 | All three c1 repeats and all three complete c4 waves |
| Gemma4 | 0/15 | 15/15 | 0 | Both groups withheld: different outputs/work; baseline JSON warmups also fail |
| Total | 20/45 | 34/45 | 1 | No pooled 45-case speed ratio |

The [incomplete first attempt](../incomplete-r1/README.md) remains separate: 30/90 executed, Qwen3 baseline 6/15 versus final 4/15, with two newly failed c4 cases. The logging fix did not change prompts, validators, model settings or generation policy.

## Qwen3.5: retain slower latency as well as faster decode

Both builds produce identical requests, complete responses, finish reasons and prompt/completion counts in each qualified group. Every JSON answer is 16 tokens. This is **short-response JSON decode**, not sustained 512-token throughput. Ratio is baseline/final for latency and final/baseline for throughput; below 1 is slower.

| Metric | Concurrency | Baseline | Final | Final speed ratio |
|---|---:|---:|---:|---:|
| Median first-token latency (ms) | 1 | 65.0391 | 72.0334 | 0.9029 |
| Median request wall (ms) | 1 | 123.9760 | 132.8216 | 0.9334 |
| Median whole-wave wall (ms) | 1 | 125.0446 | 133.8167 | 0.9344 |
| Median JSON decode tokens/s | 1 | 271.1333 | 288.2934 | 1.0633 |
| Median first-token latency (ms) | 4 | 258.4001 | 276.2135 | 0.9355 |
| Median request wall (ms) | 4 | 437.5794 | 416.3407 | 1.0510 |
| Median whole-wave wall (ms) | 4 | 506.2811 | 447.4791 | 1.1314 |
| Median JSON decode tokens/s/request | 4 | 97.9664 | 129.1873 | 1.3187 |

The c1 first-token observations are baseline `[64.671, 161.391, 65.039]` ms versus final `[72.033, 79.107, 64.273]` ms. All observations remain; the baseline outlier is not removed, nor does this three-repeat median establish a stable regression cause. Both c1 request/wave latency and c1/c4 first-token latency remain slower in the recorded comparison. The subsequent [60-case alternating control](../qwen35-alternating/README.md) is a separate experiment and also records slower c1 latency in both version orders.

All six timed windows pass the runner's declared periodic telemetry policy: owned GPU 7 client only, sampled SM 1,740 MHz and memory 7,251 MHz throughout. Qwen3.5 has only two in-window samples per build. The audit independently recomputes the clock/client statistics and retains non-owned CPU tick changes. This sampling does not prove every in-flight clock or exclude sub-second contention. Warmup timings below precede telemetry and are not qualified.

## Same-request Qwen3 failure under different prior workloads

`json-c4-r0-i0` passes in the earlier final 75-case quality run (flat object, 21 tokens), but fails in both JSON-performance attempts (nested object, 25 tokens). The recorded HTTP request SHA256 is `e1c5ae800ba0b4c69abce54cb2dd6301f7a8a6329b7a9892d83b8514eefaacfc`. The fixture's `input_sha256` is a separate hash over its scenario/sampling metadata; the audit labels both hashes explicitly. **All 15 r0 JSON requests are identical to their earlier quality-run counterparts**, not differently tagged requests. Final binaries and request settings match as well.

The requested flat response is `{"name":"Mars","moons":2,"habitable":false}`. The failing response is valid JSON and valid Unicode, but uses `{"Mars":{"moons":2,"habitable":false}}`. The exact semantic validator continues to reject it. In r2, `json-c4-r0-i1` also fails on baseline, leaving only i0 newly failed; baseline i1 passed in r1. Thus even the unchanged baseline varies across the two performance attempts.

The earlier 75-case paired run's zero-new-failure observation is limited to that run; it is not a universal guarantee for those requests. The retained logs show different preceding workloads and concurrent admission order. The [independent request-context review](request-context-review.md) and [hashed evidence](request-context-review.json) preserve the ordering and source details. No cache-state or grammar-state defect is demonstrated. Source/log review found retained fused-cache continuation unavailable, target `kvReused=0`, fresh per-request grammar constraints and no grammar fallback warnings. Qwen3 declines multi-sequence token-batched decode and falls back to serial round-robin execution; attributing this result to a multi-sequence decode GEMM would be unsupported. Different prefill chunk geometry or first-owner migration remain hypotheses without per-step traces. The later [12-case chunk control](../../existing-model-regressions/qwen3-json-chunks/README.md) uses the same recorded request on both builds: chunk limits 256 and 64 each pass twice with the same 21-token flat output; limit 76 fails twice on each build with the same 25-token nested output. This demonstrates the response change under a controlled prefill split on both builds. It preserves all four semantic failures and does not establish which unrecorded chunk sizes occurred in the earlier concurrent runs or the exact rounding mechanism.

## First JSON request: diagnostic observations only

Each fresh server performs a 512-token decode warmup before the first JSON c1 request. Complete requests, outputs and token counts match for Qwen3 and Qwen3.5; Gemma4's first warmup fails on baseline (`{}`, 2 tokens) and passes on final (28 tokens), so its first-use timings are not work-matched.

| First JSON c1 request | Baseline TTFT (ms) | Final TTFT (ms) | Baseline wall (ms) | Final wall (ms) |
|---|---:|---:|---:|---:|
| Qwen3, incomplete r1 | 600.71 | 670.21 | 1,082.35 | 1,176.73 |
| Qwen3, completed r2 | 2,415.30 | 575.37 | 2,938.36 | 1,216.08 |
| Qwen3.5, completed r2 | 564.86 | 655.23 | 1,097.26 | 1,164.05 |

These are one observation per model/version/run, before telemetry. Qwen3's large run-to-run baseline change prevents a simple cold-grammar interpretation. They do not isolate grammar compilation, establish a systematic first-use regression, or substitute for the repeated timed groups. All five JSON warmups per job remain in the original reports, including the five failed Gemma4 baseline warmups.

## Provenance

The r2 runner SHA256 is `10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5`; its shared helper is `cb1b1a0caf232fb8386c89d87cf09a492b1429c6b8665a14d5ab39b9e215c5e4`. The [reviewed protocol and guard report](../../scripts/run-final-json-performance-r2.md) describe the immutable-host and timing requirements. The local auditor is [audit-json-performance-r2.py](../../scripts/audit-json-performance-r2.py). It does not launch inference or rehash weights/VM deployments; those producer checks are identified separately from locally recomputed evidence. Full native/managed hashes, source catalog, failed outputs and timing distributions are in [audit.json](audit.json).
