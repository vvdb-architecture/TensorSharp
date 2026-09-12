# Joint Engram table scheduling

The candidate prepares both Engram tables before submitting their row reads to the existing 16-worker pool. It interleaves table tasks in one submission, then uploads each output after all workers finish. Row hashes, quantization and arithmetic are unchanged. Staging groups are bounded to 64 MiB; an individually larger table output retains the preceding one-table allocation bound.

The Linux VM scratch benchmark passed **24/24 exact-output comparisons**. All 18 cold trials confirmed zero selected pages resident in the local page cache before lookup. Each trial used the same private 64 MiB file, two tables, hashes, prepared callbacks, synthetic int16-to-F32 dequantizer and worker pool. Only separate versus joint submission changed inside the matched timer. Cold pairs alternate execution order; remote filesystem/server caches were not controlled.

| Tokens | Rows per table | Cold median separate → joint | Warm-memory median separate → joint |
|---|---:|---:|---:|
| 1 | 24 | 9.050 → 7.488 ms | 102.162 → 51.772 µs |
| 3 | 72 | 24.470 → 20.011 ms | 156.944 → 95.728 µs |
| 257 | 6,168 | 122.338 → 121.030 ms | 1,058.515 → 1,021.659 µs |

Every one- and three-token cold pair improved. The 257-token cold pairs were mixed: separate/joint ratios 1.045, 1.050 and **0.968**. This is a scheduling microbenchmark, not a full-model speedup or llama.cpp parity result. The warm benchmark reports all 12 samples for each schedule; each sample averages 200 submissions for one/three tokens or 10 for 257 tokens. The paired timer excludes preparation/allocation to isolate scheduling.

Normal and ASan/UBSan local tests passed. They cover three mixed-format tables, one/three/257 tokens, one/16 workers, bounded groups of one/two/three tables, image rows that never touch table storage, invalid later hashes before any reads, callbacks whose preparation stack has expired, and recovery after worker errors. The VM ran the same unit tests successfully. Local warm-memory medians were 84.047→45.185, 90.076→52.124 and 580.415→506.315 µs; these are separate macOS measurements.

The combined joint-scheduling/mmap-advice native CPU build and Engram CTest passed. The established stable fixture passed **41/41** independent PyTorch comparisons at both one and 16 Engram workers with flash attention disabled; all reported numerical metrics were identical between worker counts. [Combined commands, source/library/fixture hashes](combined-native.json) and the [one-worker](native-stable-threads1.json)/[16-worker](native-stable-threads16.json) reports retain that scope. An initial attempt on the older F32 fixture with default flash attention returned **35/41**, retained in [its report](native-oracle-threads1.json). The earlier backend stage also retained 35/41 on that fixture with flash disabled; no matched default-flash before/after comparison or passing claim is made for it.

Retained evidence:

* [VM provenance and exact commands](vm-provenance.json), [independent audit](audit.json), [audit source](audit.py).
* [Every VM filesystem trial](engram-joint-filesystem.jsonl), [every VM warm sample](engram-joint-warm.jsonl), [VM unit-test log](engram-joint-test.log).
* [Local manifest](report.json), [normal log](normal.log), [sanitizer log](asan.log), [native CPU syntax check](native-syntax.log), [local warm samples](warm-joint.jsonl). The native syntax check predates the separate mmap-advice integration.
* [Frozen benchmark](source/TensorSharp.GGML.Native/tests/dsv41_engram_io_bench.cpp), [unit test](source/TensorSharp.GGML.Native/tests/dsv41_engram_io_test.cpp), [prepared lookup](source/TensorSharp.GGML.Native/dsv41_engram.h), [unchanged worker pool](source/TensorSharp.GGML.Native/dsv41_engram_io.h).

Run `python3 audit.py` in this directory to verify source/log hashes, scenario completeness, selected-page residency and all reported medians. The Linux commands create and remove their own scratch file; they never evict a live model mapping. The original single-table `--warm-memory` and scratch-directory benchmark modes remain available.
