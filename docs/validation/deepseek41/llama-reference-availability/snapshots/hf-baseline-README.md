# llama.cpp conversion patch for DeepSeek-V4.1

The GGUFs in this repo were converted with a patched llama.cpp. Upstream does not yet know
`DeepseekV41ForCausalLM`, so a stock checkout cannot produce them.

The same change is submitted upstream as
[ggml-org/llama.cpp#28696](https://github.com/ggml-org/llama.cpp/pull/28696). This copy is here so
you can convert V4.1 yourself before that lands.

## Apply

```sh
python patch_llamacpp_v41.py /path/to/llama.cpp            # apply
python patch_llamacpp_v41.py /path/to/llama.cpp --check    # report status, change nothing
python patch_llamacpp_v41.py /path/to/llama.cpp --revert   # undo
```

It is idempotent, and it writes a `.py.v41orig` backup beside every file it edits.

## What it touches

| file | change |
|---|---|
| `conversion/deepseek.py` | `DeepseekV41Model`, subclassing the existing `DeepseekV4Model` |
| `gguf-py/gguf/constants.py` | the `deepseek41` arch, its tensor list, and four engram tensor entries |
| `conversion/__init__.py` | one registry line |

## Architecture name

Files produced by this patch carry `general.architecture = deepseek41`.

llama.cpp does not mirror the HF `model_type`, it drops the `_v`: `deepseek_v2` became
`deepseek2`, `deepseek_v3.2` became `deepseek32`, `qwen2_moe` became `qwen2moe`. Only 10 of the
150 arch strings in `constants.py` contain an underscore at all, so V4.1 is `deepseek41`.
vLLM's `deepseek_v41` names a set of vLLM plugins, a tokenizer mode and two parsers, and is not
a GGUF architecture value.

The behavioural reason matters more than the convention. Riding on `deepseek4` sends a V4.1 file
to the V4 loader, which then asks for `output_hc_fn`, `output_hc_base` and `output_hc_scale`.
V4.1 ships none of the three, while it does ship the per-layer `hc_attn_*` and `hc_ffn_*`, so the
loader sees a confusing partial match rather than refusing the file. `deepseek4` was also not
strictly correct: V4.1 emits `attn_kv_a_norm`, which `DEEPSEEK4`'s own declared tensor list does
not contain.

The `deepseek41` tensor list is the 39 families the converter actually writes. It differs from
`DEEPSEEK4` by dropping `HC_HEAD_{FN,BASE,SCALE}`, `FFN_GATE_TID2EID`, `ATTN_KV_NORM`,
`ATTN_COMPRESSOR_APE`, `INDEXER_COMPRESSOR_*` and all six `NEXTN_*`, and by adding the four
engram entries plus `ATTN_KV_A_NORM`. This patch leaves `DEEPSEEK4` itself untouched.

This name is not yet settled upstream. It is proposed on the PR, and if the maintainers choose
differently the arch string in already-converted files can be restamped with
`gguf-py/gguf/scripts/gguf_new_metadata.py` without re-quantizing.

## Why a subclass is not enough on its own

Four things differ from V4 and each one is quiet rather than loud:

- **FP8 scale block size.** V4 hardcodes `repeat_interleave(128, ...)` to match its
  `weight_block_size` of `[128, 128]`. V4.1 declares `[32, 32]`. Running the V4 path unchanged
  rescales every dequantized weight, raises nothing, and yields a model that loads and reads
  fluently while being numerically wrong. The block size is read from `quantization_config`.
- **`num_hash_layers`** is absent in V4.1 while the V4 path reads it unconditionally.
- **Nested config.** V4.1 puts text parameters under `text_config` and vision under
  `vision_config`. Overriding `load_hparams` does not work, because `ModelBase.__init__` calls it
  explicitly rather than through the instance, so they are promoted in `index_tensors`.
- **The engram tables.** Two of them, on layers 1 and 14, each `384,006,168 x 256`. The inherited
  dequant computes `weight.float() * scale` over the whole tensor, about 393 GB as float32 for a
  single table. They get a streaming path instead: read in row blocks straight from the
  safetensors shard, quantized per block, accumulated into a disk backed memmap. Their scale
  layout also differs from the rest of the checkpoint, `[rows, 8]` rather than the
  `[rows/32, cols/32]` tiling the linear weights use.

Tunable through `_V41_ENGRAM_CHUNK_ROWS` and `V41_ENGRAM_TMPDIR`.

## Status

Conversion only. A converted file does not load yet: the `deepseek4` runtime wants
`output_hc_fn`, `output_hc_base` and `output_hc_scale`, and V4.1 does not ship those tensors.
Runtime support is separate work and is not in this patch.
