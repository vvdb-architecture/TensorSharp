# Historical CPU-MoE4 baseline

This full-Q2 run used the `3b88` native library before the later explicit shared
projection placement fix. It is a historical baseline, not the final CPU-MoE
result. The original quality suite passed **29/30**, steady decode passed
**15/15**, and long-context recall passed **2/2**. Its placement plan completed
with `all_passed=false`.

A separately recorded client-policy follow-up passed **10/10** weather and
agentic cases at concurrency 1/4 with `parallel_tool_calls: false`. It does not
replace the original quality failure or establish a numerical inference fix.

| Measurement | Observed result |
|---|---:|
| c1 decode median, three 512-token completions | 23.982 tokens/s |
| c1 TTFT median | 421.455 ms |
| c4 per-request decode median, twelve 512-token completions | 6.734 tokens/s |
| c4 whole-wave throughput | 26.403 tokens/s |
| 7,706-token prompt TTFT / request wall | 28.533 / 30.009 s |
| 30,585-token prompt TTFT / request wall | 110.334 / 111.742 s |

Decode uses the measured SSE stream window. Whole-wave throughput includes
prefill and is 2,048 tokens divided by median wave wall time. Each long length
has one sample. No comparison here isolates CPU offload from native or managed
build changes.

The failed original case, `agentic-c4-r0-i2`, returned both
`read_invoice(invoice_id="INV-472")` and a premature
`calculate_total(unit_price=0, quantity=0)` in one response. The fixture requires
reading the invoice result first, then passing its actual values to the second
function. The response therefore failed the unchanged exact call-count/order
and argument checks. Its complete request, tool structure and output remain
in [report.json](report.json).

The separate serial follow-up changes only the initial requests' explicit
`parallel_tool_calls: false` field and applies that constraint to every
tool-bearing workflow turn. All ten policy comparisons and affected input hash
changes were verified. The calls still had to supply `INV-472`, then the returned
`unit_price=13.75` and `quantity=5`, and finish with the exact expected JSON.
The report retains the original failure beside the successful constrained case.

Both phases used eight A40 GPUs, layer placement with the first four routed MoE
layers on CPU, explicit 48 native CPU threads, context 65,536, native ubatch
1,024, scheduler chunks 1,024/1,024, four running slots, F16 KV, and sparse
attention plus compact raw gather. Exact launch settings and binary hashes are
in the report. The native SHA is
`3b885898475da3f6b6a629c0e9486f97d7351f6bcb67daa684dfef0e6ca74d2c`.

Later [scheduler-observer evidence](../shared-expert-placement/README.md) on a
five-layer CUDA fixture showed that the old CPU-MoE/TP path also placed affected
shared-expert gate/up projections on CPU. These shared projections were not all
on GPU in the old implementation. The number of misplaced projections across
the full 40-layer historical host was inferred from source and fixture behavior;
it was not directly observed on that host. The newer pinned candidate and its
validation are separate evidence, so this table must not be labeled its result.

The audit recomputed all four server log-range SHA values and all native work
counters. Child reports are complete with exact planned case counts, and their
exit codes correctly preserve the failed quality case while later phases ran.
No error or preemption lines occur in the audited ranges. Including warmups:

| Phase | Native prefill tokens | Native decode calls | Generated tokens + EOS |
|---|---:|---:|---:|
| Original quality | 9,555 | 1,285 | 1,236 + 49 |
| Steady decode | 683 | 7,682 | 7,681 + 1 |
| Long recall | 38,314 | 72 | 69 + 3 |
| Serial tools | 8,280 | 1,078 | 1,052 + 26 |

Original quality performed fewer workflow turns than an all-passing run because
its failed case stopped after the premature combined call. Its wall time cannot
be treated as equivalent work to a completed 30/30 quality run.

Artifacts and reproduction:

- [Audit, failed case, source hashes and serial-policy comparisons](report.json)
- [Full serial request/response report](serial-tools.json)
- [Serial phase plan, command, completion and native counters](serial-tools-runs.json)
- [Audit source](../scripts/audit-cpu4-placement.py) and [snapshot hashes](../scripts/sources.json)

```sh
python3 docs/validation/deepseek41/scripts/audit-cpu4-placement.py \
  --output /tmp/cpu4-historical-baseline-report.json
```

The audit reads the recorded files under `/tmp/deepseek41-reference/final-raw`.
Restore that evidence or adapt a separately hashed copy to reproduce elsewhere.
The serial reports are byte-for-byte copies of the saved raw files. Performance
qualification for the original placement follows its recorded measurement
artifacts. The serial follow-up is reported as a correctness result; no separate
policy speedup is claimed here.
