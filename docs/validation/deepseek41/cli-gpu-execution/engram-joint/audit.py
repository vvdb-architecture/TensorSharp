#!/usr/bin/env python3
"""Recompute retained Engram scheduling evidence; no VM or model access."""
import hashlib
import json
from pathlib import Path
import statistics

ROOT = Path(__file__).resolve().parent
manifest = json.loads((ROOT / "vm-provenance.json").read_text())
for path, expected in manifest["file_sha256"].items():
    assert hashlib.sha256((ROOT / path).read_bytes()).hexdigest() == expected, path
for path, expected in manifest["source_sha256"].items():
    assert hashlib.sha256((ROOT / "source" / path).read_bytes()).hexdigest() == expected, path

rows = [json.loads(line) for line in (ROOT / "engram-joint-filesystem.jsonl").read_text().splitlines()]
assert len(rows) == 24
assert all(r["exact_output"] and r["mode"] == "scratch_joint" and r["threads"] == 16
           and r["tables"] == 2 and r["bytes"] == 64 * 1024 * 1024 and r["head_dim"] == 256 for r in rows)
assert all(r["cold_pages_confirmed"] and r["initial_selected_resident_pages"] == 0 for r in rows if not r["warm"])
assert sum(not r["warm"] for r in rows) == 18
keys = {(r["tokens"], r["warm"], r["sample"], r["schedule"]) for r in rows}
assert len(keys) == len(rows)
assert keys == {(t, w, n, s) for t in (1, 3, 257) for w in (False, True)
                for n in range(1 if w else 3) for s in ("separate", "joint")}
for r in rows:
    assert r["selected_rows"] == r["tokens"] * 24 * 2
    assert r["schedule"] == ("joint" if (r["sample"] + r["order"]) % 2 else "separate")

summary = {"filesystem_exact_cases": len(rows), "cold_selected_pages_confirmed": 18, "cold_pairs": []}
for tokens in (1, 3, 257):
    values = {s: sorted((r for r in rows if r["tokens"] == tokens and not r["warm"] and r["schedule"] == s),
                       key=lambda r: r["sample"]) for s in ("separate", "joint")}
    summary["cold_pairs"].append({
        "tokens": tokens,
        "median_separate_ms": statistics.median(r["lookup_seconds"] for r in values["separate"]) * 1000,
        "median_joint_ms": statistics.median(r["lookup_seconds"] for r in values["joint"]) * 1000,
        "paired_separate_over_joint": [a["lookup_seconds"] / b["lookup_seconds"]
                                       for a, b in zip(values["separate"], values["joint"])],
    })
for file in ("warm-joint.jsonl", "engram-joint-warm.jsonl"):
    warm = [json.loads(line) for line in (ROOT / file).read_text().splitlines()]
    assert len(warm) == 6 and {(r["tokens"], r["schedule"]) for r in warm} == {
        (t, s) for t in (1, 3, 257) for s in ("separate", "joint")}
    for r in warm:
        assert r["exact_output"] and r["threads"] == 16 and r["tables"] == 2 and len(r["samples_us"]) == 12
        assert abs(statistics.median(r["samples_us"]) - r["median_us"]) <= 0.0000011
    summary[file] = [{k: r[k] for k in ("tokens", "schedule", "median_us")} for r in warm]
print(json.dumps(summary, indent=2))
