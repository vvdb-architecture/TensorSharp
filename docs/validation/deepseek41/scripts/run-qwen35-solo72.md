This control holds final native `6b3b5ab3…` fixed and runs four fresh processes in baseline/final/final/baseline order. It reuses the two immutable final-native hosts from the reviewed crossing; every original host file is checked before and after execution. One `/proc/PID/maps` read verifies the selected native path after readiness and before warmups. No mapping observer runs during measurement.

Each process receives the unchanged R2 512-token decode warmup and five JSON warmups, followed by six repetitions of the exact original solo requests tagged `json-c1-r0-i0`, `json-c1-r1-i0` and `json-c1-r2-i0`. HTTP payloads and tags remain unchanged. Separate logical iteration IDs distinguish the 18 observations. Request dispatch uses R2's original single-worker `run_wave`; every per-request and whole-wave time is retained.

Both primary comparisons use all 18 paired requests: job0 baseline versus job1 final, and job3 baseline versus job2 final. First3/later15 groups are descriptive and predeclared. No failure or outlier is removed. Qualification requires complete exact requests, responses and token counts, passing warmups, unchanged sources, an explicit exclusive window, and the established telemetry/clock rules. An interval with fewer than two telemetry samples is refused without sleep padding.

Inherited `DOTNET_`, `COMPlus_`, `CORECLR_`, `TS_`, `GGML_` and related model/log prefixes are removed. Only the ordinary pinned R2 environment and the supported external log directory are added. No profiling, phase or debug settings are introduced. Unexpected native phase output fails the job.

After the parent releases the exclusive VM window:

```sh
/workspace/deepseek41-work/venv/bin/python \
  /workspace/deepseek41-work/run-qwen35-solo72.py --exclusive-window
```

The default new output folder is `/workspace/deepseek41-work/existing-regressions/qwen35-solo72-final-native-r1`. Existing output is refused. The stop marker is `/workspace/deepseek41-work/stop-qwen35-solo72`. Only owned processes are stopped. A cleanup failure after observed process exit retains the failed report and permits safe later jobs; an unconfirmed live host retains its active marker and blocks continuation.

Frozen runner SHA: `344382bbf8b4eb1170886c3cafbad5c0774544e18ade9a0485a06e85950432f4`. Forty-three new local guards plus the 40 prerequisite crossing guards passed. The final independent review accepted that source. These guards simulate HTTP/processes/telemetry; they are not model or performance results. `qwen35-solo72-checks.json` records the source and guard hashes.
