# Final existing-model quality rerun

The final managed 3651 build and native `6b3b5ab3…` introduced **no new failures
in this 75-case run** relative to either the HEAD managed baseline or
the earlier after build. This result is specific to the recorded run and
workload order: only **39/75**
current cases passed their strict contracts. A separate Chinese/emoji JSON
suite passed **15/15**. These quality timings are unqualified. Later
[JSON runs](../../json-performance/completed-r2/README.md) exposed additional
failures for identical requests, so zero introduced failures is not a
repeatability or broader regression guarantee.

| Model | HEAD baseline | Earlier after | Final 3651 + 6b3 | New failures vs either reference | Separate Unicode JSON |
|---|---:|---:|---:|---:|---:|
| Qwen3-0.6B Q8_0 | 11/25 | 12/25 | 12/25 | 0 | 5/5 |
| Qwen3.5-0.8B Q8_0 | 18/25 | 18/25 | 18/25 | 0 | 5/5 |
| Gemma-4-E2B-it Q4_K_M | 4/25 | 4/25 | 9/25 | 0 | 5/5 |
| Total | 33/75 | 34/75 | 39/75 | 0 | 15/15 |

Each model ran short, 512-token decode, JSON, multi-turn and weather-tool
round-trip cases at concurrency 1 and 4, with one repeat. Every one of the
75 initial request hashes matches both historical reports. The additional
Unicode suite uses five distinct request tags per model and requires exact
JSON values `北京`, `你好，世界` and `🚀`; it is not included in the historical
75-case comparison. Output identity excludes generated tool-call IDs and
includes assistant content, reasoning content, finish reason and tool
functions. It matches HEAD in 63/75 cases and the earlier after build in
65/75 cases; equality of all generated text is not claimed.

All 36 current failures remain failures. Examples include extra text around
`42`, `OK.` instead of exactly `OK`, a nested `Mars` object instead of the
requested three keys, fenced tool-result JSON, and decode answers that omit
the required collision discussion. A mathematical answer or valid JSON
syntax alone does not satisfy these fixtures. The overall runner preserved
`run_complete=true`, `all_cases_passed=false` and its failure exit status.
Each suite also has one separate short warmup. Four of the six warmups
failed their strict format check, including Qwen3 and Gemma's Unicode-suite
warmups; this explains those suites' failure status despite all their
measured Unicode cases passing. Warmups are not part of the 90 measured
cases.

The [offline audit](audit.json) verified the SHA256 and byte size of all 12
current/historical raw suite reports, recomputed the paired case results,
and replayed all 90 current cases plus six warmups through the exact frozen
content-only validator. All 116 recorded turn requests, including generated
tool/history messages, matched the replayed requests. Replay reproduced
every status, failure detail, validated answer and initial input hash. It
performs no inference, network access, response repair or reclassification.

[Quality reports](quality/) and [Unicode reports](unicode/) retain every
request, full generated response, original timing, failure and warmup.
Only process/GPU snapshots are omitted; each curated report records the
original source path, SHA256 and byte size. The audit retains each historical
case's status and generated-output hash. Complete historical raw reports
remain on the VM under `existing-regressions/quality-baseline` and
`existing-regressions/quality`, and were independently read during this
audit. Their earlier binary provenance is also retained in the
[original summary](../final-before-after-quality-summary.json).

All six current reports record identical managed/native binary hashes and
no changes to the deployment files tracked by the runner. The native SHA256
is `6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014`;
Runtime is `957c14b102c8a72056216b091e31d93a60f084f006c39f2c18c4339d6523d814`.
The baseline's managed source is HEAD commit
`04a5faa9e641fc0b06de661be1191afbc6edaf50`, but its preserved native build
already includes the V4.1 graph additions. It is not a fresh HEAD-native
build. This rerun is not an isolated numerical-path or individual-fix A/B.

The same model files are identified by the immutable download revisions,
sizes and hashes in [model-sources.json](../tokenizer-microbenchmark/model-sources.json).
This run reused the earlier full-file weight hashes after checking identical
path, byte size and nanosecond modification time; it did not freshly rehash
the weights. Launches used GPU 7, context 8192, four scheduler slots, batch
and prefill chunk 256, F16 KV, greedy sampling, disabled thinking and prefix
cache, and four GGML CPU threads. Exact commands, environment and sampling
are retained in each report. Tool-result fixtures retain their original
unconstrained final-answer request; no serial-tool or structured-result
override was added to improve the pass count.

The [runner snapshot](run-final-existing-models.py) and
[validator snapshot](validate_inference.py) match the recorded source hashes.
To reproduce the local audit against the retained raw directories:

```sh
python3 audit.py /path/to/final-existing-regressions /path/to/existing-historical --output /tmp/final-quality-audit
```

To rerun inference on the VM, use the runner's `--source-host-dir`,
`--native` and `--expected-runtime-sha256` arguments with a new `--label`;
it refuses to overwrite an existing deployment or result directory. The
runner only starts and stops its own servers. No performance conclusion,
broader absence of regressions, or llama.cpp parity follows from these
cases. Subsequent JSON runs reuse request tags from this run and must be
assessed separately. In particular, Qwen3 `json-c4-r0-i0`, recorded HTTP
request SHA256
`e1c5ae800ba0b4c69abce54cb2dd6301f7a8a6329b7a9892d83b8514eefaacfc`,
passed here with a flat 21-token answer but failed in both later JSON runs
with a nested 25-token answer. The completed JSON rerun's baseline passed
that same case. Differences in run, warmup and repetition context do not
make the input different and do not erase this additional failure.
