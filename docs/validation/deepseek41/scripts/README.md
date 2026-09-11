# VM-specific measurement snapshots

These are byte-for-byte snapshots of the local helper scripts used to launch,
collect, and analyze the DeepSeek V4.1 VM measurements. They retain the original
VM/local paths and are reviewable source artifacts, not a portable benchmark
entrypoint. No script was run as part of this snapshot operation.

The primary portable harness remains
[`validate_inference.py`](../../../../benchmarks/engine_comparison/validate_inference.py),
[`validate_deepseek41_tools.py`](../../../../benchmarks/engine_comparison/validate_deepseek41_tools.py),
and [`validate_deepseek41_media.py`](../../../../benchmarks/engine_comparison/validate_deepseek41_media.py).
The snapshots' paths must be adapted, or the recorded directory structure
restored, before executing them on another machine.

| Snapshot | Role and version scope |
|---|---|
| [analyze-placements.py](analyze-placements.py) | Read-only profile/settings/completeness and matched-request analysis; requires an explicit manifest |
| [analyze-thread-experiment.py](analyze-thread-experiment.py) | Read-only one-thread experiment audit with request/output matching, native counter reconciliation, and explicit exclusion of the aborted remainder |
| [audit-cpu4-placement.py](audit-cpu4-placement.py) | Historical CPU-MoE4 baseline audit, preserving the original failure and separately validating the serial-tools policy |
| [audit-final-placement.py](audit-final-placement.py) | Final placement/serial case and native-log audit; validates the wider outer plan while explicitly leaving other phases outside its numerical scope |
| [audit-first-tp.py](audit-first-tp.py) | Read-only first-TP child-report and native log-range reconciliation; expects the raw logs locally |
| [placement-first-tp-manifest.json](placement-first-tp-manifest.json) | Exact manifest used for the first-TP analyzer report, including intended launch fields and report counts |
| [run-final-suite.py](run-final-suite.py) | **Updated** suite runner, with phase completeness, child JSON validation and failure propagation; this is not the earlier wrapper that launched the first TP run |
| [run-final-suite-thread-control.py](run-final-suite-thread-control.py) | Later 2026-09-11 snapshot adding the three-repeat, single-request decode control; the preceding 7d83 runner snapshot stays unchanged |
| [run-final-suite-serial-tools.py](run-final-suite-serial-tools.py) | Later local snapshot adding a separate ten-case serial-tools follow-up; not executed when snapshotted, earlier wrappers remain unchanged |
| [stop-thread-experiment.py](stop-thread-experiment.py) | VM-specific intentional-stop helper for the one-thread experiment; waits for complete quality and explicitly marks the unfinished outer plan incomplete |
| [run-profile.py](run-profile.py) | Outer phase orchestration; continues later phases after nonzero child exits and records all results |
| [launch-final.py](launch-final.py) | Current VM launcher; copies a chosen native library into an isolated host folder, records binaries/settings, and starts the server |
| [prepare-final-profile.py](prepare-final-profile.py) | Stops the recorded preceding host, creates a new immutable deployment, and waits for model loading plus diagnostic source warming before inference |
| [collect-final.py](collect-final.py) | Curates raw JSON while preserving cases and request hashes; qualification flags reflect supplied labels or previously reviewed output flags |
| [collect-final-http-rejections.py](collect-final-http-rejections.py) | Later collector preserving numeric HTTP rejection reports separately from inference case reports |
| [run-final-existing-models.py](run-final-existing-models.py) | Isolated final managed/native existing-model runner: exact 75 historical cases plus 15 separate Unicode cases; prepared before execution |
| [run-final-json-performance.py](run-final-json-performance.py) | Prepared JSON-only HEAD/final A/B: 90 timed cases across three models, with 512-token decode and JSON warmups retained separately; staged but not executed |
| [final-layer8-audit-manifest.json](final-layer8-audit-manifest.json) | Reviewed intended final3651/native6b3 layer profile; seven outer phases, with only placement and serial-tools numerically audited by this helper |
| [summarize-final-telemetry.py](summarize-final-telemetry.py) | Per-GPU min/median/max summaries of sampled CSV telemetry, with source hashes |

The runner fix was made after the first TP baseline. The launch helper also
contains later explicit CPU-thread options. Neither is claimed to be an unchanged
historical source file from every earlier run. The later thread-control wrapper
and stop helper were snapshotted separately on 2026-09-11 after the one-thread
experiment was intentionally stopped. The stop helper retains only process
commands matching that experiment and its descendants; no raw process snapshot
is included here. Exact executed commands, binary
hashes, child JSON and raw log-range hashes are retained in each measurement's
launch/run/report artifacts. The first-TP audit independently validates the old
run's children and counters instead of relying on its wrapper exit status.

All snapshot bytes, original local paths and SHA-256 values are recorded in
[sources.json](sources.json). The first-TP analyzer and audit hashes also match
their corresponding report provenance. The manifest still references the
recorded checkout path; adjust it in a new copy and retain that copy's hash if
re-running elsewhere.

From a checkout with the recorded raw artifacts restored:

```sh
python3 docs/validation/deepseek41/scripts/analyze-placements.py \
  docs/validation/deepseek41/scripts/placement-first-tp-manifest.json \
  /tmp/placement-first-tp-report.json
python3 docs/validation/deepseek41/scripts/audit-first-tp.py
```

`performance_qualified` is a declared property of the measurement window, not a
qualification independently established by the collector. Verify the associated
run/telemetry evidence when reusing or overwriting an artifact. Periodic GPU
samples cannot establish clocks or exclusive resource use at every instant.

The prepared final existing-model follow-ups have separate usage notes:
[75 historical plus15 Unicode correctness cases](run-final-existing-models.md)
and [90 timed JSON A/B cases](run-final-json-performance.md). The latter pins the
former as a shared helper and requires its complete75+15 report before starting.
It preserves all failures and permits timing ratios only for groups with exactly
matched requests, responses and token counts. JSON responses are short; their
decode throughput is not a sustained512-token measurement. Decode eligibility
is independent of TTFT and request/wave wall-time eligibility. A new output and
deployment directory prevents overwriting earlier reports or modifying hosts.

The [seven correctness-runner checks](final-existing-models-checks.json) and
[23 JSON-runner checks](final-json-performance-checks.json) use simulated HTTP,
process and telemetry responses with the real portable request/response harness.
They are not model inference results. Their exact test sources are
[check-final-existing-models.py](check-final-existing-models.py) and
[check-final-json-performance.py](check-final-json-performance.py). The JSON
checks include rejecting nonfinite clocks and stopping every owned server even
when telemetry finalization fails. The [job plan](final-json-performance-plan.json)
and [source manifest](final-json-performance-sources.json) record the prepared
version; no VM benchmark was executed while taking these snapshots.

The concrete layer manifest pins final3651 Chat/Runtime/Server and native6b3;
the earlier generic templates described in [the audit notes](audit-final-placement.md)
remain distinct. Its [17-check local plan review](final-layer8-plan-review.json)
verifies source hashes,32 CPU threads, CPU-MoE0, eight selected GPUs with the true
TP switch disabled, and the exact staged suite definitions. Placement47 plus
serial-tools10 total57 cases within the helper's numerical scope. Tools,
protocol, media, multilingual and long-parallel are checked only as complete
outer phase envelopes by this auditor; their independent case reports still
need review. Neither this plan review nor the declared qualification labels
assert that the final layer inference has completed or passed.

The later [JSON r2 protocol](run-final-json-performance-r2.md) fixes the inherited-log
snapshot error from the [incomplete first30/90 attempt](../json-performance/incomplete-r1/README.md).
It redirects supported per-job file logging outside each frozen deployment; no
frozen binary/configuration checks are removed. The [r2 runner](run-final-json-performance-r2.py)
and [27-check report](final-json-performance-r2-checks.json) remain separate from the
original5794/23-check preparation snapshots above. All90 requests are repeated
under a new label, with original failures and exact validators preserved.

The separate [60-case Qwen3.5 alternating control](qwen35-json-control.md)
reuses the unchanged r2 executor and frozen deployments, reversing version order
in its second complete pair. Its [11 local guards](qwen35-json-control-checks.json)
verify full prior-source/child/deployment checks, all four jobs, retained failures
and raw latency distributions. [Completed r2 evidence](../json-performance/completed-r2/README.md)
remains separate. These snapshots retain original filenames; to replay outside
the VM staging layout, provide the reviewed10ab r2 runner under the sibling
`run-final-json-performance.py` name rather than the older5794 historical snapshot.

The [four-job Qwen3.5 managed/native crossing](run-qwen35-cross-phase.md)
uses the same r2 executor with native phase timing enabled and explicitly
diagnostic status. Its [40 simulated guards](qwen35-cross-phase-checks.json)
remain separate from the [completed 60-case evidence](../json-performance/qwen35-cross-phase/README.md).
The archived runner, actual loaded-library observer, independent case auditor
and solo phase analyzer are indexed with their exact source hashes. The offline
auditors require the recorded raw-artifact layout; none launches inference.

The [completed EventPipe diagnostic](../json-performance/qwen35-eventpipe/README.md)
uses the [two-job runner](run-qwen35-eventpipe.py) with capture after all warmups
and collector rundown before host shutdown. The separate offline case auditor,
solo phase correlation and final r3 [trace parser project](eventpipe-trace-analysis/trace-analysis.csproj)
preserve request, trace and source identities. Local checks cover 32 collector
lifecycle cases, 42 parser/reducer/negative cases and 11 comparison guards.
Raw `.nettrace`, ETLX and full sample exports remain outside the repository;
compact summaries and hashes are linked from the completed evidence.

The [completed 72-case solo control](../json-performance/qwen35-solo72/README.md)
holds final native fixed across four fresh processes and repeats the exact three
original solo HTTP requests six times per job. Its [plan](run-qwen35-solo72.md),
[43 simulated guards](qwen35-solo72-checks.json), offline audit and standard-log
correlation are separately indexed. All whole-18 observations and the predeclared
first-three/later-15 descriptive groups remain; this follow-up makes no production
change and does not erase the earlier measured latency regression.
