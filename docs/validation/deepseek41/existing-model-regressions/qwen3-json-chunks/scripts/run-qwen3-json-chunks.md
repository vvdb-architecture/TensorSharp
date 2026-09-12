# Qwen3 prefill chunk diagnostic

Prepared locally only. Run after the root explicitly releases the Qwen3.5 performance window. This does not change production source, grammar, requests, sampling, frozen deployments, or validators.

```sh
/workspace/deepseek41-work/venv/bin/python /workspace/deepseek41-work/run-qwen3-json-chunks.py
```

The six fresh process jobs are baseline/final at chunk256, final/baseline at chunk76, and baseline/final at chunk64. Every job sends the same retained `json-c4-r0-i0` request twice, alone. The tag remains unchanged although actual concurrency is one. There are12 logical cases and no extra warmup request. The first request is cold; the second has warm graph/grammar state. Timings are explicitly unqualified.

Both the prefill CLI option and scheduler prefill/solo caps are set to256,76,64 respectively. The existing global batched-token budget remains256, with four slots, prefix caching disabled, F16 KV, GPU7, fixed greedy sampling, and no speculative decoding. `TS_CB_DEBUG=1` exists in both preserved HEAD and final sources. It logs each request's computed-token position before each prefill/decode step. Required observed sequences are P@0→D@90, P@0→P@76→D@90, or P@0→P@64→D@90; these establish90,76+14,64+26 prefill lengths in this sequential control. Missing, wrong, or non-solo traces make diagnostic coverage incomplete. These are scheduler traces, not native kernel-assignment traces; all existing native/server stdout is also retained.

The runner pins reviewed R2 runner10ab194b and complete R2 manifest7ba46725, verifies all six retained child hashes, the current harness, both frozen hosts' file hashes, and the Qwen3 weight identity before starting. It reuses the hosts directly without copies or mutation; each job verifies them before and after. Logs go to a fresh external directory. It refuses occupied port5011, resident GPU processes, an existing output directory, or a STOP file, and stops only its owned process using the reviewed shared cleanup helper.

The exact initial fixture hash is820cc397…, and the serialized retained request hash is e1c5ae800ba0b4c69abce54cb2dd6301f7a8a6329b7a9892d83b8514eefaacfc. The two repetitions do not alter the user tag or other fields. The original semantic checker is applied to every result. A wrong nested Mars object remains a failure. Exit0 means all12 requests and their chunk observations completed; inspect `all_cases_passed` separately. Semantic failures are the outcome being investigated, so they do not prevent later jobs.

This is a control for chunk geometry. It does not reproduce the earlier concurrent arrival order, primary-holder migration, or cross-request execution interleaving. A chunk-dependent result common to both builds would support inherited numerical sensitivity; a baseline/final mismatch at the same shape requires further investigation. Neither result alone proves a kernel or cache defect.

Local guard evidence: `qwen3-json-chunks-checks.json` binds runner/test source hashes and23 simulated checks. No native inference or VM timing was executed by those tests.
