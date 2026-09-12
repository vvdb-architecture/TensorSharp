# Final placement audit helper

`audit-final-placement.py` is a local, read-only auditor for the current
`run-profile.py` / `run-final-suite.py` placement and serial-tools plans. It never
connects to the VM, starts an endpoint, mutates raw evidence, or runs inference.
Manifests separate `expected_outer_phases` from `audited_phases`. The former
checks the complete ordered outer run and every phase envelope status; the latter
selects placement/serial-tools for independent numerical-work and case audits.
Other phases are listed in `not_numerically_audited`: their child cases and
performance are never marked verified just because a wrapper exited zero.

Prepared manifests:

- `final-cpu4-audit-template.json`: exact current final CPU4 profile name.
- `final-tp8-audit-template.json`: fill in the exact future TP8 profile name.
- `final-layer8-audit-template.json`: fill in the exact future layer8 profile name.

All three templates pin native6b3, the currently recorded3635 Runtime/Chat/Server
hashes, suite9139670b, harnessb791d4f6, eight GPUs, context65536, ubatch1024,
F16 KV, sparse/compact1, scheduler1024/1024 and four slots. The TP and CPU-MoE
settings differ explicitly by template. CLI `--tp 8` selects eight devices;
`TS_DSV41_TP` is the actual routed-MoE tensor-parallel switch. CPU4 expects explicit48 threads; TP8 and layer8 expect32. These expectations
must be reviewed if a future launch intentionally uses another value. Review all intended fields; do not blindly replace them
from the launch being audited merely to make the audit pass.

After the complete raw profile artifacts are pulled, copy the appropriate
manifest and set `qualified_labels` only for windows independently reviewed as
qualified (normally quality, steady, long, plus serial-tools only if that
follow-up was itself qualified). Empty means no qualified timing is declared.
Binary/source changes in later builds must be recorded in a separate reviewed
manifest. The TP8 outer plan is placement, tools, protocol, media, multilingual,
serial-tools. The layer8 template additionally appends long-parallel; review its
exact planned order before use. No final6b3 inference result is asserted by the
templates.

```sh
cp /tmp/deepseek41-reference/final-cpu4-audit-template.json \
   /tmp/deepseek41-reference/final-cpu4-audit-manifest.json
# Review the manifest against the intended launch and qualification evidence.
python3 /tmp/deepseek41-reference/audit-final-placement.py \
  /tmp/deepseek41-reference/final-cpu4-audit-manifest.json \
  /tmp/deepseek41-reference/final-cpu4-audit-report.json
```

The helper checks the expected CLI/environment and binary/harness hashes;
outer/phase/child completeness, ordered coverage and exit-code consistency;
exact unique case tags and waves; policy settings and actual requests; native
log-range hashes, counters, prompt-minus-reuse and generated-plus-EOS accounting;
chat-completion counts including warmup; log-error/preemption counts; actual
512-token decode work; and GPU telemetry coverage. It retains all failed cases,
per-turn request/output hashes and metrics, and sampled telemetry summaries.
It does not infer resource exclusivity between samples or claim llama.cpp parity.

Exit codes:

- `0`: evidence integrity/coverage passed. **Recorded model failures may remain.**
- `1`: missing/inconsistent evidence, log errors/preemption, or invalid workload.
- `2`: `--require-success` was supplied and an otherwise valid run has failed cases.

`integrity_valid` and `all_audited_scenarios_passed` are deliberately separate fields.
Quality28/30 or29/30 remains failed even when perfectly recorded. The exact
failed case remains in `failed_cases`; failed cases are excluded from successful
metric summaries without disappearing from counts or source evidence.

The outer script's `runner_sha256` identifies the suite, not its own executing
source. This limitation is recorded; the auditor does not reinterpret the field
as an observed outer-source hash. `run-profile.py` continues phases after nonzero
child exits. Its aggregate status alone does not validate child JSON; this helper
independently checks those reports.

Fourteen local checks passed using historical CPU4 evidence plus deliberately
altered temporary copies: preserved29/30 failure, incomplete outer plan, missing
child, duplicate case, wrong count/counter/native hash/thread setting,511-token
decode, explicit log error/preemption serial-policy coverage, and additional outer phases with correctly or incorrectly
propagated status. The last case
uses an explicitly synthetic outer envelope around the real separate follow-up;
it is a checker test, not a historical claim about that outer runner.

Evidence: `final-placement-audit-checks.json`, test source
`check-final-placement-audit.py`, and valid known-failure example
`final-audit-historical-{manifest,report}.json`.
