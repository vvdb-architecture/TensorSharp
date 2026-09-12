# TP failure recovery and CPU thread override

Native revision `3b885898475da3f6b6a629c0e9486f97d7351f6bcb67daa684dfef0e6ca74d2c` fixes two TP recovery defects and delivers the existing CLI thread override to the DeepSeek fallback/host-expert CPU pool. The previous `b26cac3e…` binary remains archived for the placement baselines.

The shared TP error string previously survived every request and slot reset. After one recoverable rank exception, even a successful request in another slot failed. Each fully validated text or image forward now clears the shared error once. Later layers retain the first failure, and the affected slot remains blocked until its cache is reset. Rank graph entries are published only after successful construction and allocation, so a failed construction cannot leave a partial graph available for reuse.

The fallback/host-expert CPU pool count now resolves as **positive native environment override → explicit CLI override → existing DeepSeek automatic count**. The added internal getter reads the existing native CLI override; the generic MoE resolver keeps its existing behavior. The count is selected at model load. Tests observe the width passed to a successfully allocated worker pool through a test-only accessor. Explicit CPU-only inference also has a separate logical device-zero CPU backend, which retains its original `n_threads` argument; these tests make no claim that the CLI override changes that backend.

| Verification | Result |
|---|---:|
| Local standalone CPU TP failure recovery | 18/18 |
| Local full-vs-sharded CPU MoE numerical comparisons | 96/96 |
| Local text/image slot failure and oracle checks | 158/158 |
| Local and VM tiny-loader thread-count checks | 18/18 each |
| VM standalone CUDA2 TP failure recovery | 18/18 |
| VM CUDA2 text/image TP slot failure and oracle checks | 230/230 |
| VM native CTests | 13/13 |
| Archived b26 CUDA2 TP oracle with explicit CPU threads=1 | 41/41 |

The standalone recovery test injects a rank failure after branch submission and a graph-construction failure before the graph exists. It checks that a later healthy layer preserves the error, then retries the same graph shape in a new forward against an independent full-weight reference. Separate temporary controls restore each former behavior while retaining the test hooks: the stale-error control rejects the new request; the premature-cache-publication control crashes on the retry. These controls are isolated source variants, not executions of an unmodified b26 binary.

The full-model fixture tests inject failures in the first and second microbatches. They verify both forward APIs reject reuse of the damaged slot, an untouched slot continues from its existing prefix without reset, and resetting the failed slot reproduces fresh oracle logits. Image cases retain complete image spans and their Engram history barriers. Ordinary invalid-input rejection remains retryable. Fault hooks and the thread-count observation API exist only in test-enabled builds.

Thread-count cases cover CPU-MoE layer counts zero and one, CLI-only settings, explicit environment precedence, invalid/nonpositive environment values, and resetting the CLI override to zero or a negative value. The automatic offload count is compared with an initial load on the same machine; the normal no-offload default is checked against the supplied loader argument.

Commands and full artifact/source hashes are retained in [manifest.json](manifest.json) and the accompanying logs/reports. VM reports are copied alongside the local evidence. These checks establish synthetic-fixture correctness and failure recovery, not full-checkpoint answer quality or llama.cpp performance parity.
