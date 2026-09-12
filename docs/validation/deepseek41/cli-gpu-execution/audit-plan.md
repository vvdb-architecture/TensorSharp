# CLI GPU-execution evidence audit

Local-only curation target: `docs/validation/deepseek41/cli-gpu-execution/`.
This is a new diagnostic record, separate from the completed `/workspace` model benchmarks. The user's actual deployment is `/root/TensorSharp/TensorSharp.Cli/bin`; its reported pre-change native SHA is `c68df1f3d0c70269be249c12a9eae9e7e6ae9144e232dda311d4e853c2d048d0`.

## Required retained evidence

- Exact CLI working directory, argv, deliberately set environment, input, output, exit status and token/timing summary. Preserve original and perf3 runs as distinct runs if flags or workload differ.
- The source revision and dirty diff, .NET SDK/runtime identity, configured backend/build flags, CMake CUDA target architecture, actual copied native/managed binary hashes and loaded library path when retained. A CUDA-enabled build does not by itself prove GPU execution.
- Startup logs showing initialized device names, actual layer or expert placement, CPU pool width, model load identity and companion/Engram settings. The auxiliary CPU worker pool is not the selected model backend.
- GPU telemetry with PID, device IDs, sample timestamps and the exact measurement interval. GPU memory residency alone is not utilization evidence; periodic nonzero utilization supports execution but is not a per-kernel attribution or exclusive performance proof.
- Complete `TS_DSV4_PERF=3` logs and before/after validation logs. Preserve all input, build, compute, logits and backend-transition evidence without silently dropping slow calls or failure messages.
- Source patch and test artifacts for the narrow startup diagnostics and V4.1 alternate-GPU rejection; preserve CPU oracle results and negative backend controls separately from full-checkpoint CLI tests.

## Bounded checks after artifacts arrive

1. Hash each raw artifact and bind each run to its own binary/source/configuration. Do not relabel historical native 6b3 results as tested against a later diagnostic build.
2. Confirm CLI completion, the actual answer and reported token counts. Compare paired outputs/settings explicitly; different prompt, thinking, cache warmth or perf instrumentation prevents an isolated throughput claim.
3. Parse every native forward and microbatch timing record. Reconcile positions and token counts, retain construction versus reused-graph observations, and report prefill/decode separately. The `inputs` interval includes waits, masks, Engram lookup and uploads; it is not an exclusive Engram timer. Synchronized `compute` includes graph scheduling/device work; it is not a pure GPU-kernel duration.
4. Parse perf3 scheduler backend transitions with their graph node indices. Consecutive transition spans establish assigned backend ranges, not measured per-node execution time. A transition's displayed op describes only its first node. Keep CPU spans and unknown backends if present; do not infer all node op types from transition headers.
5. Recompute telemetry sample counts/ranges per device and bind compute clients to the owned CLI PID. Distinguish load, prefill and decode intervals where timestamps allow; otherwise state the limitation. Do not infer low GPU utilization means CPU inference or that all eight layer-split devices execute concurrently.
6. Verify after-change diagnostics identify actual compute devices, auxiliary pool and routed-expert offload clearly. Confirm V4.1 alternate-backend rejection and explicit CPU oracle preservation from independent tests. Describe the behavior actually changed; do not credit clearer logging as a GPU arithmetic or performance fix.
7. Curate original logs/reports byte for byte, a compact analysis/README and source/artifact manifest. Check new links and hashes locally. Main validation and prior benchmark labels remain unchanged.

No new benchmark framework, remote job, model load, configuration change or performance claim is authorized by this preparation. Root owns VM execution and supplies the completed diagnostic artifacts.
