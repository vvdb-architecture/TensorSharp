# Qwen3 repeated JSON request: bounded state review

No demonstrated state bug. Admission/chunk geometry sensitivity is consistent with the evidence but is not a causal attribution.

- Same final binaries, request payload and90 prompt tokens across the three executions.
- Quality returns21-token flat object; r1/r2 return the same25-token nested object. Nested output is syntactically valid JSON and violates the prompt semantic structure.
- Observed request arrival order changes: quality i0,i1,i3,i2; both timing runs i1,i2,i3,i0.
- Prefix caching explicitly disabled; all target completions report kvReused=0; retained fused holders unavailable.
- Qwen3 logs decline token-batched fused decode and fall back to serial per-sequence fused forwarding.
- Current adapter constructs a fresh constraint per request. Shared grammar-state/mask caches are synchronized; ApplyMask does not mutate the shared mask.
- Scheduler total256-token budget cannot admit all4x90 prompt tokens in one forward step; chunk sizes depend on waiting/running contenders.

- No per-step sequence IDs, chunk sizes, cached/logit tensor identities or per-token logits were retained; no proof of a cache lifecycle bug or exact numerical cause.
- Different request order, previous requests, and resulting prefill/kernel geometry are confounded. The quality run is not a controlled equal-history replay of the timing run.
- Source review identifies mechanisms, not proof of the recorded runtime branch beyond logged execution plans/fallback.
- Do not describe this as proven nondeterministic batched decode GEMM: Qwen3 runs serial fused decode in these logs.
- The earlier zero-introduced-failures quality conclusion remains limited to that particular execution. These repeated timing reports add a real semantic failure on the same request.
- No VM access, new model runs, source edits, tolerances or validator changes were made for this review.

Source locations: OpenAIChatAdapter.cs365–374; GrammarLibrary.cs40–91; GrammarConstraint.cs190–225 and531 onward; ContinuousBatchScheduler.cs325–338,443–445,512–540,586–606; BatchExecutor.cs996–1034,1075–1190; Qwen3Model.PerSeqCache.cs106–180; Qwen3Model.cs814–852.

Full source/artifact hashes, exact outputs, request identity and structured RequestId correlations are in [request-context-review.json](request-context-review.json).
