# Existing-model regression evidence

These comparisons use Qwen3-0.6B Q8_0, Qwen3.5-0.8B Q8_0 and
Gemma-4-E2B-it Q4_K_M on GPU 7 of the requested eight-A40 VM. Model hashes,
binary hashes, case exclusions and individual timing flags are retained in
the JSON summaries.

The baseline managed source is commit
`04a5faa9e641fc0b06de661be1191afbc6edaf50`, built in an isolated directory.
Its native library is the preserved build from before the CUDA source-F32
precision change. That native build already includes the added V4.1 graph
and expanded scheduler capacity; this is not a separately rebuilt HEAD
native-library baseline. The compared existing architectures do not select
the V4.1 graph. Current native source-F32 requests are confined to V4.1.

- [Final 3651 + native 6b3 quality rerun](final3651-native6b3/README.md):
  75 matched cases introduced no failures relative to either the HEAD
  managed baseline or the earlier after build. Final passes were 12/25
  for Qwen3, 18/25 for Qwen3.5 and 9/25 for Gemma: **39/75**, with all
  36 failures retained. The separate Chinese/emoji JSON suite passed
  **15/15**. All 12 current/historical raw report hashes and all 75 paired
  request identities were independently checked; offline replay reproduced
  every current result. This describes that run and its workload order.
  Later [JSON runs](../json-performance/completed-r2/README.md) exposed
  additional failures for identical requests under a different run, warmup
  and repetition context; the zero-introduced result does not extend to
  those runs. Timings here were unqualified; the runner's overall failure
  status remains intact.
- [Earlier quality comparison](final-before-after-quality-summary.json):
  the prior 75-case stage is preserved, including its existing strict-format
  failures and unqualified timings.
- [Initial performance comparison](performance-before-after-summary.json):
  225 cases, three repeats, concurrency 1 and 4. Timings include only complete
  passing workflows with matching per-turn token lengths and timer sources.
  The initial Qwen3 single-request decode slowdown and other flags are
  preserved. [Window manifest](performance-window.json).
- [Alternating Qwen3 rerun](qwen3-alternating-comparison.json): after,
  baseline, after, baseline; each process has a separate unmeasured 512-token
  decode warmup, three measured decode requests and tool workflows at
  concurrency 1 and 4. Both after runs passed all 18 cases; baseline runs
  passed 13 and 12. Failed or differently sized workflows are excluded from
  timing comparisons. [Window manifest](qwen3-alternating-window.json).
- [Telemetry summary](qwen3-alternating-telemetry-summary.json): the full
  V4.1 model remained resident and idle. GPU clocks were stable and no
  thermal/power violations were observed. Builds, tests and other GPU work
  were paused during both performance windows.

The alternating sustained-decode ratios were 1.190 and 0.980, with a combined
median paired ratio of 1.007. The original sustained slowdown did not
reproduce consistently. Per-pair total-wall and first-token latency flags
remain; these runs do not establish a blanket latency guarantee or llama.cpp
parity.

The final [Qwen3.5 grammar-cost diagnostic](grammar-cost-diagnostic/README.md)
separates cold setup from warmed request/mask operations. It does not
reproduce the roughly 7 ms per-request factory/mask increase needed to
explain the later VM JSON TTFT flag, which remains unresolved. A separate
[Qwen3 mask review](qwen3-json-mask-review/README.md) finds no duplicate token
byte groups or changed ASCII choices at ten early JSON prefixes; it does
not identify the cause of the same-request semantic failure in later runs.

A separate [warmed tokenizer/render diagnostic](tokenizer-microbenchmark/summary.json)
compares the managed implementations locally on macOS ARM64. All nine BMP
short/tool/long prompts retained identical rendered strings and token hashes.
Qwen3 encoding was 1.154/1.280/2.176 times as fast and allocated 22%/31%/38%
fewer bytes; Qwen3.5 encoding was 1.127/1.196/2.433 times as fast and allocated
19%/27%/38% fewer bytes. Render medians stayed within 2.2%, and Gemma encoding
within 2.4%. This identifies no repeated tokenizer/render slowdown; it is
not a VM HTTP or GPU throughput measurement.

The diagnostic retains its [source](tokenizer-microbenchmark/Program.cs),
[project](tokenizer-microbenchmark/probe.csproj),
[inputs](tokenizer-microbenchmark/inputs.json), per-run samples and
[binary/metadata hashes](tokenizer-microbenchmark/provenance.json). Build with
`dotnet build probe.csproj -c Release -p:EngineBin=/path/to/managed/binaries`
and run `dotnet probe.dll MODEL.gguf inputs.json output.json LABEL`. Only
tokenizer metadata is read; no tensor weights or native inference are used.

Raw requests, generated responses, per-request timings, launch settings,
telemetry and runner scripts remain under
`/workspace/deepseek41-work/existing-regressions` and
`/workspace/deepseek41-work/dsv41-*-regressions.py` on the VM. Process snapshots
are omitted from this checked-in subset because unrelated service command
lines can contain credentials. No generated response was repaired or
normalised after the run.
