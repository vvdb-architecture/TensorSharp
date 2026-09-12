# First full-checkpoint TP baseline

This is the completed **first** full-Q2 TP8 profile,
`tp8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-chunk1024-b26c-tools`.
It uses routed-MoE tensor sharding with host-staged F32 communication; attention
and shared experts retain layer placement. It is not evidence for the subsequent
native recovery patch, serialization patch, CPU-MoE4 profile, or llama.cpp parity.

All seven child reports explicitly completed with their planned case counts:

| Suite | Passed / total |
|---|---:|
| Multilingual and Unicode JSON | 10 / 10 |
| Tool policies | 29 / 30 |
| Thinking workflows | 3 / 4 |
| Blocking protocol | 4 / 4 |
| Short/JSON/schema/multi-turn/tool quality | 30 / 30 |
| Sustained 512-token decode | 15 / 15 |
| Long 8k/32k retrieval | 2 / 2 |

The two failures remain in the report. Named-tool thinking closed reasoning at
1,536 tokens, then exhausted its total 2,048-token budget inside a malformed,
unfinished string argument. The thinking-agent workflow stopped on repetitive
tool markup. The later [serialization replay](../tool-serialization-replay/README.md)
does not convert either result into a pass.

The launch records 8 visible/selected GPUs, `TS_DSV41_TP=8`, CPU-MoE0, context
65,536, native ubatch1,024, F16 cache, sparse FA1, compact gather1 and warmed
Engram pages. Scheduler prefill and solo chunks are both 1,024, maximum batched
tokens 4,096, and maximum running sequences 4. The native SHA is
`b26cac3e40ff67b6de077063cd7a3c68e683220f0bc60237edf728ec6d218f1f`;
actual host Runtime SHA is
`b80a2475c484c314db5e9f86673b806961893d14c3e4d3456b0ba63f83a75d44`.
The launch's `--cpu-moe-threads 48` records intent; this older native did not yet
honor that CLI override in the DeepSeek-specific loader. No routed experts were
CPU-offloaded in this profile.

| Observed metric | First TP8 baseline | Earlier layer control |
|---|---:|---:|
| Decode c1 median, 512 tokens | 15.417 tok/s | 34.775 tok/s |
| Decode c4 per-request median, 512 tokens | 3.709 tok/s | 8.432 tok/s |
| Decode c4 whole-wave median throughput | 14.704 tok/s | 33.03 tok/s |
| 8k retrieval TTFT, matched r0 request | 33.988 s | 19.956 s |
| 32k retrieval TTFT, matched r0 request | 137.273 s | 80.027 s |

These are **descriptive comparisons**, not an isolated TP experiment. The earlier
4608 layer host had different native/managed hashes, compact-gather configuration
and scheduler defaults. Its steady/long files predate explicit completeness
metadata, so the strict analyzer keeps them ineligible while displaying their
15 matched decode requests and two matched r0 retrieval requests. The long rows
use only matched r0 requests, not the median of all three earlier repetitions.
Actual long prompt lengths are 7,706 and 30,585 tokens. TP has only one repetition
per long length. Decode timings are stream-window estimates; whole-wave throughput
uses complete wave wall time, including prefill.

The later b26 layer host provides complete same-request quality controls: 30/30
quality, 4/4 blocking, 3/4 thinking, and 5/10 multilingual. All 48 initial request
hashes match the TP profile. Its Runtime is the older 3,433-test stage, with the
known Unicode-mask defect, and its scheduler chunks are 256/8,192 instead of
1,024/1,024. The five Unicode fixes therefore cannot be attributed to TP. Source
hash differences and all failed outputs are retained. No comparison is marked
as isolated placement performance.

The independent native accounting audit recomputed all three placement phase
log-range SHA-256 values and every recorded forward counter. No errors or
preemptions appeared in those ranges. Prompt work reconciles exactly:

- Quality: 10,366 logged prompt tokens minus 36 reused tokens equals 10,330 native
  prefill tokens. There are 50 measured turns plus one warmup.
- Steady: 660 measured prompt tokens plus 23 warmup tokens equals 683 native
  prefill tokens. All 15 measured completions contain 512 tokens.
- Long: 38,291 measured prompt tokens plus 23 warmup tokens equals 38,314 native
  prefill tokens.

Native decode calls also equal logged generated tokens plus forwarded EOS tokens
for every phase. All warmups passed. The sampled GPU clocks were 1,740/7,251MHz;
these periodic samples do not prove clocks at every instant.

Artifacts:

- [Explicit profile manifest](manifest.json)
- [Analyzer report with failures, source hashes and matching controls](report.json)
- [Native counter and completion audit](native-accounting-audit.json)
- [Runner orchestration checks](runner-checks.json)

The inspected old `run-final-suite.py` recorded child exit codes but itself
returned zero even after failures and had no phase completeness flag. The three
placement child reports were independently checked here; no missed failure was
found. The updated runner records planned labels, source hash, initial incomplete
state, and per-child JSON/count/completion validation. It still attempts later
labels after a failure, then returns nonzero unless all passed. Four local mocked
orchestration checks cover success, continued execution after failure, zero exit
with missing JSON, and exception cleanup. Native accounting is unchanged.

The [analyzer](../scripts/analyze-placements.py),
[native audit](../scripts/audit-first-tp.py), and
[used manifest](../scripts/placement-first-tp-manifest.json) are preserved with
[exact source hashes and version scope](../scripts/README.md).
These VM-specific snapshots retain recorded paths; restore the raw artifacts or
adapt a separately hashed copy before reproducing:

```sh
python3 docs/validation/deepseek41/scripts/analyze-placements.py \
  docs/validation/deepseek41/scripts/placement-first-tp-manifest.json \
  /tmp/deepseek41-reference/placement-first-tp-report.json
python3 docs/validation/deepseek41/scripts/audit-first-tp.py
```
