# Final3651/native6b3 existing-model rerun

Prepared source: `run-final-existing-models.py`. This new helper leaves the
original regression runner, old75-case reports and every source/running host
unchanged. It creates a new deployment and output directory and refuses either
already existing, so a rerun needs an explicit new label.

After all full-model placements and the final managed build are complete,
upload this helper and run on the requested VM:

```sh
/workspace/deepseek41-work/venv/bin/python \
  /workspace/deepseek41-work/run-final-existing-models.py \
  --source-host-dir /workspace/deepseek41-work/server-v41-final3651 \
  --native /workspace/deepseek41-work/native-v41-shared-pins-6b3b5ab3.so \
  --label final3651-native6b3
```

No build/download occurs. The source host must already contain a complete
published deployment. The native source must exactly match
`6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014`.
An optional `--expected-runtime-sha256` pins the intended managed Runtime in
addition to recording all actual deployment hashes. The source host path is
explicit because later managed error-path fixes may change the final folder;
review it against the completed final3651 build before execution.

New deployment:
`/workspace/deepseek41-work/regression-final3651-native6b3/TensorSharp.Server.Host/bin`

New artifacts:
`/workspace/deepseek41-work/existing-regressions/final3651-native6b3/`

- `run.json`: ordered model/suite plan, complete/status counters, source hashes,
  and comparisons to both preserved references.
- `quality/{qwen3,qwen35,gemma4}-tensorsharp.json`: exact old75 requests,25/model.
- `unicode/{qwen3,qwen35,gemma4}-tensorsharp.json`: separate15 Unicode requests.
- Per-model server logs, safe process-name/GPU snapshots, launch configuration,
  weight identity, binary hashes and all response/status data.
- Deployment `deployment.json`: all copied file hashes, checked unchanged
  before/after each model. The active source host is never overwritten.

Models and exact previous settings are retained: Qwen3-0.6B Q8_0,
Qwen3.5-0.8B Q8_0, Gemma4-E2B Q4_K_M; GPU7, context8192, four slots,
scheduler256, F16 KV, no speculation/prefix cache/repetition stop, four
CPU/OpenMP threads, greedy seed42, concurrency1/4 and one repeat. The75-case
suite remains short/decode/json/multi_turn/tool_round_trip. No serial workflow
constraint or changed final-tool JSON policy is added. The new Unicode suite
uses json_unicode and runs after quality on each same model server; its results
never enter the historical75 comparison.

The helper hashes its own source plus the portable validation/engine/scenario
modules, requires exact25 unique reference cases, and compares every old request
hash against both `existing-regressions/quality-baseline` and `.../quality`.
Missing/mismatched coverage cannot become a regression pass. All partial/failing
reports remain visible. New case failures retain full before/current cases;
changed outputs and model/binary hashes remain available. Existing strict-format
failures are not relabeled successful. Inspect a new failure under the current
assistant-content-only validation before attributing a numerical regression.

`run_complete` becomes true only for all90 planned logical cases. Status remains
failed when any known or new case fails; exit1 is therefore possible even with
zero newly failed historical cases. The report's per-reference introduced-case
counts distinguish this from a new regression. Unicode has no historical
baseline and is reported as new coverage. All timings are explicitly unqualified.

Only one owned model server runs at a time on port5011; an occupied port is
rejected. Startup timeout180s, per-request timeout90s. Each server process group
is stopped in a finally block before the next model. To stop between request
waves, create the run's `STOP` file, or pass a separate `--stop-file`. A stop
leaves remaining suites incomplete. No unrelated process is killed.

Local verification used the real portable case validator with simulated HTTP
and process operations. Seven checks passed: complete75+15 collection and exact
reference hashes; retained introduced failures; preexisting stop with no server;
duplicate/missing case rejection; frozen deployment mutation detection; and
refusal to overwrite an existing deployment. Source files stayed unchanged and
all simulated owned processes stopped. No VM job or actual model inference has
been run by this preparation.

Verification source/result:
`check-final-existing-models.py`, `final-existing-models-checks.json`.
