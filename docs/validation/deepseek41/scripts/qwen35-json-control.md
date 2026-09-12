# Alternating Qwen3.5 JSON latency control

This is a new60-case measurement plan, separate from completed r2. It targets the unresolved first-token/request latency observations; it does not remove r2 outliers or change a request. The preserved r2 c1 TTFT samples are baseline `[64.671,161.391,65.039]` ms and final `[72.033,79.107,64.273]` ms.

| Pair | Fresh process order | Timed plan per process |
|---|---|---|
| pair1 | baseline → final | Same15 tagged JSON requests: c1/c4 ×3 repeats |
| pair2 | final → baseline | Same15 tagged JSON requests: c1/c4 ×3 repeats |

Every process uses the exact reviewed r2 `run_version`, SHA256 `10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5`:512-token decode warmup, five JSON warmups, unchanged sampling, native/CPU settings, GPU7, telemetry and cleanup. Total60 measured cases, four decode warmups,20 JSON warmups. The existing frozen r2 baseline/final deployments are reused without copying or editing them. Their recorded files and all core binaries are rechecked before the plan and by the reused function before/after every job. Mutable logs go to the fresh pair/job output paths.

The runner pins completed r2 manifest `7ba46725c2a73a7131b859855533b09cf864e4f5b6d64ecb494eb6f1eeb16763`, checks all six child hashes and90-case coverage, and verifies the source/helper/harness and model identities. Known semantic failures in other models do not invalidate the fact that r2 completed; incomplete, altered or deployment-failed prior runs are refused.

Each pair has its own full comparison and all paired-case raw TTFT/decode/request-wall values. A ratio requires its complete c1 or c4 group to satisfy the original request/output/token-count/telemetry checks. Failures and incomparable groups are retained, later jobs still run, and no selected successful subset or pooled replacement for the original observations is produced.

Root may execute only after reserving an exclusive CPU/GPU/I/O window:

```sh
/workspace/deepseek41-work/venv/bin/python \
  /workspace/deepseek41-work/run-qwen35-json-control.py \
  --exclusive-window
```

Default new output: `/workspace/deepseek41-work/existing-regressions/final3651-native6b3-qwen35-json-alternating/`, with `pair1/` and `pair2/` child reports. Existing output is refused. `--plan-only` prints the source-pinned plan without reading VM artifacts or launching an endpoint. Local guards use real temporary frozen files and retained r2 response data with a simulated job executor; they are not inference/performance results.
