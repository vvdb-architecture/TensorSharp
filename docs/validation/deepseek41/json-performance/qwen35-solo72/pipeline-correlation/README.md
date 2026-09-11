All **72 requests** matched their standard structured and stdout server logs. Each retained the exact original R2 HTTP payload for its tag, 92 prompt tokens, 16 completion tokens, zero KV reuse and the same correct JSON content. The tags repeat six times per process; separate logical iteration IDs are matched to the 18 sequential, unique server request IDs. No native phase lines were emitted.

Both qualified comparisons include all 18 requests per build with final native `6b3b5ab3…` held fixed:

| Pair | Managed builds | Client first-token median | Server first-token median | Client request-wall median |
|---|---|---:|---:|---:|
| 1 | Baseline job0 → final job1 | 67.540 → 64.362 ms | 59.5 → 58 ms | 124.216 → 118.670 ms |
| 2 | Baseline job3 → final job2 | 69.892 → 65.382 ms | 64 → 58 ms | 125.147 → 121.487 ms |

The earlier approximately 15 ms solo first-token increase is absent from these two full comparisons. This broader, uninstrumented control used four fresh processes and made no production change. It does not erase the earlier measured slower runs or establish a source-level fix.

The first3/later15 groups were declared before execution and remain descriptive. Their client first-token medians preserve the mixed behavior:

| Pair | First3, baseline → final | Later15, baseline → final |
|---|---:|---:|
| 1 | 67.611 → 68.779 ms | 67.498 → 62.371 ms |
| 2 | 87.711 → 65.351 ms | 64.276 → 65.413 ms |

All observations, including the second baseline process's slower early requests, remain in the full comparisons. The later15 portion of pair2 is slightly slower for final, so the result is not a claim that every subgroup improved. No native phase instrumentation was enabled; these pipeline timings cannot separate cache reset, grammar, GC, scheduling or native execution costs.

[analysis.json](analysis.json) retains all 72 request IDs, exact request hashes, timestamps, source line numbers, client/pipeline/wave timing arrays, binary and raw-log hashes, and every predeclared median. [analyze-qwen35-solo72.py](analyze-qwen35-solo72.py) reproduces the correlation from the full local raw directory `/tmp/deepseek41-reference/qwen35-solo72-final-native-r1/`. The independent suite audit owns telemetry, immutable-file and complete-run qualification; this artifact supplements it with exact standard-log correlation.
