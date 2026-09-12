# Backend diagnostics contract

The remote CPU lane initially passed 3,380 of 3,381 tests. `HealthyBackendNeverReportsAFailure` found stale CUDA initialization text despite `HasBackendFailure=false` when CUDA devices were hidden. Real CPU matrix operations passed.

`GgmlNative.BackendFailureText` now returns empty unless a compute failure is latched. The true-failure branch still returns native details unchanged. The two real GGML failure-reporting tests pass locally. `Program.cs` and `stub.c` separately verify both latch states through the actual managed P/Invoke ABI in an isolated process; the stub simulates diagnostic state and does not claim GPU fault recovery.

After the guard, the same remote lane passed all 3,381 tests. The subsequent [final managed lane](../managed-correctness/README.md), including additional thinking/JSON, audio-refusal and GPU-selection tests, passed 3,400/3,400 tests locally and remotely.
