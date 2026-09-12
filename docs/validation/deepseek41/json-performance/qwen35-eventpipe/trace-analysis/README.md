# Qwen3.5 EventPipe analysis

Both retained traces parsed without reported event loss, time inversions, missing sampled stacks, or depth-limited sampled stacks. The target PID was observed throughout each recorded 15-case window, and the trace session enclosed that window. The parser is the exact TraceEvent 3.1.23 assembly shipped in the pinned dotnet-trace 10.0.745401 bundle; its SHA-256 is `7ad04f5abbd704e8bd9c7b29f9aeab951f20cdc5d5c8e701f256be52a6e8543e`.

| Observation | Baseline managed / final native | Final managed / final native |
|---|---:|---:|
| Target PID | 148222 | 148403 |
| Engine worker OS TID, identified by stack | 148332 | 148519 |
| Parsed target events, full session | 75,227 | 74,137 |
| Target SampleProfiler samples in case window | 41,825 | 41,201 |
| Engine-worker samples in case window | 1,673 | 1,648 |
| Samples with an unresolved frame, all threads | 3,365 | 3,316 |
| Engine samples with an unresolved frame | 0 | 0 |
| Events lost / time inversions / missing stacks | 0 / 0 / 0 | 0 / 0 / 0 |
| Largest UTC/monotonic anchor discrepancy | 0.5 µs | 0.4 µs |

Session UTC ranges are 2026-09-11 14:42:12.942–14:42:15.316359 and 14:42:27.148–14:42:29.4140157. Exact request-window endpoints, all 15 per-request intervals, and raw source identities are in each `summary.json`. The raw metadata's collection-ready UTC anchor plus each request's monotonic delta provides the alignment. Anchor uncertainty is retained; this is not a kernel clock correlation. TraceLog's thread start/end metadata can be synthesized because the trace attaches to a live process: the enormous unknown-end sentinel is preserved but is not an observed thread lifetime.

The profiled solo TTFTs were baseline 70.301, 73.544, 144.342 ms and final 71.843, 105.674, 69.131 ms. Their medians do not reproduce the earlier unprofiled final-build regression. Profiling is intrusive and these timings remain diagnostic.

The baseline r2 outlier contained 37 engine samples in cache reset from request offsets 8.605–53.629 ms. The final r1 outlier contained 42 reset samples at 6.234–56.490 ms. Other solos had 13–16 reset samples. In the outliers, 22 baseline and 23 final samples were in `ModelBase.InvalidateTensorDeviceCache → ResetCacheTensor → Qwen35Model.ResetKVCacheCore → BatchExecutor.EnsureOwnership`; additional reset stacks included `GgmlBasicOps.Fill` and `Qwen35ResetDecodeCache`. Native verify boundary samples followed these reset spans. These observations locate sampled thread residence and include waits; they are not measured CPU time or exact function durations.

No suspension with reason `SuspendForGC` or `SuspendForGCPrep` overlapped any of the six solo first-token intervals. In the full 15-case windows, baseline had three such spans (0.855, 4.979, 2.0548 ms), and final had two (40.917, 0.6629 ms), all outside those solo intervals. Runtime `SuspendOther` events closely track the profiler cadence and are kept separate. They must not be relabeled GC pauses. Pairing uses CLR instance plus the initiating OS thread, so simultaneous sampler and GC suspension attempts do not overwrite one another. Their spans can overlap and must not be summed into exclusive pause time. No contention event appeared in the baseline solo outlier; the final outlier included a ~0.139 ms contention event on another thread, not the engine worker.

The evidence identifies common reset work in these two profiled outliers. It does not establish the cause of the earlier unprofiled median regression, its absence under other conditions, or a safe implementation change. Standard EventPipe exposes managed stacks and native transition boundaries here; it does not supply native CPU stacks, GPU execution time, or an OS scheduler on/off-CPU timeline.

## Local artifacts and reproduction

Raw captures remain at `/tmp/deepseek41-reference/qwen35-eventpipe-final-native-r1/`, with original VM locations recorded in the capture metadata. Final parser exports are under `/tmp/deepseek41-reference/qwen35-eventpipe-analysis-baseline-r3/` and `qwen35-eventpipe-analysis-final-r3/`. Each contains `summary.json`, `.etlx`, complete timestamped SampleProfiler rows, runtime GC/threading/contention rows, stack definitions, suspension spans, and conversion diagnostics. The bounded comparison is `/tmp/deepseek41-reference/qwen35-eventpipe-engine-comparison.json`. Do not copy `.nettrace`, `.etlx`, or full timestamped exports into the repository.

`Program.cs` builds with `dotnet build trace-analysis.csproj -c Release`, using only the three shipped local assembly references. No packages are fetched. To parse a capture, supply `--trace FILE --metadata CAPTURE_JSON --report REQUEST_JSON --out NEW_DIRECTORY`. Existing output directories are rejected. The request report must bind the exact capture metadata hash, the raw trace must match that metadata hash/size, and versions, intervals, all 15 unique cases, ordered anchors, and target PID are checked. Parser exceptions preserve a failed summary and do not silently enable tolerant parsing. The reducer's `--self-test` plus `check.py` provide 42 local checks including truncated traces, wrong PID, mismatched provenance, invalid timing, and both real traces. `check.py` is a single-use evidence run whose fixed `-r3` output directories must not already exist; use a separate scratch copy with fresh directory names to repeat it without changing retained outputs.

`compare.py` requires successful complete analysis and verifies the bound HTTP report and all four consumed export hashes before interpreting stacks. Its 11 guards include failed integrity and substituted/missing exports. Python timestamp parsing truncates .NET's seventh fractional digit to Python's microsecond precision; displayed offsets therefore have up to 0.9 µs truncation. Categories are explicit, ordered string matches in the source. Per-request sample windows overlap for concurrent requests and are temporal correlations, not request-attributed CPU work. Unknown and unresolved samples remain in the raw exports and counts.

An initial exploratory parser output under `qwen35-eventpipe-analysis-baseline/` paired runtime suspensions only by CLR instance, producing two pairing errors where the sampler and GC overlapped. It is preserved locally, is excluded from final evidence, and was corrected to instance-plus-thread pairing with a regression check. The final `-r3` exports use the reviewed corrected implementation and retain every sample.

Primary API/context references: [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace), [EventPipe](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe), and the [TraceEvent programmer guide](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md). Exact API behavior was exercised against the pinned local assembly, not inferred from latest upstream defaults.
