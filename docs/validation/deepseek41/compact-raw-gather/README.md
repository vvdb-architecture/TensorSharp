# Opt-in compact raw gather validation

`TS_DSV41_COMPACT_RAW_GATHER=1` is default-off and changes only V4.1 sparse single-token gather. The first gather runs on the raw-cache GPU and preserves the finite raw-mask rows in ascending physical ring order. The second runs on the shared compressed-cache GPU. Existing CPU/CUDA KGATHER kernels are unchanged.

The released geometry has 128 visible raw rows and 512 selected compressed rows. CUDA head-512 attention requires a key width divisible by 256, so 128 masked duplicates pad the raw prefix: the final tensor is 768 rows. The initial unpadded 640-row proposal was rejected during review before GPU execution. Logical cross-source input/output traffic becomes 1 MiB per affected layer, versus 3 MiB with a 1,280-row ring. This calculation does not establish a performance improvement; an extra local gather launch and synchronization costs must be measured.

The CPU test `GgmlOpsDsv41RawGatherTest` passed 16,384 independent comparisons against the original raw-mask formula, five invalid/incomplete-window guards, and 34 bit-exact paired gathers. Those gathers use head size 512, window 128, top-k 512, and ring sizes 512 and 1,280, including wraps and padding. Arbitrary half bit patterns verify lossless row copying.

Five native fixture runs passed **1,200/1,200 logit comparisons**, comparing masked-dense attention, unchanged sparse gather, and compact gather. Maximum absolute difference was 2.623e-6; every argmax matched. They cover the small fixture's separate sparse-selection/full-window thresholds, actual top-k-512 selection around position 1,024, raw-ring wraps, graph reuse, and CPU flash attention on/off. These generated models have five layers, text width 256, attention head size 64, and window 8. They test graph behavior, not full-checkpoint quality or GPU kernel eligibility.

`provenance.json` records the loaded native-library hash, source hashes, generated fixture hashes and arguments. The five JSON reports retain all comparisons. Fixture GGUFs remain under `/tmp/deepseek41-reference`; they are reproducible and are not committed.

Reproduce from the repository root with NumPy, tokenizers and gguf installed (or the local llama.cpp `gguf-py` directory on `PYTHONPATH`):

```bash
python eng/dsv41-fixture.py /tmp/compact-small --f32 --cuda-index --index-topk 2 --token-count 16
python eng/dsv41-fixture.py /tmp/compact-long --f32 --cuda-index --index-topk 512 --token-count 1544

cmake -S TensorSharp.GGML.Native -B /tmp/compact-build -DTENSORSHARP_GGML_NATIVE_BUILD_TESTS=ON
cmake --build /tmp/compact-build --target GgmlOps GgmlOpsDsv41RawGatherTest
/tmp/compact-build/GgmlOpsDsv41RawGatherTest

# Use libGgmlOps.so on Linux; run small and ring-1280 cases with FA=0 and FA=1.
TS_DSV4_FA=0 python eng/tests/dsv41-gather.py /tmp/compact-small \
  --library /tmp/compact-build/libGgmlOps.dylib --compare-compact-raw --ubatch 32 \
  --prefixes 1 4 5 6 7 8 9 --decode-tokens 16
TS_DSV4_FA=0 python eng/tests/dsv41-gather.py /tmp/compact-long \
  --library /tmp/compact-build/libGgmlOps.dylib --compare-compact-raw --ubatch 256 \
  --prefixes 1020 1023 1024 1025 1026 1532 --decode-tokens 10
TS_DSV4_FA=0 python eng/tests/dsv41-gather.py /tmp/compact-long \
  --library /tmp/compact-build/libGgmlOps.dylib --compare-compact-raw --ubatch 1024 \
  --prefixes 1020 1023 1024 1025 1026 1276 1280 1404 --decode-tokens 10
```

The subsequent requested-VM CUDA runs used native SHA `b26cac3e40ff67b6de077063cd7a3c68e683220f0bc60237edf728ec6d218f1f`, two and eight visible GPUs, and both ring sizes. With flash attention disabled, all **924/924** comparisons passed the original strict `atol=rtol=2e-5` bound; maximum absolute error was 1.58e-6. With flash attention enabled, only **208/924** comparisons passed that bound, although every argmax matched. The unchanged gathered path already differs from masked-dense attention by up to 0.0481; compact versus unchanged gather differs by up to 0.0140 (maximum relative L2 0.00617). These strict failures remain in the eight raw CUDA reports and [CUDA summary](cuda-summary.json). They are not counted as numerical parity, and the optimization remains default-off pending full-checkpoint comparison.

All twelve native CTests passed across the one-GPU run and the separate two/four/eight-GPU TP run. The first run's three insufficient-device skips were explicitly rerun with eight visible GPUs; both logs are retained here. The separate two-GPU microbenchmark checks actual head-512 attention support and independently measures the transfer tradeoff before any default change.

The dedicated A40 microbenchmark window ran after all other builds and numerical jobs stopped; the existing full-checkpoint server stayed resident and idle. Sampled GPU clocks were 1,740/7,251 MHz, P0, at 46–48 °C. All four exact-gather and independent attention checks passed (maximum absolute error 3.82e-5; relative L2 below 0.000437). The 21 alternating repetitions produced:

| Physical raw ring | Window wraps | Original median | Compact median | Speedup |
| --- | --- | --- | --- | --- |
| 512 rows | Yes | 299.436 µs | 251.876 µs | 1.189× |
| 512 rows | No | 375.693 µs | 301.185 µs | 1.247× |
| 1,280 rows | Yes | 607.180 µs | 300.862 µs | 2.018× |
| 1,280 rows | No | 441.660 µs | 233.141 µs | 1.894× |

The [complete samples](compact-bench-qualified.jsonl), [run manifest and hashes](compact-bench-manifest.json), and [telemetry](compact-bench-telemetry.csv) retain the evidence. This measures a two-GPU gather-and-attention graph, not complete model throughput. The earlier three-repeat `correctness-only` run overlapped other tests and supplies no qualified timing evidence. Per-compute ggml DEBUG logging is suppressed in the timed executable; warnings and errors remain enabled.

The manual benchmark target is intentionally excluded from CTest, so it cannot silently collect timings alongside other tests:

```bash
cmake --build TensorSharp.GGML.Native/build --target GgmlOpsDsv41CompactGatherBench
CUDA_VISIBLE_DEVICES=0,1 TensorSharp.GGML.Native/build/GgmlOpsDsv41CompactGatherBench 21 \
  > compact-gather-benchmark.jsonl 2> compact-gather-benchmark.log
```

Run this only after other inference, test, build and I/O jobs have stopped. It places the raw ring and queries on GPU 0, the compressed cache on GPU 1, and checks that gather and attention execute on their intended CUDA backends. Both ring sizes run at wrapping and non-wrapping positions. Before timing, every gathered half-precision row must match its source bit for bit, and all 32,768 attention outputs must match an independent double-precision oracle within the stated F16 envelope (absolute 3e-4 plus relative 1.5e-3; relative L2 below 0.0015). The output retains all alternating timing samples, medians, errors and graph split counts. Record the binary hashes and GPU telemetry alongside the JSONL. Reported transfer bytes describe the graph's logical transfers; they are not measured PCIe traffic.
