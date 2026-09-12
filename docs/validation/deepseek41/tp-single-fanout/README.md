# Single-fanout TP candidate

The candidate dispatches one worker job per rank that submits its local GPU graph and then drains its own queue. Other rank workers submit concurrently. The worker pool waits for every job before host reduction or error propagation. Each job preserves its first exception while still attempting to drain queued copies.

The preceding two-fanout path remains available only through a test-build C++ method. The standalone test can compare both paths in the same binary, on the same graph, weights, inputs and backend queues. It alternates execution order, warms both modes, and requires bitwise-identical finite outputs before reporting timings.

Local verification passed:

- 96 standard full-vs-sharded CPU numerical comparisons.
- 24 CPU comparisons with checkpoint projection dimensions and mixed Q2_K gate/up plus Q3_K down weights.
- 63 TP failure checks, including a submission error followed by a drain error that must preserve the first error.
- 158 text/image slot failure and oracle checks.
- 18 bitwise old/new comparisons across two, four and eight CPU ranks with token batches of one and 16.

The paired fixture uses hidden dimensions 5,120 and 2,304, eight experts and top-six routing. It is an isolated synthetic MoE fixture. The three-pair local timings are diagnostic; they do not establish a GPU or full-model performance gain.

Remote candidate `8e66aaf4e724d4da9442f14ff01a901db038b89a9e1daa0df9d701ceeb5dc242` also passed 63/63 CUDA2 TP failure checks, 230/230 text/image TP slot checks, and 13/13 native CTests. These ran during model startup and establish correctness, without qualified timing claims.

The test observer is absent from a full native library relink without test hooks and present in the test-enabled build; the real Forward export remains in both. [test-export-guard.json](test-export-guard.json) records that check. The production iOS export manifest remains unchanged.

The exclusive GPU comparison ran after model startup with the full-model host idle and other validation jobs stopped. All 180 alternating old/new output pairs were bitwise identical. Thirty pairs followed ten warmups per path for each shape. The enclosing CPU backend uses one thread. Median timings were:

| GPU ranks | Tokens | Two fanouts (ms) | One fanout (ms) | Ratio |
|---:|---:|---:|---:|---:|
| 2 | 1 | 0.154806 | 0.145893 | 1.061× |
| 2 | 16 | 0.423523 | 0.413350 | 1.025× |
| 4 | 1 | 0.232696 | 0.196686 | 1.183× |
| 4 | 16 | 0.733850 | 0.698106 | 1.051× |
| 8 | 1 | 0.282318 | 0.235546 | 1.199× |
| 8 | 16 | 1.291988 | 1.236900 | 1.045× |

These isolated MoE improvements support retaining the candidate for final full-model validation. They do not establish an end-to-end inference improvement. [paired-summary.json](paired-summary.json) preserves the returned summaries; [native-single-fanout-paired.json](native-single-fanout-paired.json) and the accompanying logs retain all per-pair timings plus start/end GPU clocks and process snapshots. All eight A40s reported 1,740 MHz SM and 7,251 MHz memory clocks at the start, and the resident model was the sole listed GPU process. Process `%CPU` snapshots are lifetime averages, not interval measurements; the exclusive interval was coordinated with the agents and full-model host.

Commands:

```sh
GgmlOpsDsv41TpTest --cuda 2 --fanout-pairs 30
GgmlOpsDsv41TpTest --cuda 4 --fanout-pairs 30
GgmlOpsDsv41TpTest --cuda 8 --fanout-pairs 30
```

The preceding remote `b26` and `3b885898` binaries remain archived and unchanged. [manifest.json](manifest.json) retains local source/binary hashes and results; complete logs accompany it.
