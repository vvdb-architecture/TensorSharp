# Preserved controls for the final layer comparison

This is a local control inventory, before pulling/auditing final layer outputs.
It does not assert a final speedup, regression result, or llama.cpp parity.
The companion `final-layer-controls.json` retains full source hashes, settings,
all group counts, request-hash matches, and failed-case messages.

The target is
`layer8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final`:
native `6b3b5ab3…`, Runtime `957c14b1…`, Chat `955cceaf…`, Server `216d861c…`.
It selects eight GPUs, uses layer placement with actual TP switch0, CPU-MoE0,
context65536, F16 KV, native ubatch1024, sparse/compact1, warmed Engram, four
scheduler slots, prefill/solo chunks1024/1024, max batch4096 and explicit32 CPU
threads. CLI `--tp 8` selects devices; it does not enable routed-MoE TP by itself.

| Final group | Closest preserved CPU0 layer control | Matching initial hashes and coverage | Defensible use after final audit |
| --- | --- | --- | --- |
| Standard quality30 | `layer8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-b26c` |30/30 hashes, complete30/30 passed | All30 case outcomes can be compared. Timing requires identical full per-turn requests, outputs and token work; an initial hash alone does not establish that. |
| Decode15, each512 tokens | `layer8-context65536-ubatch1024-cpumoe0-sparse1-4608` steady report |15/15 hashes, all15 passed; complete decode scenario within old30 short+decode cases | Use every c1 repeat (3) and c4 request (12), with every one of the six decode waves. Never compare the old30-case wrapper wall against the new15-case wrapper wall or choose favorable repeats. |
| Long2, c1, one repeat | No exact earlier CPU0 layer group |4608 and ubatch256 each have6 cases; only the two r0 hashes match | Retain all old3 repeats per context as historical observations. Do not select r0 and present an exact whole-group timing ratio. Final TP8 and CPU4 have exact2-case controls, but those are placement comparisons. |
| Serial-tools10 | No earlier CPU0 layer group | Final TP8 and CPU4 each have10/10 hashes and10/10 passed | Compare explicitly as a serial-client-policy placement workload. There is no historical CPU0 serial-workflow regression control. |
| Long-parallel8 | `layer8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-b26c` |8/8 hashes; complete8 cases,7 passed | Retain the entire8k wave, including its formatting failure. The32k wave is a complete predefined4/4-passing group; a ratio needs all four final requests/outputs/token counts to match. Do not select only successful8k requests. |

The b26c control uses native
`b26cac3e40ff67b6de077063cd7a3c68e683220f0bc60237edf728ec6d218f1f`,
Runtime `12ce3bc3…` and Chat `4e19639e…` from the earlier3433 stage. It already
has compact raw gather and the larger metadata scheduler pool, but explicitly
uses prefill/solo chunks256/8192 instead of1024/1024. Its CLI says48 CPU-MoE
threads; before the native getter fix this did not establish native48. The
native default32 is source-inferred for that host, not directly observed there.
Its quality wrapper took96.439s. Its parallel waves took114.912s (8k,3/4 passed)
and358.186s (32k,4/4 passed). Both source ranges record no preemptions. Parallel
native prefill153187 tokens equals the requests plus warmup; no recomputed
prompt work is needed to explain that count. These remain descriptive controls,
not isolated native-kernel comparisons against the final build.

The4608 control uses native
`4608dd0d59cb049befa9d689917337322740e140db38d7cb0e2c1d7c9080e573`,
Runtime `bdca2ce3…` and Chat `19ec3d23…`. Compact gather was not enabled, and its
launch does not record the later explicit scheduler and native-thread settings.
Its complete decode groups measured c1 median34.7752 tokens/s, c4 per-request
median8.43205 tokens/s and c4 whole-wave median33.0341 tokens/s, with all15
responses containing512 tokens. Missing legacy `run_complete`/plan fields must
remain identified as missing: full scenario coverage is reconstructed from
retained cases and waves, not retroactively written into old evidence.

Do not use the4608 32k parallel wall time as a clean kernel control. Retained
accounting shows10 preemptions and183057 native prefill tokens for122340 actual
request tokens. The newer b26c parallel control avoids that recomputation.
The b26c CPU0 full server log is not in the current local raw directory; its
recorded counters/range hashes are preserved, but this inventory did not rehash
the absent range bytes. Reconfirm that evidence before treating its no-preemption
record as independently revalidated.
The even older ubatch256 profile is available as a historical throughput point,
but changes both native microbatch size and several later implementations.
The crashed37c103 profile and earlier unqualified vision diagnostics are not
performance controls for this final comparison.

The closest **placement** controls are:

- `tp8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final`:
  same final3651 Runtime/Chat/Server, native6b3 and scheduler/thread settings;
  actual TP switch8 is the intended placement difference. It has matching
  quality30, decode15, long2 and serial10 groups, with outcomes29/30,15/15,2/2
  and10/10. Preserve the quality failure.
- `layer8-context65536-ubatch1024-cpumoe4-cputhreads48-sparse1-compact1-chunk1024-6b3-final`:
  same native6b3 and Runtime957c, but Chat693df8af from3635, CPU-MoE4 and48 CPU
  threads. Its matching groups passed28/30,15/15,2/2 and10/10. This comparison
  includes those explicit placement/thread and managed-build differences.

After the final raw pull, run the manifest-driven layer audit first. Then require
complete coverage and preserve every failed case; verify rendered request work,
generation/finish counts, full outputs, error/preemption counters and qualified
telemetry before reporting any timing ratio. No cross-profile aggregate should
mix different case plans, and no successful subset should erase a failed wave.
