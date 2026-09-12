# Pinned Engram source review

These are unchanged research snapshots taken before the combined implementation. Source inspection supplies mechanisms and constraints; it is not an upstream inference benchmark. [llama.cpp/Hugging Face findings](llama-hf-review.md) and [vLLM/SGLang findings](vllm-sglang-findings.md) contain the pinned primary-source links and line references.

| Project | Inspected revision | What was established |
|---|---|---|
| llama.cpp upstream | `8172e6577ac2b35de1ec1e5d1c0aaad6c4a2129f` | Generic lazy mmap ranges use RANDOM advice; no V4.1 Engram runtime was identified. This revision was not built. |
| Supplied HF conversion patch | `dbbd1f37aa33ed18fc38c2e367463bd73c23ed33` | Conversion-only patch and README remained byte-identical to the retained checkpoint revision. |
| vLLM upstream | `9dd969da096e37256ee37e24f6a4689d860f39ce` | Implemented table preparation and device/pinned-UVA storage paths; no observed hot embedding-row cache in those paths. |
| SGLang PR 38798 | `da64c5cbb8cf6bfd39be19da43573fdfd484c43a` | Open/unmerged at review time. Narrow optional decode overlap is implemented in that PR; broader overlap and hot-row caching remain proposals. |

TensorSharp adopted bounded joint table scheduling and scoped mapping advice, with separate [implementation tests](../phase2-final/README.md). This does not imply adoption of pinned/UVA storage, device-table sharding, speculative prefetch, asynchronous GPU overlap or hot-row caching. Those candidates require their own lifetime, memory, numerical and performance evidence.

[llama/HF manifest](llama-hf-manifest.json), [review report](llama-hf-report.json), [download identities](downloads.json), and [vLLM/SGLang provenance](vllm-sglang-review-provenance.json) bind the retained files to immutable revisions. Their paths and current-source hashes describe the time of review, including phase1 core `24246c…`; they do not identify the later combined core. The final-stage offline audit verifies the source snapshots against those recorded hashes.
