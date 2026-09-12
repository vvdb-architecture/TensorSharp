# Executed Linux mapping-advice control

All **24/24** trials returned exact expected rows. The [raw records](engram-advice-vm.jsonl) and [summary](engram-advice-summary.json) retain every result: two token counts, one/16 workers, three default/RANDOM pairs. All 24 trials had zero selected local pages resident immediately before lookup. Advice and thread order both alternate; the same row fingerprints and frozen headers are used throughout.

| Tokens | Workers | Median paired default/RANDOM time ratio |
|---|---:|---:|
| 1 | 1 | 0.969× |
| 1 | 16 | 1.862× |
| 3 | 1 | 1.092× |
| 3 | 16 | 1.701× |

A ratio above one means RANDOM was faster. The one-worker cases are mixed, including a median regression for one token; they remain visible and the production automatic policy leaves one-worker mappings at their default advice. Three pairs are a small diagnostic sample.

The benchmark uses only its owned, fsynced 64 MiB scratch file and fresh mappings, with 24/72 selected rows of 256 synthetic int16 elements. It does not evict model files. Local `mincore` residency and whole-process fault counts do not measure physical or network bytes; remote filesystem/server caches and other processes were not controlled. These are lookup-timer observations, not a full-model speedup.

[Preparation README and commands](README.md), [frozen source](engram_advice_bench.cpp), [preparation manifest](manifest.json), [13 summarizer guards](analyzer-checks.json), [Linux binary hash](engram-advice-bench.sha256), and [stderr](engram-advice-vm.stderr) are unchanged snapshots. The preparation README's statement that Linux had not yet run describes that earlier stage. The later raw report and summary record the completed execution. The final-stage [offline audit](../phase2-final/native-audit.json) independently recomputes the summary and validates the preparation source hashes.
