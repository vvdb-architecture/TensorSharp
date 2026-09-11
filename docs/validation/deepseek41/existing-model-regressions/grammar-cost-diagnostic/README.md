# Qwen3.5 JSON grammar cost diagnostic

This local metadata-only probe does not reproduce a per-request grammar
factory or mask cost large enough to explain the approximately 7 ms
single-request TTFT increase in the [completed VM JSON run](../../json-performance/completed-r2/README.md).
That VM latency regression remains recorded and unresolved. These macOS
ARM64 timings are not a substitute for the VM HTTP measurements.

Four separate processes ran in baseline/final/final/baseline order against
the same Qwen3.5 tokenizer metadata and the actual identical JSON answer
from all 15 measured VM cases. Both implementations admitted its same 16
encoded tokens. Every measured operation has at least 350 ms of warmup and
five blocks of at least 100 ms. The [source](Program.cs),
[project](probe.csproj), four raw reports and [summary](summary.json) retain
all samples, allocation counts, grammar/token/metadata hashes and assembly
identities. No native calls or model tensor reads occur.

| Warm operation | HEAD baseline, range of run medians | Local final, range of run medians |
|---|---:|---:|
| Factory + new request constraint + initial mask | 0.102–0.105 µs | 0.109 µs |
| Apply initial mask to a full vocabulary logit array | 85.5–87.4 µs | 98.7–98.9 µs |
| All 16 response masks + accept tokens | 1.058–1.066 µs | 1.034–1.037 µs |
| All 16 response masks + apply masks + accept | 0.881–0.919 ms | 0.857–0.866 ms |

The shared-cache factory path allocates the same 120 bytes per fresh
constraint. The final initial-mask application was about 12 µs slower
locally; this result does not erase the VM TTFT flag or establish its cause.
Cold tokenizer/trie construction and explicitly discarded mask caches take
far longer and vary substantially between processes. Those samples are
preserved, but are not interpreted as a cost paid by every warmed request.
The server shares these caches per tokenizer/grammar and creates independent
request parse positions. The probe deliberately separates cold and warmed
operations; tokenization and proof hashing are excluded from warm timings.

The baseline Runtime is local HEAD build `253d4942…`; final is local managed
3651-stage source build `1542edf8…`. The VM builds have different
platform-specific hashes, so this is a source-level diagnostic rather than
an exact VM-binary A/B. Exact source hashes are retained in the summary.
Tokenizer metadata comes from the pinned Qwen3.5 model described in
[model-sources.json](../tokenizer-microbenchmark/model-sources.json); its
metadata-only SHA256 is `60451b1d5b7a3166b08735f5f422a5be21687fa984e9960f5165028ff6ffe1ba`.

```sh
dotnet build probe.csproj -c Release -p:EngineBin=/path/to/baseline/or/final/dlls -o /tmp/grammar-probe
dotnet /tmp/grammar-probe/probe.dll /path/to/Qwen3.5-0.8B-Q8_0.gguf /tmp/cost.json LABEL
```

The probe reads only GGUF metadata, even when passed the complete file.
