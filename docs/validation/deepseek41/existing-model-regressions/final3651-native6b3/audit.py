#!/usr/bin/env python3
"""Offline audit/curation of the final 75 matched + 15 Unicode quality cases.

No network, inference, deployment, or output repair. Python standard library only.
Raw reports are required to verify their original SHA256 and recompute comparisons.
"""
import argparse
import ast
import copy
import hashlib
import json
from pathlib import Path
import time
from types import SimpleNamespace

MODELS = ("qwen3", "qwen35", "gemma4")
SUITES = {"quality": ("short", "decode", "json", "multi_turn", "tool_round_trip"),
          "unicode": ("json_unicode",)}
REFERENCES = {"head-baseline": "quality-baseline", "earlier-after": "quality"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True,
                                    separators=(",", ":")).encode()).hexdigest()


def source(path):
    data = path.read_bytes()
    return {"path": str(path), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}


def read_verified(path, expected):
    actual = source(path)
    require(all(actual[k] == expected[k] for k in ("sha256", "bytes")),
            "Raw source identity mismatch: " + str(path))
    return json.loads(path.read_text())


def key(case):
    return (case["scenario"], case["tag"], case["concurrency"], case.get("repeat", 0))


def coverage(report, scenarios):
    expected = {(name, f"{name}-c{n}-r0-i{i}", n, 0)
                for name in scenarios for n in (1, 4) for i in range(n)}
    actual = [key(case) for case in report["cases"]]
    require(len(actual) == len(set(actual)) and set(actual) == expected,
            "Missing, repeated, or unexpected planned case")


def generated(case):
    # Stable output identity omits generated tool-call IDs, as in the runner.
    return [{"content": t["metrics"].get("assistant_message", {}).get("content"),
             "reasoning_content": t["metrics"].get("assistant_message", {}).get("reasoning_content"),
             "finish_reason": t["metrics"].get("finish_reason"),
             "tool_functions": [c.get("function") for c in
                                t["metrics"].get("assistant_message", {}).get("tool_calls", [])]}
            for t in case["turns"]]


def load_replay_validator(path):
    # Load only the recorded pure request/check functions. The HTTP entry point is
    # replaced below; executing/importing the runner or network engine is avoided.
    tree = ast.parse(path.read_text())
    functions = {"digest", "exact_json", "function", "case_spec", "assistant_content",
                 "check_answer", "execute_tool", "run_case"}
    assignments = {"SAMPLING", "WEATHER_TOOL", "INVOICE_TOOL", "TOTAL_TOOL"}
    selected = [node for node in tree.body
                if (isinstance(node, ast.FunctionDef) and node.name in functions)
                or (isinstance(node, ast.Assign) and any(isinstance(target, ast.Name)
                    and target.id in assignments for target in node.targets))]
    state = {"json": json, "hashlib": hashlib, "time": time}
    exec(compile(ast.Module(body=selected, type_ignores=[]), str(path), "exec"), state)
    return state


def replay_case(case, state):
    observed = []
    failures = []

    def frozen_response(url, model, **request):
        request.pop("timeout_s")
        index = len(observed)
        if index >= len(case["turns"]):
            failures.append("Replay requested an unrecorded turn")
            raise ValueError(failures[-1])
        turn = case["turns"][index]
        observed.append(request)
        if request != turn["request"]:
            failures.append("Replay request/history differed at turn " + str(index))
            raise ValueError(failures[-1])
        return copy.deepcopy(turn["metrics"])

    state["engines"] = SimpleNamespace(run_openai_chat=frozen_response,
                                      thinking_body=lambda engine, enabled: {"think": enabled})
    result = state["run_case"]("offline", "offline", "tensorsharp", case["scenario"], case["tag"])
    require(not failures, str(failures))
    require(len(observed) == len(case["turns"]), "Replay did not consume every recorded turn")
    for field in ("status", "input_sha256", "detail", "validated_content"):
        require(result.get(field) == case.get(field),
                f"Replay {field} mismatch for {case['tag']}")
    return len(observed)


def compare(reference, current, recorded):
    coverage(reference, SUITES["quality"])
    a = {key(c): c for c in reference["cases"]}
    b = {key(c): c for c in current["cases"]}
    for field in ("weights_id", "profile", "thinking", "sampling", "stream"):
        require(reference.get(field) == current.get(field), "Comparison field differs: " + field)
    rows = []
    for k in sorted(a):
        require(a[k]["input_sha256"] == b[k]["input_sha256"], "Initial request hash mismatch")
        rows.append({"key": list(k), "input_sha256": b[k]["input_sha256"],
                     "reference_status": a[k]["status"], "current_status": b[k]["status"],
                     "reference_generated_sha256": digest(generated(a[k])),
                     "current_generated_sha256": digest(generated(b[k]))})
    introduced = [r["key"] for r in rows if r["reference_status"] == "ok" and r["current_status"] != "ok"]
    changed = [r["key"] for r in rows if r["reference_generated_sha256"] != r["current_generated_sha256"]]
    counts = {"cases": len(rows), "baseline_pass": sum(c["status"] == "ok" for c in a.values()),
              "current_pass": sum(c["status"] == "ok" for c in b.values()),
              "matching_request_hashes": len(rows), "matching_generated_outputs": len(rows) - len(changed)}
    require(recorded["comparable"] and not recorded["issues"], "Runner comparison is not comparable")
    for field, value in counts.items():
        require(recorded[field] == value, "Comparison count mismatch: " + field)
    require(sorted(recorded["changed_output_keys"]) == sorted(changed), "Changed output key mismatch")
    require(sorted(c["key"] for c in recorded["introduced_failed_cases"]) == sorted(introduced),
            "Introduced failure key mismatch")
    return {**counts, "introduced_failed_cases": introduced, "changed_output_keys": changed,
            "reference_source": recorded["reference_source"],
            "reference_binary_sha256": reference.get("binary_sha256"), "cases_detail": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("current", type=Path, help="Raw final-existing-regressions directory")
    parser.add_argument("historical", type=Path, help="Directory with quality-baseline/ and quality/")
    parser.add_argument("--output", type=Path, default=Path(__file__).parent)
    args = parser.parse_args()
    here = Path(__file__).parent
    validator_path = here / "validate_inference.py"
    runner_path = here / "run-final-existing-models.py"
    run = json.loads((args.current / "run.json").read_text())
    require(run["label"] == "final3651-native6b3", "Unexpected deployment label")
    require(run["run_complete"] and not run["all_cases_passed"], "Failure/completeness state changed")
    require(not run["timings_qualified_for_comparison"], "Quality timings must remain unqualified")
    require(source(validator_path)["sha256"] == run["harness_sha256"]["validate_inference.py"],
            "Frozen validator hash mismatch")
    require(source(runner_path)["sha256"] == run["runner_source"]["sha256"], "Runner hash mismatch")
    validator = load_replay_validator(validator_path)
    index = {row["model"]: row for row in run["runs"]}
    require(set(index) == set(MODELS) and len(run["runs"]) == len(MODELS), "Model coverage mismatch")
    audit = {"scope": "Offline source/hash audit, exact request replay and semantic revalidation; no inference or performance measurement.",
             "run_source": source(args.current / "run.json"), "runner_source": run["runner_source"],
             "harness_sha256": run["harness_sha256"], "deployment_manifest": run["deployment_manifest"],
             "finished_utc": run["finished_utc"], "run_complete": True, "all_cases_passed": False,
             "inference_runner_expected_exit_code": 1, "timings_qualified_for_comparison": False,
             "models": [], "raw_source_files_verified": 0, "cases_replayed": 0,
             "warmups_replayed_separately": 0, "turn_requests_replayed": 0}
    binaries = None
    args.output.mkdir(parents=True, exist_ok=True)
    for model in MODELS:
        row = {"model": model, "suites": {}, "comparisons": {}}
        for suite, scenarios in SUITES.items():
            path = args.current / suite / (model + "-tensorsharp.json")
            expected = index[model]["suites"][suite]["source"]
            report = read_verified(path, expected)
            audit["raw_source_files_verified"] += 1
            coverage(report, scenarios)
            require(report["run_complete"] and not report["deployment_changed_files"], "Partial/mutated deployment")
            require(not report["timings_qualified_for_comparison"], "Unexpected qualified report")
            require(report["harness_sha256"] == run["harness_sha256"], "Suite harness identity differs")
            require(report["runner_source"] == run["runner_source"], "Suite runner identity differs")
            require(report["binary_sha256"]["libGgmlOps.so"] ==
                    "6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014", "Native hash differs")
            if binaries is None:
                binaries = report["binary_sha256"]
            require(binaries == report["binary_sha256"], "Managed/native binaries differ between models/suites")
            for case in report["cases"]:
                audit["turn_requests_replayed"] += replay_case(case, validator)
                audit["cases_replayed"] += 1
            audit["turn_requests_replayed"] += replay_case(report["warmup"], validator)
            audit["warmups_replayed_separately"] += 1
            passed = sum(c["status"] == "ok" for c in report["cases"])
            expected_status = "ok" if passed == len(report["cases"]) and report["warmup"]["status"] == "ok" else "fail"
            require(expected_status == report["status"] == index[model]["suites"][suite]["status"], "Suite status mismatch")
            require(passed == index[model]["suites"][suite]["passed"], "Suite pass count mismatch")
            row["suites"][suite] = {"cases": len(report["cases"]), "passed": passed,
                "failed": len(report["cases"]) - passed, "status_including_warmup": report["status"],
                "warmup_status": report["warmup"]["status"], "warmup_detail": report["warmup"].get("detail"),
                "by_scenario": {s: {"passed": sum(c["scenario"] == s and c["status"] == "ok" for c in report["cases"]),
                                      "cases": sum(c["scenario"] == s for c in report["cases"])} for s in scenarios}}
            if suite == "quality":
                for label, directory in REFERENCES.items():
                    reference = read_verified(args.historical / directory / path.name,
                                              report["comparisons"][label]["reference_source"])
                    audit["raw_source_files_verified"] += 1
                    row["comparisons"][label] = compare(reference, report, report["comparisons"][label])
                    require(report["comparisons"][label] == index[model]["comparisons"][label], "Top-level comparison mismatch")
            # Preserve every request, response, failed case, warmup, and raw timing.
            # Only process/GPU snapshots are omitted; original file hashes remain.
            curated = {k: v for k, v in report.items() if k not in ("before", "after")}
            curated["curation"] = {"raw_source": expected, "omitted_fields": ["before", "after"],
                                    "generated_output_modifications": None}
            out = args.output / suite / path.name
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_text(json.dumps(curated, ensure_ascii=False, indent=2) + "\n")
        audit["models"].append(row)
    audit["binary_sha256"] = binaries
    audit["totals"] = {suite: {"cases": sum(m["suites"][suite]["cases"] for m in audit["models"]),
                               "passed": sum(m["suites"][suite]["passed"] for m in audit["models"])} for suite in SUITES}
    audit["introduced_failed_cases"] = {label: sum(len(m["comparisons"][label]["introduced_failed_cases"])
                                                  for m in audit["models"]) for label in REFERENCES}
    audit["audit_source_sha256"] = source(Path(__file__))["sha256"]
    audit["curated_reports_sha256"] = {str(p.relative_to(args.output)): source(p)["sha256"]
        for suite in SUITES for p in sorted((args.output / suite).glob("*.json"))}
    (args.output / "audit.json").write_text(json.dumps(audit, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({k: audit[k] for k in ("totals", "introduced_failed_cases", "raw_source_files_verified",
          "cases_replayed", "warmups_replayed_separately", "turn_requests_replayed")}, indent=2))


if __name__ == "__main__":
    main()
