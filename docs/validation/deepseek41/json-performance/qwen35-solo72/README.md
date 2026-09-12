# Qwen3.5 solo control: 72/72 pass, earlier slowdown not reproduced

All **72 measured requests** pass, with identical original HTTP requests, full outputs, finish reasons and token counts. All four 512-token decode warmups and 20 JSON warmups also complete and match their retained reference requests and outputs. Both whole-18 comparisons qualify. **Final managed latency is lower in this control; no production change was made, and the earlier qualified slower runs remain unexplained.**

The [independent audit](audit.json) verifies four fresh jobs in baseline/final/final/baseline order, all requests and responses, source/host manifests, actual native mappings, telemetry and sequential server request IDs. Each job repeats the exact three original R2 solo HTTP payloads six times after its six warmups. Every measured response has 92 prompt tokens, 16 completion tokens and zero KV reuse. All original reports and logs are copied byte for byte; the [72-row table](observations.csv) retains every client and pipeline observation.

## Complete comparisons

Pair 1 compares baseline job 0 with final job 1; pair 2 compares baseline job 3 with final job 2. Every comparison includes all 18 requests per build. Ratios above 1 indicate lower final latency.

| Pair | Metric | Baseline median | Final median | Baseline/final ratio |
|---|---|---:|---:|---:|
| 1 | First token (ms) | 67.5404 | 64.3621 | 1.0494 |
| 1 | Request wall (ms) | 124.2156 | 118.6700 | 1.0467 |
| 1 | Whole-wave wall (ms) | 125.6426 | 119.7285 | 1.0494 |
| 2 | First token (ms) | 69.8916 | 65.3819 | 1.0690 |
| 2 | Request wall (ms) | 125.1474 | 121.4875 | 1.0301 |
| 2 | Whole-wave wall (ms) | 126.1771 | 122.6603 | 1.0287 |

These are 16-token JSON responses. SSE decode estimates remain short-response observations, not sustained 512-token throughput; warmup timings are excluded.

The first-three/later-15 groups were predeclared **descriptive subsets**, never replacement comparisons:

| Pair | Group | First-token median, baseline → final (ms) | Request-wall median, baseline → final (ms) |
|---|---|---:|---:|
| 1 | First 3 | 67.6105 → 68.7794 | 126.5321 → 127.1479 |
| 1 | Later 15 | 67.4975 → 62.3712 | 122.9620 → 116.4773 |
| 2 | First 3 | 87.7110 → 65.3509 | 142.4011 → 125.7604 |
| 2 | Later 15 | 64.2762 → 65.4129 | 119.8335 → 120.7952 |

Final is slightly slower in pair 1's first-three and pair 2's later-15 subsets. All outliers remain in the whole-18 comparisons. These subsets neither establish a warmup mechanism nor justify dropping early requests.

## Interpretation and qualification

The [standard-log correlation](pipeline-correlation/README.md) independently matches all 72 unique request IDs in structured and stdout logs. Pipeline first-token medians are 59.5 → 58 ms and 64 → 58 ms. This timer starts after rendering/tokenization and submission; subsequent engine, prefill and sampling costs remain combined. No native phase or EventPipe instrumentation was enabled.

The [original R2](../completed-r2/README.md) and [alternating control](../qwen35-alternating/README.md) used each build's native library and three solo requests per job within a mixed c1/c4 plan. Their slower final observations remain valid historical evidence. This larger solo-only control holds final native fixed and removes inherited diagnostic overrides; it does not isolate every earlier interaction. The [crossed diagnostic](../qwen35-cross-phase/README.md), [EventPipe evidence](../qwen35-eventpipe/README.md), [inherited reset-source review](reset-source-review/README.md) and [source-hashed synthesis](latency-synthesis.json) are separate evidence. Sampled reset residence is not exclusive CPU duration or proof of causality. No source-level fix is claimed.

Each job has three in-window telemetry samples, with only its owned GPU client and sampled SM 1,740 MHz / memory 7,251 MHz; temperatures span 41–46 °C. No non-owned CPU process exceeded the declared 10% of one-core policy. One `sshd` tick increase across 1.276 seconds is retained. Sampling cannot exclude every transient clock state or sub-second contention. All four server exits were observed; root separately confirmed no remaining GPU client.

## Provenance

Both immutable hosts load native `6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014`, confirmed by actual process mappings before warmups. Baseline managed files come from HEAD `04a5faa9e641fc0b06de661be1191afbc6edaf50`; final managed files are stage 3651. Complete binary/file hashes and commands remain in [run.json](run.json). The unchanged profile uses GPU 7, context 8,192, F16 cache, four slots, prefill 256, four CPU/OMP threads, no prefix reuse or speculation, temperature 0 and seed 42. Ordinary pinned settings replace inherited runtime/model diagnostic overrides. No frozen host file changed.

The [runner](../../scripts/run-qwen35-solo72.py) SHA is `344382bbf8b4eb1170886c3cafbad5c0774544e18ade9a0485a06e85950432f4`. Its [43 simulated guards](../../scripts/qwen35-solo72-checks.json) are distinct from inference results. The [VM-specific plan](../../scripts/run-qwen35-solo72.md) records the original command and prerequisites. The run finished at 15:07:58 UTC on 2026-09-11; root manifest SHA is `0256a32cb1594b10c645990f3f0b38af13907cdba27f05583c4f135db717fa0d`.

The [offline auditor](../../scripts/audit-qwen35-solo72.py) performs no inference or VM reads. With the retained local raw layout and pinned prerequisite snapshots:

```sh
/tmp/tensorsharp-validation-venv/bin/python \
  /tmp/deepseek41-reference/audit-qwen35-solo72.py \
  --runner-sha 344382bbf8b4eb1170886c3cafbad5c0774544e18ade9a0485a06e85950432f4
```

The [artifact manifest](artifact-manifest.json) pins every curated byte; the shared [source index](../../scripts/sources.json) pins runner, guard and analyzer snapshots. Offline checks validate retained producer binary evidence, not a fresh VM/model rehash. Raw evidence remains at `/tmp/deepseek41-reference/qwen35-solo72-final-native-r1/` and `/workspace/deepseek41-work/existing-regressions/qwen35-solo72-final-native-r1/`.
