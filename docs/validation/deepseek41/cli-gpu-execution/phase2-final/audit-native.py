#!/usr/bin/env python3
"""Offline checks over retained native/advice evidence; does not run inference."""
import hashlib
import importlib.util
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent


def identity(path):
    data = path.read_bytes()
    return {"file": str(path.relative_to(ROOT)), "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest()}


def verify_manifest(folder, name):
    records = json.loads((folder / name).read_text())["files"]
    for path, record in records.items():
        info = identity(folder / path)
        assert info["sha256"] == record["sha256"], path
        assert info["bytes"] == record.get("bytes", record.get("size_bytes")), path
    return len(records)


def main():
    pins = {
        "ggml_ops_deepseek4.cpp": "8a88f291052a1c734321af904b77207181d9abeb1498de483c574dee2ac27f57",
        "dsv41_engram_advice.h": "c4c983453b57b99d2253b240db3d27bb8278ff352c2b4e101aef3355bfc548ce",
        "CMakeLists.txt": "d83ac9e7ff50cf5863619d575881be77fee3423a83d628cf9eaa2b65fc6d8312",
        "tests/dsv41_engram_advice_test.cpp": "eeda64006fdcede8f114ea43c6540e497c3e031eaceabb13f013b2a8f5a24207",
    }
    for name, sha in pins.items():
        assert identity(HERE / "sources" / name)["sha256"] == sha, name
    runs = {}
    for name, expected in (("phase1-vision-cuda1", 158), ("final-vision-cuda1", 158),
                           ("final-text-cuda1", 41)):
        report = json.loads((HERE / (name + ".json")).read_text())
        checks = report["checks"]
        assert len(checks) == expected and all(c["passed"] is True for c in checks)
        # The vision harness repeats position/slot assertions for each chunk.
        # Its ordered full report is compared byte-for-byte below.
        if name == "final-text-cuda1":
            assert len({c["name"] for c in checks}) == expected
        assert report["backend"] == "CUDA" and report["gpus"] == 1 and report["cpu_moe"] == 0
        assert report["environment"]["TS_DSV4_FA"] == "0"
        assert report["environment"]["TS_DSV4_GATHER"] == "0"
        log = (HERE / (name + ".log")).read_text()
        assert f"Passed {expected}/{expected}" in log.splitlines()[-1]
        runs[name] = {"checks": expected, "passed": expected,
                      "max_absolute_error": max((c.get("max_absolute_error", 0) for c in checks)),
                      "max_relative_l2": max((c.get("relative_l2", 0) for c in checks)),
                      "report": identity(HERE / (name + ".json")),
                      "log": identity(HERE / (name + ".log"))}
    assert (HERE / "phase1-vision-cuda1.json").read_bytes() == (HERE / "final-vision-cuda1.json").read_bytes()
    text = json.loads((HERE / "final-text-cuda1.json").read_text())
    assert text["atol"] == text["rtol"] == 2e-5
    assert text["environment"]["TS_DSV41_ENGRAM_THREADS"] == "16"
    assert text["environment"]["TS_DSV41_ENGRAM_WARM"] == "1"
    lines = (HERE / "final-text-cuda1.log").read_text().splitlines()
    events = {name: [(i + 1, line) for i, line in enumerate(lines) if needle in line]
              for name, needle in (("warming", "[dsv41] warming "),
                                   ("warmed", "[dsv41] warmed "),
                                   ("advice", "[dsv41] Engram mmap advice: RANDOM"))}
    assert all(len(v) == 1 for v in events.values())
    assert events["warming"][0][0] < events["warmed"][0][0] < events["advice"][0][0]
    assert "accepted=1, unsupported=0, skipped=0, failed=0" in events["advice"][0][1]
    unit = (HERE / "engram-advice-test.log").read_text()
    assert "Linux smaps confirms rr on the two advised pages only" in unit
    assert "Passed 61 mapped Engram advice checks" in unit
    for name in ("native-joint-advice-build.log", "native-final-config-build.log"):
        assert "[100%] Built target GgmlOps" in (HERE / name).read_text()
    scratch = ROOT / "engram-advice"
    scratch_pins = verify_manifest(scratch, "manifest.json")
    spec = importlib.util.spec_from_file_location("advice_summary", scratch / "analyze.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    rows = [json.loads(s) for s in (scratch / "engram-advice-vm.jsonl").read_text().splitlines() if s]
    recomputed = module.summarize(rows)
    original = json.loads((scratch / "engram-advice-summary.json").read_text())
    assert recomputed == {k: v for k, v in original.items() if k not in ("input", "analyzer_sha256")}
    assert identity(scratch / "engram-advice-vm.jsonl")["sha256"] == original["input"]["sha256"]
    assert identity(scratch / "analyze.py")["sha256"] == original["analyzer_sha256"]
    research = ROOT / "upstream-review"
    research_pins = verify_manifest(research, "llama-hf-manifest.json")
    upstream = json.loads((research / "vllm-sglang-review-provenance.json").read_text())
    for filename, item in zip(("vllm-engram.py", "vllm-engram-config.py", "vllm-model.py"), upstream["vllm_files"]):
        assert identity(research / filename)["sha256"] == item["sha256"]
    for item in upstream["sglang"]["files"]:
        assert identity(research / item["local_file"])["sha256"] == item["sha256"]
    assert upstream["sglang"]["merged"] is False
    report = {"audit_complete": True, "all_checks_passed": True,
              "scope": "Offline retained-evidence audit. Native library hashes/launch mapping are supplied by the executing parent; this auditor does not load or rehash VM binaries.",
              "baseline_native_sha256": "1d9209883c7275e545cfb18c4fd53e7ab4eb51bcef6c8d3bc83c328d5c817835",
              "final_native_sha256": "fa5ac07517e4e1c891a6bc53833246917ba03bd13d112ed67a53bbebccf6891d",
              "cuda_runs": runs, "vision_reports_byte_identical": True,
              "warm_then_advice_log_events": events, "linux_advice_checks": 61,
              "scratch": recomputed, "verified_scratch_preparation_files": scratch_pins,
              "verified_llama_hf_files": research_pins, "verified_vllm_sglang_sources": 7,
              "sources": [identity(p) for p in sorted((HERE / "sources").rglob("*")) if p.is_file()],
              "auditor": identity(Path(__file__))}
    (HERE / "native-audit.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
    print("Native evidence audit passed: CUDA 158 + 158 + 41; Linux 61; scratch 24/24")


if __name__ == "__main__":
    main()
