The four fresh Qwen3.5 jobs cross the pinned R2 managed and native deployments in this order:

1. baseline managed / baseline native
2. baseline managed / final native
3. final managed / baseline native
4. final managed / final native

Each job calls the unchanged, SHA-pinned R2 `run_version`: one 512-token decode warmup, five JSON warmups (concurrency 1 then 4), then 15 JSON cases (concurrency 1 and 4, three repeats each). Request tags, sampler settings, model, GPU7, port5011, context8192, slots4 and chunk256 remain those of R2. All four launches add `TS_GGML_PHASE_TIMING=1`. These are diagnostic timings and are never qualified performance evidence.

After the parent releases the VM window, place `run-qwen35-cross-phase.py` next to the existing pinned `run-final-json-performance.py` and `run-final-existing-models.py`, then run:

```sh
/workspace/deepseek41-work/venv/bin/python \
  /workspace/deepseek41-work/run-qwen35-cross-phase.py
```

Defaults create new, non-overlapping folders `/workspace/deepseek41-work/regression-qwen35-managed-native-cross-phase-r1` and `/workspace/deepseek41-work/existing-regressions/qwen35-managed-native-cross-phase-r1`. Existing output or host directories cause refusal. No R2 source file is changed; every original and copied file is hashed, including inherited logs. Mutable runtime logs are redirected with the existing `TENSORSHARP_LOG_DIR` setting outside each private host. A stop file at `/workspace/deepseek41-work/stop-qwen35-cross-phase` closes the resource window between bounded operations; only owned server processes are stopped.

Every child retains responses, failures, timings, telemetry, binary identities, launch command and native phase lines. The model identity preserves the pinned R2 path/size/nanosecond mtime and content SHA, explicitly recording whether the existing content-hash cache was reused. A crossed host's native ABI compatibility must be demonstrated by execution, not inferred from copy hashes. Missing phase lines, missing requests, source changes or failed warmup coverage prevent a completed diagnostic result. Semantic failures remain failures and do not suppress subsequent jobs.

`check-qwen35-cross-phase.py` passed 40 local guards using real pinned R2 request builders with simulated HTTP/processes/telemetry. `qwen35-cross-phase-checks.json` records exact runner and guard hashes. The independent source review accepted runner SHA `32219050b755a6b28a08de3c0255d929d53813e5378b022b00d435c4484104b2`. No inference or performance claim follows from these local checks.

Native phase lines include warmups and do not carry request IDs. Solo phase attribution requires checking surrounding request logs and exclusive serial coverage. Concurrent phases remain aggregate unless further evidence supports a particular association.
