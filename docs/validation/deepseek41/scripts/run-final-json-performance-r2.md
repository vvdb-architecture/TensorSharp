# JSON performance r2: preserve logs outside the frozen deployment

The original attempt is [preserved as incomplete30/90](../json-performance/incomplete-r1/README.md), including two newly failed Qwen3 semantic cases. It is not silently resumed. The r2 runner repeats all90 requests with the same fixtures, settings, version order, warmups and timing/response eligibility policies described by the [original protocol snapshot](run-final-json-performance.md).

Only the default label and logging destination changed. Both HEAD04a5faa and final3651 support `TENSORSHARP_LOG_DIR`; `ServerOptionsBuilder.cs` is byte-identical at SHA256 `2e61fa5d560c3293bc7771fdd55db8b40bc5627ca8ab11809b71df9b6e1fdce1`. Each owned job records that environment override and writes file logs to `OUT/<model>-<version>-file-logs`. All original frozen-file copy/hash checks remain unchanged, including DLLs, configuration and inherited logs. There are no filename exclusions and no production code changes.

The staged runner is SHA256 `10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5`. The original5794 version remains archived locally and remotely as `run-final-json-performance-original-5794b097.py`. Root reviewed the change; a separate local review found no actionable issue. [27 local guards](final-json-performance-r2-checks.json) retain earlier coverage and add actual temporary-file logging across six jobs plus deliberate DLL/configuration/inherited-log mutation checks.

Root's command, only during its reserved exclusive window:

```sh
/workspace/deepseek41-work/venv/bin/python \
  /workspace/deepseek41-work/run-final-json-performance.py \
  --exclusive-window
```

New default output: `/workspace/deepseek41-work/existing-regressions/final3651-native6b3-json-performance-r2/`.
New frozen deployments: `/workspace/deepseek41-work/regression-final3651-native6b3-json-performance-r2/{baseline,final}/bin`.
Existing paths are refused. Staging does not count as executing or passing the model suite; the later r2 reports determine completion, failures and comparable groups.
