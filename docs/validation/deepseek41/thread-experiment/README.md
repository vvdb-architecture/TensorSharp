# One-thread TP experiment, 2026-09-11

The explicit one-native-CPU-thread configuration was slower on the full Q2 TP8
model. Its completed quality suite passed 30/30 and its separately planned
single-request decode control passed 3/3. The original outer suite was
intentionally stopped; this is not a complete placement validation.

| Completed measurement | Earlier TP baseline | Explicit one thread |
|---|---:|---:|
| Quality suite wall time, 30 cases | 151.800 s | 472.311 s |
| Native quality prefill, exactly 10,330 tokens | 49.013 s | 317.516 s |
| Native quality decode, exactly 1,318 calls | 100.034 s | 150.246 s |
| Median c1 decode, 512 generated tokens | 15.417 tokens/s | 10.083 tokens/s |
| Median c1 TTFT | 345.636 ms | 1,569.657 ms |
| Median c1 request wall time | 33.492 s | 52.250 s |

The three decode requests have identical request hashes, output hashes, prompt
counts and 512-token completion counts. They request neither tools,
response-format grammar nor thinking. Their median stream-window decode rate
fell 34.6%, with request wall time increasing 56.0%. All 30 quality initial
request hashes also match; all 50 measured turns have identical output text,
tool function names/arguments, prompt counts and completion counts. Generated
tool-call IDs can differ between runs.

Both runs use the same native library
`b26cac3e40ff67b6de077063cd7a3c68e683220f0bc60237edf728ec6d218f1f`,
weights, eight GPUs, routed-MoE TP8, zero CPU-offloaded expert layers, context
65,536, native ubatch 1,024, scheduler prefill/solo chunks 1,024, four running
slots, F16 KV, sparse attention and compact raw gather. Their recorded
environments differ only in `TS_CPU_MOE_THREADS=1`. The old CLI requested 48
threads, but that b26 native DSV path did not honor the CLI override. Its
effective default of 32 is inferred from the loader/source path; the historical
run did not directly record that thread count.

This is an observed configuration regression, with limits on causal attribution.
The managed Runtime changed from the 3,566-test stage to the 3,597-test stage;
all binary hashes are retained in the report. Native work and output equality
narrow the possible explanations, but these runs are not an isolated,
same-build thread-count A/B. No CPU-MoE expert was offloaded: the setting also
controls the native CPU backend used by the TP host execution path.

Only `quality` and `thread-control` are retained as complete, qualified
measurement labels. The original `placement` and outer profile reports remain
`run_complete=false` and `all_passed=false`. The abandoned steady report contains
one of 15 planned cases; the next request was cancelled after 78 tokens. Neither
is counted in the completed control. Long, tools, protocol and media phases
were not run for this experiment. Startup overlapped other work and remains
unqualified.

All native byte-range hashes and counters were independently recomputed. Quality
accounts for 10,366 logged prompt tokens minus 36 reused tokens, and 1,267
generated tokens plus 51 forwarded EOS tokens, including warmup. The separate
control accounts for 155 prefill tokens and 1,538 decode calls: 1,537 generated
tokens plus one EOS, including warmup. Neither completed phase has preemptions.

The quality log contains one HTTP/Kestrel `FlushAsync`
`OperationCanceledException` after a completed 65-token tool response and the
client's next workflow request. The adjacency suggests a transport-finalization
race, but no request-ID correlation establishes that cause. All 51 completions
including warmup and all native work reconcile. This log error is preserved;
30/30 harness success does not make the log error-free. The separate decode
control contains no error lines.

Sampled GPU clocks were 1,740/7,251 MHz throughout the one-thread telemetry.
The earlier run had the same medians, with a few lower SM-clock samples. A
bounded read-only process inspection during quality found no competing busy
process. These observations provide no sampled clock or competing-job
explanation for the slowdown; they do not establish CPU-thread causality.
Periodic telemetry does not certify every instant, and current placement
telemetry also includes the subsequently aborted steady segment.

[Report with all comparisons, checks, source hashes and retained log errors](report.json).
The report's integrity checks all pass; its separate
`all_completed_logs_error_free` field is false.

Reproduce from the recorded raw and curated artifact directories:

```sh
python3 docs/validation/deepseek41/scripts/analyze-thread-experiment.py \
  --raw /tmp/deepseek41-reference/final-raw \
  --curated docs/validation/deepseek41/full-checkpoint \
  --output /tmp/thread-experiment-report.json
```

The [analysis source](../scripts/analyze-thread-experiment.py),
[original wrapper](../scripts/run-final-suite.py),
[separate control wrapper](../scripts/run-final-suite-thread-control.py), and
[intentional-stop helper](../scripts/stop-thread-experiment.py) are preserved
with [exact snapshot hashes](../scripts/sources.json). Source paths differ if
reproduced elsewhere, so the regenerated report hash may change while its
measurements and source content hashes remain equal.
