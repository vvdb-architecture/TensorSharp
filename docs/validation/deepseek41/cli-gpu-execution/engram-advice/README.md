# Owned-scratch Engram mapping-advice experiment

Prepared locally; the Linux filesystem path has not been run during preparation. The experiment derives from frozen benchmark `772fef5b26b1cc8ef164701788b944d7e7d5a811bd99f2a0610ac1507fbc3bdb` and uses separately copied, hash-pinned headers. It does not modify model mappings or production source.

The Linux path writes and fsyncs its own 64 MiB temporary file, then compares default mapping advice against `POSIX_MADV_RANDOM` for identical 24- and 72-row lookups (one and three tokens; row width 256 synthetic int16 elements). It runs three paired samples at each of one and sixteen workers, with both advice and thread-block orders alternating: 24 trials. Each trial advises away only this private file, creates a fresh mapping, checks initial residency, performs the lookup, checks final residency, verifies exact output bytes, and unmaps. File/mapping cleanup runs on failures. Advice acceptance is not proof that a filesystem honors the hint.

`cold_pages_confirmed` refers only to the selected local file-cache pages at the pre-read snapshot. Filesystem/server caches are not evicted. The final resident-page count can show changed local readahead behavior but is not a physical or network byte count. `getrusage` fault deltas include the whole process and all its threads; they are not exclusively attributed to the selected mapping. Timing excludes setup, mincore, fault-counter reads, and parity checks. These are scratch-file diagnostics, not a qualified full-model speedup.

## VM commands after copying this directory's source/header files

```sh
g++ -std=c++17 -O3 -pthread /workspace/deepseek41-work/engram-advice/engram_advice_bench.cpp -o /workspace/deepseek41-work/engram-advice/bench
sha256sum /workspace/deepseek41-work/engram-advice/bench
/workspace/deepseek41-work/engram-advice/bench /workspace/deepseek41-work > /workspace/deepseek41-work/engram-advice/vm.jsonl 2> /workspace/deepseek41-work/engram-advice/vm.stderr
python3 /workspace/deepseek41-work/engram-advice/analyze.py /workspace/deepseek41-work/engram-advice/vm.jsonl --report /workspace/deepseek41-work/engram-advice/vm-summary.json
```

Preserve the compiler command/result, binary hash, benchmark exit status, stdout and stderr. The summarizer requires all 24 exact shape/order/output records and rejects missing or substituted rows, malformed timing/residency, or a false cold-page claim. Valid uncold trials remain in the raw report and their pairs are excluded from cold timing ratios.

Preparation checks: portable macOS compilation and unchanged cached-memory mode passed four exact-output controls. Thirteen local simulated-record guards cover summarizer coverage, both orders, geometry, output, residency, timing, and unqualified cold-page failures. They do not substitute for compiling or running the Linux branch.

The first draft coupled advice order to thread order, so the fixed-thread pairs did not alternate. Independent review caught this before execution. The final formula alternates advice from the sample index independently, and an explicit regression rejects the old order.
