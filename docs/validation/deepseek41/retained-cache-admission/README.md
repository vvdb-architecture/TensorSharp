# Retained-cache test admission race

The intermittent `SequentialRequestIdReuse_DiscardsOldRetainedMetadataAndHolder`
failure came from an unproven assumption that two immediate submissions are
admitted together. It did not demonstrate a stale-token/new-holder mismatch.

An independent local probe used the unchanged Runtime and the original test's
private `FusedStubModel` and scheduler configuration. It reproduced the original
failure twice in 40 attempts. In both traces the executor correctly discarded
the old same-ID retained holder once; the follow-up reused 16 tokens from
legitimate **pooled prefix-cache blocks**. The first request had briefly run
alone before its partner entered the fused path.

| Diagnostic control | Trials | Follow-up reuse | Old same-ID holder discarded |
|---|---:|---|---|
| Original submission order | 40 | 0 in 38; 16 in 2 | Once in all 40 |
| Delay old partner by 1 ms | 40 | 16 in all 40 | Never; no old fused holder was made |
| Delay replacement partner by 1 ms | 40 | 28 in all 40 | Never; the genuinely old holder was not overwritten |
| Disable pooled reuse for diagnosis | 40 | 0 in all 40 | Once in all 40 |
| Gate both pairs, no delay | 40 | 0 in all 40 | Once in all 40 |
| Gate both pairs, delay old partner | 40 | 0 in all 40 | Once in all 40 |
| Gate both pairs, delay replacement partner | 40 | 0 in all 40 | Once in all 40 |

The [test correction](../../../../InferenceWeb.Tests/RetainedFusedCacheTests.cs)
uses the existing `ComputeGate`: close it, submit the first request, wait for
`StepsHeldByGate` to prove the worker parked before scheduling, submit the
partner, then reopen. Both rounds now exercise the intended fused-holder
replacement. Merely closing and reopening around two submissions would still
race a worker that had drained only the first command.

The original zero-reuse and exactly-one-discard assertions remain unchanged.
The correction does not disable pooled caching or modify production Runtime
code. The full 28-test cache class and the 3,635-test managed lane passed locally
and on the requested VM afterward; exact commands, counters and hashes are in
[local verification](local-correctness.json) and
[VM verification](../managed-correctness/remote-image-url-correctness.json).

[Diagnosis and representative traces](diagnosis.json) retain the ungated
failures and binary/source provenance. [All 120 gated trial results](gated-trials.json)
preserve both original assertions per trial. The complete raw logs remain at
the paths and SHA-256 hashes in the diagnosis manifest. Exact probe sources
are preserved as `probe-before.cs.txt`, `probe-gated.cs.txt`, and
`Probe.csproj.txt`. To reproduce, copy the project and one source into a scratch
directory as `Probe.csproj` and `Program.cs`, update the absolute test-assembly
and report paths for that workspace, and run:

```bash
dotnet run --project Probe.csproj -c Release
```

The probe references already-built test/runtime assemblies and
does not rebuild or change the repository.
