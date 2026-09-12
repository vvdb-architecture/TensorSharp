# V4.1 Engram implementation review

Read-only source review, September 11, 2026. No vLLM/SGLang performance result or full-model numerical comparison is claimed. Source hashes and local/upstream revisions are retained in `vllm-sglang-review-provenance.json`; downloaded official source files are adjacent.

## vLLM: implemented

Pinned upstream main `9dd969da096e37256ee37e24f6a4689d860f39ce`. The Engram implementation matches the local source at `b5d4186300fda544ec5ec51113d569159c5c52bd`; latest upstream configuration also adds V4.1 to its explicit architecture allowlist.

* [Engram embedding, lines 622–748](https://github.com/vllm-project/vllm/blob/9dd969da096e37256ee37e24f6a4689d860f39ce/vllm/models/deepseek_v4_1/common/engram.py#L622): shards complete hash-head vocabulary ranges across tensor-parallel ranks. GPU-resident storage is supported; optional CPU storage uses pinned host tensors plus cached UVA pointer views. The pointer cache is not an embedding-row cache. The lookup kernel reads FP8 and exponent scales, returning BF16 rows. Its persistent grid bounds worker blocks by SM count.
* [Prepared row storage, lines 879–970](https://github.com/vllm-project/vllm/blob/9dd969da096e37256ee37e24f6a4689d860f39ce/vllm/models/deepseek_v4_1/common/engram.py#L879): preallocates row output storage and gathers required rows before layer consumption. Model `nvidia/model.py` lines 553–603 hashes the full flat token batch once, applies image masking, and prepares both Engram tables before its decoder loop.
* The `background` lookup parameter can reduce the persistent grid, but the inspected model preparation path does not enable it or run the lookup on a separate CUDA stream. The token `hash_cache` holds causal token history, not hot embedding rows. No bounded hot-row cache or row-deduplication implementation was found in these paths.

## SGLang: implementation in an open PR

[PR 38798](https://github.com/sgl-project/sglang/pull/38798) is open/unmerged at reviewed head `da64c5cbb8cf6bfd39be19da43573fdfd484c43a`. Checked main `4309c7ce19dc42fb42cc9e7d883691c8dd8bda10` and local `94ce940ff80f74787ab297d20a6bc828bfe2b454` do not contain this V4.1 Engram implementation. It must not be described as a released SGLang feature.

* [Engram storage and gather](https://github.com/sgl-project/sglang/blob/da64c5cbb8cf6bfd39be19da43573fdfd484c43a/python/sglang/srt/layers/engram.py#L531): supports device-resident shards or opt-in host-backed storage. The host layout uses shared memfd or private anonymous storage, optional host registration and huge-page advice. Lines 744–817 gather directly from the backing storage and reduce private owned-row outputs.
* [Gather kernel](https://github.com/sgl-project/sglang/blob/da64c5cbb8cf6bfd39be19da43573fdfd484c43a/python/sglang/kernels/ops/embeddings/engram_gather.py#L17): one program per selected row performs FP8/E8M0-to-BF16 conversion. It does not deduplicate or maintain a hot-row cache.
* [Model overlap](https://github.com/sgl-project/sglang/blob/da64c5cbb8cf6bfd39be19da43573fdfd484c43a/python/sglang/srt/models/deepseek_v4.py#L3508): an opt-in side CUDA stream can prepare layer 14 lookup plus its WKV projection, then waits before that layer consumes it. Eligibility is narrow: one-token normal decode, pipeline-parallel size 1, no DP attention, no vision layers, shared host-table layout and the selected FlashInfer CuTe DSL MXFP8 implementation. Lines 3651–3697 contain stream launch, lifetime recording and the dependency wait. It is not general batch/prefill overlap.
* [Issue 38856](https://github.com/sgl-project/sglang/issues/38856) proposes broader rows-only overlap and an optional GPU hot-row cache. These are proposals, not performance evidence or completed implementation in the inspected PR.

## Transfer into TensorSharp

The bounded implemented candidate adopts the gather-all staging pattern while retaining TensorSharp's evictable Q2_K mappings and exact existing CPU dequantizer. All required table/hash metadata is validated first, then the existing persistent worker pool interleaves row tasks across tables. Separate table outputs are uploaded after the workers drain. Groups cap staging at 64 MiB, except that a single output exceeding the cap retains its pre-existing one-table allocation bound. The prepared callable owns its scalar parameters and dequantizer; validated hashes and table/output storage remain stable until completion. Images produce zero rows without reading the table.

This scheduling choice is TensorSharp-specific, inspired by upstream preparation order; it is not a copied vLLM/SGLang CPU worker implementation. Local warm-memory tests compare the same two prepared tables and 16 workers with two separate pool submissions versus one interleaved submission. Raw sample logs and exact-output tests are retained under `../engram-joint/`. Linux private-file and full-model results are separate follow-up evidence.

A full pinned/UVA conversion would make tens of GiB of tables non-reclaimable and requires a Q2_K device gather implementation and hardware/memory validation. It is not a safe automatic replacement for the current mapping policy. GPU-resident tables are possible in principle: the architecture does not require host storage, and ggml already implements Q2_K GET_ROWS, but TensorSharp must rebalance ordinary layer weights and account for KV/workspace to fit each table, or shard whole hash heads. Exact dequantization/row-order/image-mask tests must precede adoption.

Batch deduplication and bounded hot-row caching remain separate candidates. Hash-head bucket offsets make the 24 columns distinct within one token; useful duplication must be measured across tokens/prefixes. Cache keys need model/table/row/type identity, lossless storage and immutable validated hashes. The current OS cache already sharply lowers repeated-prompt host input time, so new allocation/locking costs should be justified by measured hits.
