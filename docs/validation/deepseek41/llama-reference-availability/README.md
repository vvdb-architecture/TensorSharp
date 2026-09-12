The September 11, 2026 **13:08–13:10 UTC** source check found no callable DeepSeek-V4.1 GGUF runtime in official llama.cpp master, the supplied Hugging Face patch, or that patch's linked upstream PR. This check did not build or execute any new reference version. It does not establish absence from every third-party fork or another inference framework.

The **actually tested baseline remains `df03399b885831b2a1603b3abb0d8c156808e363`**. Its checkpoint load rejected `deepseek41`; the [preserved environment record](tested-baseline-environment.json) identifies `/workspace/deepseek41-work/llama-v41-load.log` as the original VM log. That old runtime result is separate from this later, unbuilt source inspection. No checkout, repository refs, private server, model files or baseline binary were changed.

| Inspected source | Pinned revision | Result |
| --- | --- | --- |
| Official llama.cpp master | [`5bda51bfbc62e64193221e639f6ad4e08767d760`](https://github.com/ggml-org/llama.cpp/commit/5bda51bfbc62e64193221e639f6ad4e08767d760), committed 11:12:55 UTC | No `deepseek41` runtime registration |
| Supplied GGUF repository | [`3b4c2f9dc4f3045b96b18620f50046be6ffc39be`](https://huggingface.co/vcruz305/DeepSeek-V4.1-Flash-GGUF/tree/3b4c2f9dc4f3045b96b18620f50046be6ffc39be/llama.cpp/patches) | Same two conversion-patch files as the pinned Q2 checkpoint revision |
| Linked conversion PR #28696 | [`b12818a24407175d941e9299e7b5fb7874a654d9`](https://github.com/vcruz305/llama.cpp/commit/b12818a24407175d941e9299e7b5fb7874a654d9), observed open/draft, last updated 12:51:17 UTC | Only three converter/Python metadata files changed; no runtime implementation |

The official [architecture enum](https://github.com/ggml-org/llama.cpp/blob/5bda51bfbc62e64193221e639f6ad4e08767d760/src/llama-arch.h) and [architecture name map](https://github.com/ggml-org/llama.cpp/blob/5bda51bfbc62e64193221e639f6ad4e08767d760/src/llama-arch.cpp) include `deepseek4` but no `deepseek41` or `deepseek_v41`. The pinned [model dispatcher and loader](https://github.com/ggml-org/llama.cpp/blob/5bda51bfbc62e64193221e639f6ad4e08767d760/src/llama-model.cpp) likewise lack V4.1 and reject an unknown architecture. Complete downloaded bytes of these three files are retained under [snapshots](snapshots/), so this conclusion does not rest solely on a filename search. The [untruncated tree summary](tree-summary.json) corroborates it.

The supplied [patch README](https://huggingface.co/vcruz305/DeepSeek-V4.1-Flash-GGUF/blob/3b4c2f9dc4f3045b96b18620f50046be6ffc39be/llama.cpp/patches/README.md) identifies conversion as its scope. Its script changes `conversion/deepseek.py`, `conversion/__init__.py` and `gguf-py/gguf/constants.py`. Both files were independently downloaded at current HF revision `3b4c2f9d…` and the **tested weights revision `8e0c4de3cb6519bfc11ed69dc87184b457a57bb5`**; the complete bytes match:

| Patch file | Bytes at each revision | SHA-256 at both revisions |
| --- | ---: | --- |
| `patch_llamacpp_v41.py` | 22,580 | `8699042d64765ab4b42f10a773b5903f53756d3aaeb0c147e49d14b9fdc26a28` |
| `README.md` | 4,271 | `2ed2ec475fba94d18c2d21c855af1d5090ac27a4b98ff27c3f89fc31130f24ed` |

[PR #28696](https://github.com/ggml-org/llama.cpp/pull/28696) states that runtime work is absent and converted files cannot yet load. Its captured [PR response](snapshots/pr28696.json) pins the observed head, state and description; the [complete changed-file response](snapshots/pr28696-files.json) contains only those three Python files. The live PR can change after this observation; the retained snapshot defines this result.

[report.json](report.json), [source URLs and hashes](sources.json), [head observations](head-observations.json) and [manifest.json](manifest.json) preserve provenance. Twelve complete small source/API snapshots total 394,789 bytes. The approximately 1 MB full tree response was omitted from this compact bundle; its exact digest and primary API URL remain recorded. This was a read-only network/source check, followed by local evidence curation, with no VM access, compilation, inference or weight downloads.

The requested llama.cpp quality/performance comparison therefore remains **unverified**. An unavailable reference is not a passing parity baseline.

The retained llama.cpp source files are covered by the accompanying [MIT license and copyright notice](snapshots/llama-LICENSE). The notice is copied from the local reference checkout; it is not an additional runtime availability observation.
