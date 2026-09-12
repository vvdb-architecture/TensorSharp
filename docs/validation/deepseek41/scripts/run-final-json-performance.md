# VM-specific final JSON grammar performance comparison

Prepared locally; no real inference or timings have been collected by this runner.
The portable fixture and SSE collector remain in `benchmarks/engine_comparison`.
This snapshot orchestrates the requested VM and preserves complete raw case reports.

After the final75+15 correctness rerun has finished, and root has released an exclusive
CPU/GPU/I/O measurement window with all full-model hosts stopped:

```sh
/workspace/deepseek41-work/venv/bin/python \
  /workspace/deepseek41-work/run-final-json-performance.py \
  --exclusive-window
```

The runner depends on the reviewed sibling `run-final-existing-models.py`, whose
SHA256 is pinned in source. It does not build, download, modify clocks, start a
second resident model, or mutate any earlier deployment/report. Use `--plan-only`
to print the job plan without reading VM inputs or creating files. To preserve a
second experiment, pass a new `--label`; existing output or deployment directories
are refused. A `STOP` file under the new output directory stops before the next
wave. Only the runner's owned server process group is terminated.

The defaults are:

- Baseline source: `/workspace/TensorSharp-baseline/TensorSharp.Server.Host/bin`.
  A small read-only VM check confirmed Runtime SHA256
  `abde2278d6c9b885c18dc37e2d93c8f273aecc68568f13c49c31cd0edc4bdc4b`
  and native `e67e4c922138a7038bfc07d110d22eeec0f3f4cf10d545fae2971ca3f7dc3093`.
  All six core binaries must match retained historical baseline reports.
- Final source: `/workspace/deepseek41-work/regression-final3651-native6b3/TensorSharp.Server.Host/bin`,
  the isolated deployment produced by the preceding90-case correctness runner.
  Its six core binary hashes must match all preceding correctness reports. The new
  copy receives only archived native6b3, SHA256
  `6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014`.
- Prior evidence: `existing-regressions/performance-baseline` and
  `existing-regressions/final3651-native6b3/run.json` beneath the VM work directory.
  The latter must account for all75 original and15 Unicode cases. Known failures
  do not prevent measurement, but incomplete or altered child reports do.
- New frozen deployments: `/workspace/deepseek41-work/regression-final3651-native6b3-json-performance/{baseline,final}/bin`.
- New output: `/workspace/deepseek41-work/existing-regressions/final3651-native6b3-json-performance/`.

Both versions use the exact old GPU7/context8192/F16/slots4/prefill256 settings,
CPU/OpenMP4, no speculative decoding, no prefix cache, no repetition stop,
deterministic sampling, and streaming. The initial JSON request hashes for every
case must equal those in the old performance reports before any server starts.
Weights reuse the earlier content hash only when path, byte size, and nanosecond
mtime still match; otherwise they are hashed before measurement.

| Model | Version order | Timed cases per version |
| --- | --- | ---: |
| Qwen3-0.6B Q8_0 | baseline, final | JSON c1/c4 ×3 repeats =15 |
| Qwen3.5-0.8B Q8_0 | final, baseline |15 |
| Gemma4-E2B-it Q4_K_M | baseline, final |15 |

Each server first performs one unmeasured512-token decode warmup, then five JSON
warmup requests (one c1 and one c4 wave). The90 timed cases and36 warmups are
retained separately. No request/token limits are changed to manufacture a longer
JSON answer. This is short-response JSON decode throughput, **not** sustained
512-token decode performance.

Per-concurrency timing ratios require every planned response in that group to
pass the unchanged validator, and every paired request, assistant message,
finish reason, prompt count, and completion count to match exactly. The runner
does not select a convenient successful subset. It preserves full failed cases
and identifies every incomparable pair. Timings are reported for matching groups
as median TTFT, request wall time, whole-wave wall time, and total output tokens
divided by whole-wave wall time. Decode throughput has its own eligibility:
matching timer sources, positive finite timings, and at least50ms of measured
streamed decode when using the SSE estimator. A shorter/missing SSE interval
withholds only that decode ratio; eligible TTFT/request/wave metrics remain.
Raw timing values remain available regardless of comparison eligibility.

Telemetry samples all GPU clients, GPU clocks/power/temperature, and safe process
names with `/proc` CPU tick deltas once per second. Qualification requires the
external exclusive-window declaration, at least two in-window samples, the owned
GPU7 client, no foreign GPU client, no foreign CPU process using over10% of one
core between samples, SM clock spread at most3%, memory clock spread at most0.5%,
and paired median clocks within those bounds. Sampling cannot establish every
in-flight clock or exclude sub-second interference; the report states this.
The runner changes no clock/power settings and captures no unrelated process
command-line arguments. Failed telemetry qualification preserves measurements
but withholds qualified ratios.

`run.json` records source/helper/harness hashes, immutable deployment hashes,
the ordered six-job plan, expected counts, each child hash/status, all failed
cases, and separate completion/correctness/comparability fields. All versions
and models are attempted even if one fails. Nonzero exit means incomplete work,
a recorded correctness failure, or incomparable groups; it never deletes or
overwrites the evidence. This whole-build A/B can detect a regression in JSON
execution, but cannot by itself attribute a change solely to the Unicode fix
because other managed and native binaries differ.

Local checks use simulated HTTP/process/telemetry responses with the real
portable fixture generator and validator; they start no native model. See
`final-json-performance-checks.json` and `check-final-json-performance.py`.
