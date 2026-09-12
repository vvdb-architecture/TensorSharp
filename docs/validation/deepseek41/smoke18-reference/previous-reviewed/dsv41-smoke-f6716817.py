#!/usr/bin/env python3
"""Run a reproducible native GGUF smoke test from exact input token IDs.

This bypasses chat rendering to isolate inference. Use the HTTP validation
suite for tools, structured output, and concurrent request behavior.
Requires numpy; decoded-text validation additionally requires tokenizers.
"""
import argparse
import ctypes as C
import hashlib
import json
import os
from pathlib import Path
import time

import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--tokens", type=Path, required=True, help="JSON array of exact prompt token IDs")
    parser.add_argument("--output", type=Path, required=True, help="Report JSON; first logits are saved beside it")
    parser.add_argument("--tokenizer", type=Path, help="Official tokenizer.json for decoded-text validation")
    parser.add_argument("--expected-text")
    parser.add_argument("--backend", default="CUDA", choices=("CUDA", "CPU"))
    parser.add_argument("--gpus", type=int, default=8, help="Number of selected GPUs")
    parser.add_argument("--context", type=int, default=4096)
    parser.add_argument("--ubatch", type=int, default=128)
    parser.add_argument("--threads", type=int, default=24)
    parser.add_argument("--cpu-moe", type=int, default=0)
    parser.add_argument("--steps", type=int, default=40)
    parser.add_argument("--eos", type=int, default=1)
    parser.add_argument("--trace-dir", type=Path, help="Save first-forward intermediate tensors; excludes it from timing comparisons")
    args = parser.parse_args()
    if args.expected_text is not None and args.tokenizer is None:
        parser.error("--expected-text requires --tokenizer")
    if min(args.steps, args.context, args.ubatch, args.threads, args.gpus) < 1:
        parser.error("steps/context/ubatch/threads/gpus must be positive")
    prompt = json.loads(args.tokens.read_text())
    if not isinstance(prompt, list) or not prompt or any(type(t) is not int or t < 0 for t in prompt):
        parser.error("tokens must be a nonempty array of nonnegative integers")
    tokenizer = None
    if args.tokenizer:
        from tokenizers import Tokenizer
        tokenizer = Tokenizer.from_file(str(args.tokenizer))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    native_hash = hashlib.sha256()
    with args.library.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            native_hash.update(block)
    environment = {name: os.environ.get(name) for name in (
        "CUDA_VISIBLE_DEVICES", "TS_DSV41_TP", "TS_DSV4_FA", "TS_DSV4_GATHER",
        "TS_DSV4_FUSED", "TS_CPU_MOE_THREADS", "NVIDIA_TF32_OVERRIDE",
        "TS_DSV41_SPARSE_FA", "TS_DSV41_COMPACT_RAW_GATHER",
        "TS_DSV41_ENGRAM_THREADS", "TS_DSV41_ENGRAM_WARM")}
    tp_ranks = int(environment["TS_DSV41_TP"] or "0")
    report = {"model": str(args.model.resolve()), "library": str(args.library.resolve()),
              "library_sha256": native_hash.hexdigest(),
              "backend": args.backend, "placement": "routed_moe_tensor_parallel" if tp_ranks else "layer",
              "gpus": args.gpus, "environment": environment,
              "context": args.context, "ubatch": args.ubatch, "threads": args.threads,
              "cpu_moe_layers": args.cpu_moe, "prompt_tokens": prompt, "steps": [],
              "generated_tokens": [], "first_forward_traced": args.trace_dir is not None}
    def save():
        args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    lib = C.CDLL(str(args.library.resolve()))
    lib.TSGgml_Dsv4LoadModel.argtypes = [C.c_char_p] + [C.c_int] * 5 + [C.c_char_p]
    lib.TSGgml_Dsv4LoadModel.restype = C.c_void_p
    lib.TSGgml_Dsv4Forward.argtypes = [C.c_void_p, C.c_void_p, C.c_int, C.c_void_p]
    lib.TSGgml_Dsv4Forward.restype = C.c_int
    lib.TSGgml_Dsv4VocabSize.argtypes = [C.c_void_p]
    lib.TSGgml_Dsv4VocabSize.restype = C.c_int
    lib.TSGgml_Dsv4Free.argtypes = [C.c_void_p]
    lib.TSGgml_Dsv4Free.restype = None
    started = time.monotonic()
    handle = lib.TSGgml_Dsv4LoadModel(str(args.model.resolve()).encode(), args.gpus,
                                     args.context, args.ubatch, args.threads, args.cpu_moe,
                                     args.backend.encode())
    report["load_seconds"] = time.monotonic() - started
    if not handle:
        report["error"] = "Native load failed; see stderr"
        save()
        return 1
    print(f"Loaded in {report['load_seconds']:.3f}s", flush=True)
    trace_previous = os.environ.get("TS_DSV41_TRACE_DIR")
    if args.trace_dir:
        os.environ["TS_DSV41_TRACE_DIR"] = str(args.trace_dir.resolve())
    try:
        logits = np.empty(lib.TSGgml_Dsv4VocabSize(handle), dtype=np.float32)
        if max(prompt) >= len(logits):
            raise ValueError("Prompt token exceeds model vocabulary")
        tokens = np.asarray(prompt, dtype=np.int32)
        for step in range(args.steps):
            started = time.monotonic()
            status = lib.TSGgml_Dsv4Forward(handle, tokens.ctypes.data, len(tokens), logits.ctypes.data)
            elapsed = time.monotonic() - started
            if status or not np.isfinite(logits).all():
                raise RuntimeError(f"Forward failed or produced nonfinite logits (status={status})")
            if step == 0:
                np.save(args.output.with_suffix(".first-logits.npy"), logits)
                if args.trace_dir:
                    os.environ.pop("TS_DSV41_TRACE_DIR", None)
            token = int(logits.argmax())
            row = {"step": step, "input_tokens": len(tokens), "token": token,
                   "seconds": elapsed, "max_logit": float(logits[token])}
            report["steps"].append(row)
            report["generated_tokens"].append(token)
            print(json.dumps(row), flush=True)
            save()
            if token == args.eos:
                break
            tokens = np.asarray([token], dtype=np.int32)
        report["ended_with_eos"] = report["generated_tokens"][-1] == args.eos
        if tokenizer:
            report["text"] = tokenizer.decode(report["generated_tokens"], skip_special_tokens=True)
        report["passed"] = args.expected_text is None or report["text"].strip() == args.expected_text
    except Exception as error:
        report.update(passed=False, error=str(error))
    finally:
        lib.TSGgml_Dsv4Free(handle)
        if trace_previous is None:
            os.environ.pop("TS_DSV41_TRACE_DIR", None)
        else:
            os.environ["TS_DSV41_TRACE_DIR"] = trace_previous
        save()
    return int(not report["passed"])


if __name__ == "__main__":
    raise SystemExit(main())
