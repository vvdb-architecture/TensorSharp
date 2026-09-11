# Qwen3 JSON prefix-mask review

The actual Qwen3 vocabulary has **no duplicate byte groups** among the
grammar-eligible token IDs. The new alias-ID support therefore cannot
explain the additional Qwen3 JSON failure in the
[later repeated run](../../json-performance/completed-r2/README.md).
This is a metadata-only mask review; it observes no model logits or inference.

The [comparison](comparison.json) uses the same metadata-only Qwen3 GGUF
and the local HEAD/final Runtime assemblies used by the
[cost diagnostic](../grammar-cost-diagnostic/README.md). All 151,936 token
slots were inspected, excluding the same 27 special IDs; the eligible
token-byte tables and generic JSON grammar source were identical.

Seven of ten early-prefix masks were byte-identical: the empty prefix,
`{`, `{\n\n`, the spaced prefix, and prefixes after either candidate key and
colon. Three masks inside a JSON string gained 1,255 token IDs, all of which
end in a valid unfinished UTF-8 sequence. No ASCII IDs changed and no IDs
were removed. This is the intended partial-UTF-8 correction. Both the
successful flat object and the failing nested object remain legal under
generic `json_object` syntax; the request's key/value contract is enforced
by the semantic validator. No output is repaired or reclassified.

The source [probe](Program.cs) and [project](probe.csproj) reproduce the
token-byte groups and all ten complete masks. The comparison retains every
changed ID with its exact bytes, source/metadata/assembly hashes and hashes
of the complete external mask files. Full token tables and binary masks
remain under `/tmp/deepseek41-reference/qwen3-json-mask-review` and can be
regenerated from the pinned model metadata. This removes the duplicate-ID
explanation; it does not identify a numerical or scheduling cause.

```sh
dotnet build probe.csproj -c Release -p:EngineBin=/path/to/baseline/or/final/dlls -o /tmp/json-mask-probe
dotnet /tmp/json-mask-probe/probe.dll /path/to/Qwen3-0.6B-Q8_0.gguf /tmp/json-masks LABEL
```
