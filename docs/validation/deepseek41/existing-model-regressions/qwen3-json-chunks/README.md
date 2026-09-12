# Qwen3 prefill chunk sensitivity: exact request control

All 12 diagnostic requests completed with the required chunk traces. The same retained JSON request changes output with prefill chunk geometry in both the preserved HEAD baseline and the final build. Every matching build, chunk and repetition pair has identical recorded response fields and prompt/completion counts: all six comparisons match. Both repetitions also match within each build and shape.

| Observed prefill lengths | Baseline | Final | Recorded response |
| --- | --- | --- | --- |
| 90 | 2/2 semantic passes | 2/2 semantic passes | 21 tokens; flat object with name, moons and habitable |
| 76 + 14 | 0/2 semantic passes | 0/2 semantic passes | 25 tokens; nested Mars object |
| 64 + 26 | 2/2 semantic passes | 2/2 semantic passes | 21 tokens; flat object with name, moons and habitable |

The four failures are preserved. They are valid JSON that does not satisfy the prompt's requested keys. Both builds emit the same failed text:

```json
{"Mars":{"moons":2,"habitable":false}}
```

This is direct evidence of inherited chunk sensitivity for this request. It does not establish the originally unlogged concurrent chunk lengths, so it cannot prove the earlier timing request used 76 + 14. No logits or intermediate activation traces were collected; a particular floating-point kernel or an inherited chunking defect is not isolated. The diagnostic observed no response difference between builds at a matching shape. It does not erase the scenario's semantic failure or establish universal regression freedom.

The exact recorded `json-c4-r0-i0` payload was replayed solo without changing its tag. Its request SHA is `e1c5ae800ba0b4c69abce54cb2dd6301f7a8a6329b7a9892d83b8514eefaacfc`; its initial fixture SHA is `820cc3973263c3e195f42ed80a0e365680660fab31f290df81e48eb9922ce237`. All 12 responses report 90 prompt tokens, no tools or reasoning, and `finish_reason=stop`. All server completions report zero reused KV tokens.

Six fresh processes used the unchanged frozen R2 hosts, with two identical requests each and no extra warmup. The global batch budget of 256 tokens, four slots, disabled prefix cache, F16 KV, GPU 7, greedy sampling and disabled speculation remained fixed. Only the CLI and scheduler prefill/solo chunk limits changed. Existing `TS_CB_DEBUG=1` logs produced request-specific P@0/P@76/P@64 positions followed by D@90, independently audited here. The first request was cold and the second warm; all timings are unqualified. These are scheduler traces, not native per-kernel traces.

The [raw run](raw/run.json) pins the previously reviewed R2 runner, inherited frozen deployments and every child report. [audit.json](audit.json) contains the independent 12-case audit, six comparisons between builds, response identity hashes, binary identities and limits. [audit.py](audit.py) verifies file hashes, exact request equality, reported configuration, directly reparsed P/D ordering and lengths, usage, all response fields, semantic results and observed exit of each owned process. No job reported changed frozen files or cleanup/finalization errors. Root separately confirmed empty GPU compute clients after completion; this local audit made no additional VM check.

The baseline native SHA begins `e67e4c92`; the final native SHA begins `6b3b5ab3`. Complete managed/native hashes and the common Qwen3 checkpoint identity are in the audit. [Scripts and 23 local guard checks](scripts/qwen3-json-chunks-checks.json) preserve the exact runner, whose SHA begins `b5d39361`, and its test code. The guards simulate lifecycle and evidence failures, not inference. The existing shared launch/cleanup helper is also copied to make source dependencies explicit. The checker retains its original local paths as provenance.

This control follows the [retained same-request review](../../json-performance/completed-r2/README.md) and [Qwen3 grammar-mask check](../qwen3-json-mask-review/README.md). The earlier concurrent records differed in arrival order; those records remain unchanged.
