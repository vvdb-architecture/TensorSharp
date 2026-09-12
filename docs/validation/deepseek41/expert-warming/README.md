# CPU expert warming helper review

`eng/dsv41-warm-experts.py` performs real reads of the three routed-expert matrices (`ffn_gate_exps`, `ffn_up_exps`, `ffn_down_exps`) in each leading CPU-MoE layer. This matches native placement, which marks layers `0..n_cpu_moe-1` for offload. Run it outside performance windows; the pages remain evictable, and successful reads do not prove residency or checksum integrity.

Local verification used the existing five-layer F32 numerical fixture, selecting four layers. Instrumented unbuffered reads matched all twelve reported ranges exactly, totaling 12,582,912 bytes. A separate binary GGUF v3 parser independently verified absolute offsets and sizes; the fixture SHA remained unchanged. The same real matrices were repacked using the upstream GGUF writer into three shards, and the helper again read exactly the twelve selected ranges, excluding the fifth layer. Later shards intentionally lacked `general.architecture`, as the writer permits. Both reports and [verification summary](summary.json) are retained here; their local I/O durations are not model benchmarks.

Review fixed one validation gap: shard zero must identify `deepseek41`. Later shards may omit that metadata but cannot contradict it. A real malformed GGUF without the first-shard architecture now fails before expert warming. Duplicate tensor errors also identify the duplicate names.

The locally cached published Q2_K first-shard binary header independently matched all 71 cached tensor descriptors. It contains all three routed matrices for layers zero and one: gate/up use Q2_K; down uses Q3_K. The six selected ranges total 9,838,264,320 bytes; [exact shapes and absolute offsets](published-first-shard.json) are retained. Headers for the remaining six published shards were unavailable locally and were not independently inspected during this review. No remote reads or warming were performed.

Example preparation for the four-layer CPU-MoE benchmark:

```bash
PYTHONPATH=/workspace/llama.cpp/gguf-py \
  /workspace/deepseek41-work/reference-venv/bin/python eng/dsv41-warm-experts.py \
  /workspace/models/DeepSeek-V4.1-Flash-Q2_K/DeepSeek-V4.1-Flash-Q2_K-00001-of-00007.gguf \
  --layers 4 --workers 3 --report /workspace/deepseek41-work/cpu-moe4-expert-warm.json
```

Only start timed requests after this process exits and other preparation work stops.
