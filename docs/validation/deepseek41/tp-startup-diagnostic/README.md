The final TP8 host loaded successfully. Its preparation wrapper failed because the optional expert-source warmer was invoked without its required `--report` argument. The retained [argument error](source-warmer-argument-error.txt) is plain text, despite its original `.json` filename. The [preparation report](preparation-incomplete.json) remains incomplete with `ready: false`; the wrapper wrote that field before startup and did not finalize the report after its exception.

The exact [executed helper](../scripts/prepare-final-profile.py) has SHA-256 `81158b05578443dbc21946b46090e25764978cbfeb20d559ac3bd3dc75f51bde`. Lines 58–60 omit `--report`; line 78 raises only after the health endpoint is ready and the warmer exits unsuccessfully. Root observed wrapper exit 1. Its raw outer traceback was not found in the retained local files, so [resolution.json](resolution.json) labels that exit code as a conversation observation rather than a recovered raw transcript. The warmer's numeric exit code was not persisted.

The [server startup excerpt](server-startup-excerpt.log) independently records 186.3 GiB of weights loaded across eight GPUs in **559.5 seconds**, followed by **60.08 GiB of native Engram warming in 151.56 seconds** with 16 I/O threads. The managed load took 714,236.3 ms; `/health` and `/v1/models` then returned HTTP 200. Native Engram warming is separate from the optional source-warmer process that failed argument validation. The library was final native `6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014`.

Earlier [read-only process samples](samples.json) showed progress: across 38.27 seconds, RSS increased by 3,836,160 pages and major faults by 96,181. Established TP rank workers waited on futexes while I/O workers waited in FUSE/page-fault paths. The unchanged `rchar` counter does not imply zero I/O because Engram accesses memory-mapped pages. These observations used no ptrace, profiling, checkpoint reads, process changes or inference requests.

Root retained the failed preparation evidence and continued with the healthy host. The later [helper correction](../scripts/prepare-final-profile-warming-report.py), SHA-256 `ae1bd5bdea38790fc151ee2838a3c37a9c22d82f5505af29c5e4c99044056ee6`, supplies `--report` and separates stdout/stderr into a log; the [exact helper diff](source-warmer-report-fix.patch) is preserved. That correction was not rerun for this TP startup and does not turn its failed preparation into a pass.

Eight image-input and four Responses-audio rejection checks passed before the inference suite. The [completed suite wrapper](completed-profile-runs.json) records all six phases from **12:12:57 to 12:30:03 UTC on September 11, 2026**, with **129 of 130 scenario cases passing**:

| Cases | Passed / total |
| --- | ---: |
| Default quality | 29 / 30 |
| Steady decode | 15 / 15 |
| Long context | 2 / 2 |
| Tool policies | 30 / 30 |
| Thinking | 4 / 4 |
| Blocking responses | 4 / 4 |
| Image/video scenarios | 25 / 25 |
| Chinese and Unicode JSON | 10 / 10 |
| Serial tool workflows | 10 / 10 |

The [remaining default-policy failure](retained-agentic-failure.json) emitted `read_invoice` and a dependent `calculate_total` together before receiving invoice data, with zero placeholder arguments. The existing validator rejected it. Separate serial-tool successes do not replace that failed case. The 12 HTTP rejection checks are additional and are not counted in the 130 scenarios.

[Resolution evidence](resolution.json) records the original paths, hashes, exact case counts and evidence limits; [manifest.json](manifest.json) checksums the retained local artifacts. Startup was diagnostic and excluded from inference timing. Unmatched cache and warming conditions prevent a startup performance-regression claim, and this successful recovery does not establish strict numerical or llama.cpp parity.
