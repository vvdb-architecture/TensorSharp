# Premature dependent tool call: local diagnosis

The final routed-TP profile's `agentic-c4-r0-i2` response called `read_invoice(INV-472)` and `calculate_total(0, 0)` together before receiving the invoice data. The [quality report](../full-checkpoint/tp8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final-quality.json) retains this failure. This diagnosis found no actionable prompt-rendering, grammar or parser defect that forces the premature call or zero arguments. It does not isolate the model's numerical path or explain every difference between placement profiles.

The exact initial fixture hash reconstructs to `d6cc93c41de1c4b88e8b9de8ec92a616155cfbe75f86bdfd39901398f1a42951`. The request explicitly says to read the invoice and pass its **returned** unit price and quantity to the second tool. Its first turn has no earlier assistant/tool messages, no skills and no media. Both `tool_choice` and `parallel_tool_calls` are omitted, selecting the existing `auto`/parallel-enabled defaults.

The locally rendered prompt is byte-identical to the supplied vLLM V4.1 reference encoder for this exact fixture. It has 394 Q2 vocabulary tokens, matching the native log, and SHA-256 `c121231a856ff4ce9b1bda8e3d4b764e5b25f5c00adb8429718d3929e1f5d0d0`. The canonical reference tool header also demonstrates multiple invokes; TensorSharp did not add an instruction to call every declared tool. [Request and rendered reference prompt](request-and-native-output.json) preserve these bytes.

Native server-log line 1997 already contains two complete DSML invokes with literal `0` parameter text and reports 105 generated tokens, EOS completion and zero reused prompt tokens. [The retained line](native-line.log) and the parsed HTTP result agree. The parser did not invent a second call, fill missing arguments with defaults or combine fragments from separate responses.

An independent local probe used the actual Q2 vocabulary and the frozen managed source, without loading model weights:

| Grammar/parser replay | Result |
|---|---|
| Original native text, two calls and literal zeros | All 105 re-encoded tokens admitted; complete, non-dead grammar; both parsed calls preserve the original arguments |
| Same response stopped after `read_invoice` | All 60 tokens admitted; one complete parsed call; EOS logit remains available with its original score |
| Numeric fields spelled `13.75` and `5` | All 107 tokens admitted; parser preserves those values |
| Separate request state | Advancing the other constraints leaves its initial state and mask unchanged |

The numeric alternative is a grammar diagnostic, **not a repaired response or a successful tool workflow**. On the actual first turn, those returned values are still unknown. The recipe allows zero or more additional invokes after the first when parallel calls are enabled; it does not require a second invoke. The supplied schemas allow numeric zero and contain no machine-readable dependency between tools. That dependency is conveyed by the user instruction, which generation failed to follow.

The [replay report](replay.json) records checks, source/assembly hashes, the exact command and reference provenance. EOS is assessed through the sampler's `ApplyMask` behavior, which restores permitted EOS scores; EOS is intentionally absent from the raw vocabulary-trie mask. The local assembly and VM assembly have separate recorded hashes. This is not a same-binary or numerical-path isolated comparison.

No production change follows from this diagnosis. Keep the failed parallel-enabled case as a failure. Explicit client choices such as serial calls or stage-specific tool availability are separate workflow policies; they must not silently replace the measured request or fabricate tool arguments. If further attribution is needed, a separately labeled reproduction with the identical request under solo/concurrent and controlled numerical settings could measure sensitivity. None was run for this local diagnosis.

To reproduce, copy [probe.cs.txt](probe.cs.txt) to `Program.cs` and [probe.csproj.txt](probe.csproj.txt) to `Probe.csproj` in an isolated directory. Use the saved request/reference JSON as `case.json`, the checkpoint's first GGUF shard or the recorded saved header JSON, and an `EngineBin` pointing at the matching built managed DLLs. The probe reads GGUF metadata without loading tensor payloads. The command in the report supplies the original absolute paths; adapt those paths for another workspace.
