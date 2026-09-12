# Final combined Engram stage

This stage adds joint table scheduling and scoped Linux mapping advice to the completed [phase1 fanout change](../phase1/README.md). Its native library is **`fa5ac07517e4e1c891a6bc53833246917ba03bd13d112ed67a53bbebccf6891d`**. Phase1 library `1d920988…` and the original user library `c68df1f3…` remain separate controls; the older full-checkpoint performance reports were not rerun under this identity.

The [native audit](native-audit.json) verifies the retained reports, source pins, log completion, scratch results and upstream source manifests. It performs no inference. The executing parent supplied the VM library-to-run identities; the fixture reports themselves do not contain a library hash.

| Check | Result | Scope |
|---|---:|---|
| Phase1 CUDA image/text fixture | 158/158 | F32 companion, one GPU, ubatch 3, dense attention |
| Final CUDA image/text fixture | 158/158 | Entire ordered report byte-identical to phase1; maximum absolute error 5.186e-6 |
| Final CUDA text oracle | 41/41 | CUDA-index fixture, one GPU, FA/GATHER off, 16 Engram workers, warming enabled; maximum absolute error 7.391e-6 |
| Linux mapped-advice unit | 61 checks | Owned read-only mapping; kernel `rr` flag on exactly the two advised pages; unchanged file bytes; invalid/unmapped-range controls |
| Local advice unit | 47 checks twice | Normal and ASan/UBSan; Linux-specific behavior explicitly unsupported on macOS |

The image/text harness covers compact image masks and embedding injection, negative Engram history barriers, chunked inputs, interleaved slots, reset, invalid-input retry and encoder attachment lifetime. It is a tiny numerical/ABI regression, not a new full-checkpoint image quality result or an injected-compute-failure run. The text log records Engram warming at lines 31–32 and accepted RANDOM advice at line 33, with no unsupported, skipped or failed table. [Vision baseline](phase1-vision-cuda1.json), [vision final](final-vision-cuda1.json), [text final](final-text-cuda1.json), [Linux unit log](engram-advice-test.log).

Joint scheduling prepares all hashes and table metadata before any reads, interleaves table tasks in the existing worker pool, and uploads outputs only after all workers drain. Its separate [24-trial scheduling experiment and local recovery tests](../engram-joint/README.md) retain both improvements and the mixed long-batch result.

`TS_DSV41_ENGRAM_RANDOM` accepts only `0` or `1`; when unset, it enables the hint with more than one Engram worker. The [helper](sources/dsv41_engram_advice.h) checks mapping bounds and arithmetic, floors the start to a page boundary, and leaves the length through the table end. The kernel acts at page granularity, so boundary pages are included. The [core hook](sources/ggml_ops_deepseek4.cpp) applies it only to Engram tables backed by the model's registered mmaps, after optional warming. Unsupported platforms and OS errors remain nonfatal and are reported. It does not change the entire shard's advice, pin tables, guarantee residency, or change GPU arithmetic. The [owned-file advice experiment](../engram-advice/results.md) motivated the multi-worker default.

The [upstream review index](../upstream-review/README.md) separates implemented mechanisms from open proposals and unmeasured candidates. It identifies the llama.cpp lazy-mapping precedent and vLLM table preparation; the inspected SGLang path was an open, unmerged PR. No upstream inference or performance parity claim follows from source review.

Build evidence is preserved in [the combined build log](native-joint-advice-build.log) and [the final CMake configure/build log](native-final-config-build.log). The latter includes CUDA `86-real` and Vulkan; V4.1 selects CUDA. The parent rehashed the native library after reconfiguration and reported the same `fa5ac075…` identity. The frozen [CMake file](sources/CMakeLists.txt) includes `GgmlOpsDsv41EngramAdviceTest` / `deepseek41-engram-mapped-advice`.

The executed Linux standalone test command was:

```sh
g++ -std=c++17 -O2 TensorSharp.GGML.Native/tests/dsv41_engram_advice_test.cpp -o /workspace/deepseek41-work/cli-gpu-report/engram-advice-test
/workspace/deepseek41-work/cli-gpu-report/engram-advice-test
```

Run `python3 audit-native.py` in this directory to repeat the offline checks. Full-model CLI and existing-model controls will be archived separately when their closed logs are available; the checks above do not anticipate those results.
