# Native backend diagnostics

This local check validates the V4.1 backend restriction and startup diagnostics. It does not establish the cause of the user's VM inference performance report.

The patch logs the actual selected backend and device before metadata loading, labels the CPU pool as auxiliary, reports the resolved routed-expert CPU offload layer count (including zero), and labels the explicit native CPU fixture correctly. V4.1 rejects any selected non-CUDA-family GPU after reading the architecture; legacy V4 alternate-GPU handling remains unchanged. No graph, mathematical, or placement logic changed.

The rebuilt Metal-enabled library rejected both a requested CUDA backend that fell back to Metal and explicit Metal. An archived local Metal-enabled library accepted the former before this rebuild. Explicit CPU load remains accepted, and the established stable CPU fixture passed all 41 strict checks at atol=rtol=2e-5. The CUDA-index fixture had 32/41 strict CPU checks both before and after, with all reported metrics exactly identical and all greedy tokens matching; these failures are retained. The separate older F32 fixture returned 35/41 and is likewise retained without a passing claim.

Only `ggml_ops_deepseek4.cpp` changed in the repository. Raw logs, reports, harness, source diff, and hashes remain in this directory. The archived local libraries are controls for these local tests, not substitutes for the actual VM binary provenance.

For the VM investigation, launch-time `TS_DSV4_PERF=3` reports allocated scheduler backend transitions and each microbatch's input versus compute duration. Input preparation includes host Engram lookup and uploads; GPU weight residency alone does not prove execution assignment. `TS_DSV4_NODE_DUMP` also reports actual backend ownership but adds substantial readback/synchronization overhead.
