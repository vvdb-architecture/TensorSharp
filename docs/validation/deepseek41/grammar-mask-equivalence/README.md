# Final grammar-stage mask comparison

The local 3,566-test runtime and final 3,582-test runtime both passed **14/14** probes using the Q2_K checkpoint's actual 129,280-token vocabulary. All **785 visited presampling masks** were byte-identical between stages. Token IDs and the first rejection positions also matched.

[comparison.json](comparison.json) records the per-case mask hashes, source and assembly hashes, and links to the original correctness manifests. [local3566.json](local3566.json) and [local3582.json](local3582.json) retain each complete probe result. These local assemblies correspond to the same source stages as the separately built VM hosts; this comparison did not execute the VM assemblies or load model weights.

The probes cover literal and split Unicode in JSON, ordered reasoning/tool activation, raw Unicode/XML strings, escaped reserved delimiters, open nested maps, and malformed or unknown tool-call rejection. This verifies unchanged masks at the exercised V4.1 states. It does not claim equivalence for all possible grammars: the final fix intentionally changes generic character-class masks when an incomplete Unicode prefix has no valid completion.

Reproduce each stage with its retained host or test assembly directory:

```sh
dotnet build eng/tests/dsv41-tool-grammar/dsv41-tool-grammar.csproj \
  -c Release -p:EngineBin=/absolute/path/to/stage \
  --output /tmp/stage-mask-probe
dotnet /tmp/stage-mask-probe/dsv41-tool-grammar.dll \
  /path/to/first-model-shard.gguf /tmp/stage-mask-report.json
```

The reports used a saved JSON copy of the first shard's metadata; its hash is retained. No remote computation or I/O overlapped the qualified placement benchmarks. Probe timings are diagnostic and do not establish model inference performance.
