# Qwen3.5 EventPipe diagnostic: 30/30 pass, latency regression unresolved

Both fresh-host jobs passed all 30 measured JSON cases. Requests, full responses, finish reasons and prompt/completion counts match the completed r2 reference, as do the two 512-token decode warmups and ten JSON warmups. Both jobs retain final native `6b3b5ab3…`, comparing baseline managed with final managed build 3651. **These profiled, fixed-order timings are diagnostic.** The earlier approximately 15 ms solo first-token gap is absent here, so this run cannot establish its cause or resolution. The qualified [alternating latency regression](../qwen35-alternating/README.md) remains preserved.

## Request and collection audit

[audit.json](audit.json) independently verifies all 30 case identities, 12 warmups, exact payloads and outputs, source/report/tool hashes, full recorded deployment maps, settings and collection anchors. Both collector processes exit 0 and report `Trace completed.` with no collector errors. The last warmup ends before attach; readiness precedes every measured request; all 15 client request intervals finish before stop; collector finalization precedes host shutdown in the structured logs. Both owned host and collector PIDs are absent from the subsequent process snapshots.

| Managed build | Request interval (UTC) | Interval length (s) | Collector stop to finalization (ms) |
|---|---|---:|---:|
| Baseline | 14:42:13.028–14:42:15.158 | 2.1300 | 264.09 |
| Final | 14:42:27.209–14:42:29.251 | 2.0427 | 264.33 |

All times are from 2026-09-11. The exact monotonic, UTC/unix and uncertainty anchors are retained in the original `*-eventpipe.json` files; rounded table endpoints are explanatory. The collector's original `trace_integrity` field remains unchanged, recording that raw parsing was pending at capture time. Subsequent raw-trace validation is separate evidence, not a rewritten collection result.

The two local raw traces hash exactly to the collection manifests:

| Managed build | Raw trace bytes | SHA256 |
|---|---:|---|
| Baseline | 6,477,626 | `dfe410e0171637f1affd6cce9da17e99a69fd3c034c1f376d43259d82f03f9e1` |
| Final | 6,487,146 | `b008b29a225983fe7df6a8fafcd5cb9a030885585d9c087481c0a364b0262f58` |

The `.nettrace` files remain in `/tmp/deepseek41-reference/qwen35-eventpipe-final-native-r1/` and their recorded VM paths. ETLX conversions and full sample/runtime-event exports also remain outside the repository. The curated reports contain their hashes and locations.

## Profiled timing observations

All answers contain 16 completion tokens from 92-token prompts. Each c1 median has three observations; each c4 median has 12. These are short JSON responses, not sustained decode measurements. All values and whole-wave times remain in the original reports and audit; no outlier or concurrent wave is discarded.

| Managed build | Concurrency | Client TTFT median (ms) | Request wall median (ms) | JSON decode median (tokens/s/request) |
|---|---:|---:|---:|---:|
| Baseline | 1 | 73.54 | 133.53 | 269.52 |
| Final | 1 | 71.84 | 137.46 | 271.73 |
| Baseline | 4 | 315.37 | 478.74 | 93.40 |
| Final | 4 | 255.01 | 481.25 | 117.81 |

The [six-case solo phase correlation](phase-analysis.json) verifies exactly one native verify phase between each matching server start and completion, with request-ID confirmation in the file log, exact content and zero KV reuse. Baseline/final median pipeline TTFT is 63/64 ms and native verify is 27.99/30.42 ms. The client TTFT arrays are `[70.3010, 73.5444, 144.3418]` versus `[71.8430, 105.6736, 69.1307]` ms. Baseline repeat 2 includes a 23.03 ms native bind phase; final repeat 1 takes 98 ms to the server's first token with a 30.42 ms native verify total. These observations remain; pipeline-minus-native time is an unresolved residual, not a directly measured grammar, scheduler or GC cost. Concurrent native phases remain aggregate.

Both telemetry reports explicitly decline performance qualification, with one in-window sample each. Sampled GPU 7 clocks are 1,740/7,251 MHz, and no foreign GPU client is recorded. Sparse sampling cannot exclude sub-second contention. EventPipe sampling, runtime suspension and rundown, native phase logging, fixed order and the small sample count prevent treating these medians as a replacement for the unprofiled comparison.

## Independent raw-trace analysis

The final r3 [baseline](trace-analysis/baseline-summary.json) and [final](trace-analysis/final-summary.json) parser summaries pass their integrity checks: complete target PID and session coverage of all 15 request windows, zero reported lost events or loss callbacks, no time inversions, and no missing or depth-limited stack records. They bind the exact raw trace, collection metadata and HTTP report hashes. The trace clock mapping uses the readiness UTC anchor plus each recorded monotonic delta; maximum observed anchor drift is 0.5/0.4 microseconds. These checks concern trace integrity and alignment, not exact attribution of every latency component.

| Managed build | Target thread samples in request interval | Engine-worker samples | Samples with unresolved frames |
|---|---:|---:|---:|
| Baseline | 41,825 | 1,673 | 3,365 |
| Final | 41,201 | 1,648 | 3,316 |

The [engine comparison](trace-analysis/engine-comparison.json) preserves categorical counts, selected stack frames and temporal runs for all six solo requests. It requires successful parser integrity and verifies the bound HTTP report plus all sample, stack, runtime-event and suspension export hashes before comparison. Unresolved frames are retained, and managed/native boundary samples can include waiting. Sample counts are never converted into exact CPU milliseconds. Concurrent request windows overlap, so their temporal counts cannot be summed as exclusive per-request work.

Runtime suspension endpoints are paired by CLR instance and initiating OS thread. `SuspendOther`, observed at the profiler's sampling cadence, is retained separately from `SuspendForGC` and `SuspendForGCPrep`; GC collection start-to-stop duration is not treated as pause time. No GC-reason suspension overlaps any of the six solo first-token intervals. This does not exclude other GC activity or establish the cause of the earlier unprofiled regression. Whole-window suspension sums may overlap and are not exclusive stop-the-world time.

Before the first token, samples containing the cache-reset path number `[13, 16, 37]` in baseline and `[13, 42, 15]` in final, in repeat order. Both builds therefore show reset-path residence, including their slower observations. Grammar-category counts in those intervals are `[0, 0, 1]` and `[0, 0, 0]`. These sparse, instrumented samples support further inspection; they do not prove that cache reset caused the earlier regression or that grammar has zero cost. The profiled median does not reproduce that regression.

The [42 parser/reducer/negative checks](../../scripts/eventpipe-trace-analysis/checks.json) include both real traces and rejected wrong-PID, truncated-trace, incomplete-collector and mismatched-metadata cases. The [11 comparison checks](../../scripts/eventpipe-trace-analysis/comparison-checks.json) reject failed integrity and substituted or missing exports. Final parser source SHA256 is `b89ebf81fca994b8e793bc275e928cf415238403b95486c30223f4e208f712f9`; comparison source is `f2d530cb49cc27ce1c524b0f4c61a1d9242e56adb9da34166f207e5e926499d1`. Only final r3 summaries are curated here; exploratory earlier parses are not used as evidence.

## Protocol and source identity

Each unchanged r2 `run_version` completes its decode and JSON warmups before EventPipe attaches. The host receives `DOTNET_EventPipeThreadSamplingRate=1` and `TS_GGML_PHASE_TIMING=1`. The exact per-job CLI is retained in each collection report:

```sh
dotnet-trace collect --process-id HOST_PID \
  --profile dotnet-sampled-thread-time,dotnet-common \
  --providers Microsoft-Windows-DotNETRuntime:0x100003C01D:4 \
  --format NetTrace --output JOB.nettrace --buffersize 128
```

The collector uses the explicitly recorded `DOTNET_ROOT`, receives SIGINT after the full request interval and completes while the host is still running. The CLR provider preserves the common profile's bits and adds contention events. Sampled thread stacks include waiting; counts are not exact on-CPU milliseconds or GPU timings.

[tool-evidence.json](tool-evidence.json) pins `dotnet-trace` version `10.0.745401+cef304c50763bf24f99566cb31d55540842e7ae9`, 54 installed tool files and five help/version outputs. The [collect help](tool-help/dotnet-trace-collect-help.txt) and other help files are copied byte for byte. Producer checks verified the actual VM tool and frozen host files before and after collection; the local audit verifies the pulled manifests, help outputs, reports and raw trace hashes. Model content SHA reuse is explicitly recorded with matching path, size and nanosecond mtime. No VM deployment or model rehash is claimed by the offline audit.

Runner SHA256 is `d4cf63ed593fde16596b433c488470704ccaa9d2180a949b12625e07eff5492f`; unchanged r2 executor is `10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5`. The [runner](../../scripts/run-qwen35-eventpipe.py), [32 simulated lifecycle guards](../../scripts/qwen35-eventpipe-checks.json), [local auditor](../../scripts/audit-qwen35-eventpipe.py) and [solo phase analyzer](../../scripts/analyze-qwen35-eventpipe-phases.py) are snapshotted in [the source index](../../scripts/sources.json). Original collector output, child reports, native phases, stdout, file logs and telemetry are retained byte for byte. The [artifact manifest](artifact-manifest.json) hashes the curated evidence; no raw trace or full sample export is committed.

The [offline parser project](../../scripts/eventpipe-trace-analysis/trace-analysis.csproj) references the recorded tool assemblies in a sibling `dotnet-trace-tool` directory and explicitly checks TraceEvent assembly SHA256 `7ad04f5abbd704e8bd9c7b29f9aeab951f20cdc5d5c8e701f256be52a6e8543e`. Restore those files and the retained raw data before rebuilding or rerunning it. It accepts `--trace FILE --metadata FILE --report FILE --out NEWDIR` and refuses existing output. The [comparison](../../scripts/eventpipe-trace-analysis/compare.py) uses the recorded final r3 local paths. These are measurement-specific snapshots; the portable HTTP harness remains under `benchmarks/engine_comparison`.

The subsequent [72-case uninstrumented solo control](../qwen35-solo72/README.md)
passes all exact requests with final native held fixed. Its two complete 18-request
comparisons do not reproduce the earlier slower final medians. Both directions
and all outliers remain recorded; no new production change, causal warmup claim
or source-level fix is credited.
