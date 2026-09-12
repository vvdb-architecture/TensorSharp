# Initial Qwen3.5 native-swap control

Both libraries produced identical outputs and token counts for all three requests. **Strict correctness is 2/3 in each run**: the request `Reply exactly OK.` produced `OK.` in both. The other outputs were `4` and `{"ok":true}`. [Exact input](cli-gpu-qwen-control-input.txt), [baseline log](qwen-before-final.log), [final log](qwen-after-final.log), [audit](audit.json).

| Request | Output tokens | Phase1 prefill / decode | Final prefill / decode |
|---|---:|---:|---:|
| Exact OK | 2 | 1,056 / 56 ms | 1,065 / 1,060 ms |
| Arithmetic | 1 | 1,102 / 522 ms | 178 / 619 ms |
| Prompted JSON | 5 | 1,014 / 1,009 ms | 1,275 / 1,269 ms |

These are unqualified initial CLI observations with very short outputs. The large differences remain unresolved by this control; identical answers do not establish performance parity. Both runs reset between requests, use temperature zero/seed 42/thinking off, and report EOS with matching 16/26/23 prompt tokens. JSON was requested in the prompt, without a constrained grammar mode. The parent executed phase1 `1d920988…` before final `fa5ac075…`; the raw CLI logs do not themselves bind those binary hashes. Further repetitions, if performed, remain separate evidence.

The [timing source excerpt](timing-source.json) shows that CLI `decodeMs` includes synchronous output writes before subsequent forwards. CLI `ttftMs` starts after prompt processing and is not end-to-end TTFT. These timer boundaries limit interpretation; they do not prove that terminal writes caused the observed stalls.
