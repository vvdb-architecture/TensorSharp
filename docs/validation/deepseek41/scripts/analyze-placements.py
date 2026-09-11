#!/usr/bin/env python3
"""Read-only comparison of explicitly selected DeepSeek V4.1 placement reports."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import statistics


def read(path):
    raw = Path(path).read_bytes()
    return json.loads(raw), {"path": str(Path(path).resolve()), "sha256": hashlib.sha256(raw).hexdigest()}


def flag(command, name):
    values = [command[i + 1] for i, item in enumerate(command[:-1]) if item == name]
    return values[-1] if len(values) == 1 else None


def number(value):
    try:
        return int(value)
    except (ValueError, TypeError):
        return None


def setting_map(launch):
    env, cmd = launch.get("environment", {}), launch.get("command", [])
    scheduler = ["TS_SCHED_MAX_RUNNING_SEQS", "TS_SCHED_PREFILL_CHUNK", "TS_SCHED_SOLO_PREFILL_CHUNK",
                 "TS_SCHED_MAX_BATCHED_TOKENS", "TS_SCHED_NUM_BLOCKS"]
    visible = env.get("CUDA_VISIBLE_DEVICES")
    settings = {
        "cpu_moe": number(flag(cmd, "--n-cpu-moe")), "tp": number(env.get("TS_DSV41_TP")),
        "gpus": number(env.get("TS_DSV4_NGPU")), "cli_gpu_count": number(flag(cmd, "--tp")),
        "visible_devices": visible, "visible_count": len(visible.split(",")) if visible else None,
        "ubatch": number(env.get("TS_DSV4_UBATCH")), "context": number(env.get("MAX_CONTEXT")),
        "backend": flag(cmd, "--backend"), "model_path": flag(cmd, "--model"),
        "companion_path": flag(cmd, "--mmproj"), "cpu_moe_threads": number(flag(cmd, "--cpu-moe-threads")),
    }
    for key in scheduler + ["KV_CACHE_DTYPE", "TS_DSV41_SPARSE_FA", "TS_DSV41_COMPACT_RAW_GATHER",
                            "TS_DSV41_ENGRAM_WARM", "TS_DSV41_ENGRAM_THREADS"]:
        settings[key] = env.get(key)  # Missing values stay unknown; never infer historical defaults.
    for key in ["libGgmlOps.so", "TensorSharp.Server.Host.dll", "TensorSharp.Runtime.dll",
                "TensorSharp.Server.dll", "TensorSharp.Chat.dll", "TensorSharp.Models.dll"]:
        settings["sha256." + key] = launch.get("sha256", {}).get(key)
    return settings


def summary(values):
    values = [float(v) for v in values if isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)]
    return {"n": len(values), "median": statistics.median(values) if values else None,
            "min": min(values) if values else None, "max": max(values) if values else None, "samples": values}


def metrics(case):
    turns = case.get("turns", [])
    return turns[-1].get("metrics", {}) if turns else {}


def identity(case):
    return (case.get("scenario"), case.get("concurrency"), case.get("repeat", case.get("repetition")), case.get("tag"))


def case_summary(cases):
    result = {"cases": len(cases), "passed": sum(c.get("status") == "ok" for c in cases),
              "failed": sum(c.get("status") != "ok" for c in cases), "metric_turn": "final turn"}
    for name in ["prompt_tokens", "completion_tokens", "ttft_ms", "prefill_tps", "decode_tps", "total_wall_ms"]:
        result[name] = summary([metrics(c).get(name) for c in cases])
    result["timing_sources"] = dict(Counter(metrics(c).get("decode_timing_source", "missing") for c in cases))
    result["finish_reasons"] = dict(Counter(metrics(c).get("finish_reason", "missing") for c in cases))
    result["exact_512_token_completions"] = sum(metrics(c).get("completion_tokens") == 512 for c in cases)
    result["conversation_wall_ms"] = summary([c.get("total_wall_ms", c.get("wall_seconds", 0) * 1000) for c in cases])
    return result


def wave_summary(waves):
    return {"count": len(waves), "wall_ms": summary([w.get("wall_ms") for w in waves]),
            "end_to_end_tokens_per_second": summary([w.get("generated_tokens", 0) * 1000 / w["wall_ms"]
                for w in waves if isinstance(w.get("wall_ms"), (int, float)) and w["wall_ms"] > 0]),
            "raw": waves}


def analyze_report(spec, profile, launch, launch_ok):
    report, source = read(spec["path"])
    issues, cases = [], report.get("cases", [])
    expected = spec.get("expected_cases")
    plan = report.get("execution_plan", {})
    if not isinstance(expected, int) or expected < 1:
        issues.append("manifest must explicitly provide a positive expected_cases")
    if report.get("run_complete") is not True:
        issues.append("run_complete is missing or not true")
    if plan.get("expected_cases") != expected or len(cases) != expected:
        issues.append(f"case count mismatch: manifest={expected}, report_plan={plan.get('expected_cases')}, actual={len(cases)}")
    if all(k in plan for k in ["scenarios", "concurrency", "repeats"]):
        derived = len(plan["scenarios"]) * sum(plan["concurrency"]) * plan["repeats"]
        if derived != expected:
            issues.append(f"execution plan derives {derived} cases instead of {expected}")
        planned_waves = {(s, c, r): c for s in plan["scenarios"] for c in plan["concurrency"] for r in range(plan["repeats"])}
        actual_waves = Counter((c.get("scenario"), c.get("concurrency"), c.get("repeat", c.get("repetition"))) for c in cases)
        if planned_waves != actual_waves:
            issues.append("actual scenario/concurrency/repetition counts differ from the execution plan")
    elif isinstance(plan.get("waves"), list):
        planned_waves = {(w.get("scenario"), w.get("concurrency"), w.get("repetition", w.get("repeat"))): w.get("concurrency") for w in plan["waves"]}
        actual_waves = Counter((c.get("scenario"), c.get("concurrency"), c.get("repeat", c.get("repetition"))) for c in cases)
        if planned_waves != actual_waves or sum(planned_waves.values()) != expected:
            issues.append("actual tool-policy wave counts differ from the execution plan")
    if len({identity(c) for c in cases}) != len(cases):
        issues.append("duplicate case identities")
    if any(c.get("status") not in ("ok", "fail") for c in cases):
        issues.append("unknown case status")
    if report.get("profile") != profile or launch.get("profile") != profile:
        issues.append("report/launch profile differs from manifest id")
    if report.get("native_sha256") not in (None, launch.get("sha256", {}).get("libGgmlOps.so")):
        issues.append("report native hash differs from launch hash")
    if not report.get("weights_id") or not isinstance(report.get("sampling"), dict):
        issues.append("weights identity or sampling settings missing")
    for case in cases:
        if not case.get("turns") or not case["turns"][0].get("request_sha256"):
            issues.append(f"initial request hash missing: {case.get('tag')}")
    evidence = None
    if spec.get("qualification_evidence"):
        _, evidence = read(spec["qualification_evidence"])
    qualified = report.get("performance_qualified") is True or (spec.get("performance_qualified") is True and evidence is not None)
    groups = defaultdict(list)
    for case in cases:
        groups[f"{case.get('scenario')}@c{case.get('concurrency')}"] += [case]
    output = {"role": spec["role"], "source": source, "eligible": not issues and launch_ok,
              "integrity_issues": issues, "launch_valid": launch_ok,
              "performance_qualified_declared": qualified, "qualification_evidence": evidence,
              "overall": case_summary(cases), "groups": {k: {**case_summary(v), "waves": wave_summary([
                  w for w in report.get("waves", []) if f"{w.get('scenario')}@c{w.get('concurrency')}" == k])}
                  for k, v in groups.items()},
              "failed_cases": [c for c in cases if c.get("status") != "ok"],
              "waves": report.get("waves", [])}
    # Retain all raw case outputs in the analysis, not just successful summaries.
    output["cases"] = cases
    return output, report


def analyze_profile(spec):
    launch, source = read(spec["launch"])
    settings, issues = setting_map(launch), []
    for key in ["cpu_moe", "tp", "gpus", "ubatch", "context", "backend", "visible_devices"]:
        if settings[key] is None:
            issues.append(f"launch setting missing or ambiguous: {key}")
    if settings["gpus"] != settings["visible_count"] or settings["gpus"] != settings["cli_gpu_count"]:
        issues.append("native selected GPU count, visibility and CLI count disagree")
    if settings["tp"] not in (0, settings["gpus"]):
        issues.append("TP rank count must be 0 or equal selected GPU count")
    for key, value in spec.get("expected", {}).items():
        if settings.get(key) != value:
            issues.append(f"expected {key}={value!r}; launch records {settings.get(key)!r}")
    for key, value in settings.items():
        if key.startswith("sha256.") and (not isinstance(value, str) or len(value) != 64 or any(c not in '0123456789abcdef' for c in value)):
            issues.append(f"missing/invalid launch hash: {key}")
    reports, raw = [], {}
    for item in spec["reports"]:
        output, report = analyze_report(item, spec["id"], launch, not issues)
        if item["role"] in raw:
            raise ValueError("Each profile report role must be unique; use decode8k-c1/c4 for separate files")
        reports.append(output); raw[item["role"]] = report
    return {"id": spec["id"], "launch": source, "settings": settings, "launch_issues": issues,
            "unknown_scheduler_settings": [k for k, v in settings.items() if k.startswith("TS_SCHED_") and v is None],
            "preparation_evidence": [read(path)[1] for path in spec.get("preparation_evidence", [])],
            "reports": reports}, raw


def compare(left, right, raw_left, raw_right, permitted):
    differences = {k: [v, right["settings"].get(k)] for k, v in left["settings"].items() if v != right["settings"].get(k)}
    confounds = {k: v for k, v in differences.items() if k not in permitted}
    scheduler_fields = ["TS_SCHED_MAX_RUNNING_SEQS", "TS_SCHED_PREFILL_CHUNK", "TS_SCHED_SOLO_PREFILL_CHUNK",
                        "TS_SCHED_MAX_BATCHED_TOKENS"]
    scheduler_known = all(p["settings"].get(key) is not None for p in (left, right) for key in scheduler_fields)
    result = {"reference": left["id"], "candidate": right["id"], "setting_differences": differences,
              "unpermitted_differences": confounds, "scheduler_controls_known": scheduler_known, "reports": []}
    lmeta = {r["role"]: r for r in left["reports"]}; rmeta = {r["role"]: r for r in right["reports"]}
    for role in sorted(set(raw_left) | set(raw_right)):
        if role not in raw_left or role not in raw_right:
            result["reports"].append({"role": role, "comparable": False, "issue": "report role missing in one profile"})
            continue
        a, b = raw_left[role], raw_right[role]
        ac = {identity(c): c for c in a.get("cases", [])}; bc = {identity(c): c for c in b.get("cases", [])}
        controls = ["weights_id", "sampling", "thinking", "stream", "structured_tool_results", "max_tokens_override"]
        control_diffs = {k: [a.get(k), b.get(k)] for k in controls if a.get(k) != b.get(k)}
        pairs, unmatched = [], []
        for key in sorted(set(ac) | set(bc), key=str):
            x, y = ac.get(key), bc.get(key)
            same_request = (x is not None and y is not None and x.get("turns") and y.get("turns") and
                            x["turns"][0].get("request_sha256") is not None and
                            x["turns"][0].get("request_sha256") == y["turns"][0].get("request_sha256"))
            if not same_request:
                unmatched.append({"identity": key, "issue": "case missing or initial request hashes differ", "reference": x, "candidate": y})
            else:
                pairs.append((x, y))
        eligible = lmeta[role]["eligible"] and rmeta[role]["eligible"] and not control_diffs and not unmatched
        work_matches = all(all(metrics(x).get(k) is not None and metrics(x).get(k) == metrics(y).get(k)
            for k in ["prompt_tokens", "completion_tokens"]) for x, y in pairs)
        decode_budget_matches = all(metrics(c).get("completion_tokens") == 512
            for pair in pairs for c in pair if c.get("scenario") in ("decode", "decode_8k"))
        groups = defaultdict(list)
        for x, y in pairs:
            groups[f"{x['scenario']}@c{x['concurrency']}"].append((x, y))
        statistics_by_group = {}
        for group, group_pairs in groups.items():
            token_work_mismatches = [{"identity": identity(x), "reference": [metrics(x).get("prompt_tokens"), metrics(x).get("completion_tokens")],
                "candidate": [metrics(y).get("prompt_tokens"), metrics(y).get("completion_tokens")]}
                for x, y in group_pairs if any(metrics(x).get(k) != metrics(y).get(k) for k in ["prompt_tokens", "completion_tokens"])]
            statistics_by_group[group] = {"reference": case_summary([x for x, y in group_pairs]),
                "candidate": case_summary([y for x, y in group_pairs]),
                "token_work_mismatches": token_work_mismatches,
                "both_sides_exact_512_tokens": all(metrics(c).get("completion_tokens") == 512 for pair in group_pairs for c in pair),
                "introduced_failures": [y for x, y in group_pairs if x.get("status") == "ok" and y.get("status") != "ok"],
                "resolved_failures": [x for x, y in group_pairs if x.get("status") != "ok" and y.get("status") == "ok"],
                "descriptive_paired_ratios": {name + "_candidate_over_reference": summary([
                    metrics(y)[name] / metrics(x)[name] for x, y in group_pairs
                    if isinstance(metrics(x).get(name), (int, float)) and metrics(x)[name] > 0
                    and isinstance(metrics(y).get(name), (int, float))])
                    for name in ["decode_tps", "ttft_ms", "prefill_tps", "total_wall_ms"]}}
        result["reports"].append({"role": role, "quality_comparable": eligible,
            "performance_comparable": eligible and work_matches and decode_budget_matches and not confounds and scheduler_known and lmeta[role]["performance_qualified_declared"] and rmeta[role]["performance_qualified_declared"],
            "token_work_matches": work_matches, "decode_budget_512_matches": decode_budget_matches,
            "harness_source_differences": {k: [a.get("harness_sha256", {}).get(k), b.get("harness_sha256", {}).get(k)]
                for k in set(a.get("harness_sha256", {})) | set(b.get("harness_sha256", {}))
                if a.get("harness_sha256", {}).get(k) != b.get("harness_sha256", {}).get(k)},
            "control_differences": control_diffs, "matched_cases": len(pairs), "unmatched_cases": unmatched,
            "groups": statistics_by_group})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest"); parser.add_argument("output")
    args = parser.parse_args()
    manifest, provenance = read(args.manifest)
    profiles, raw = [], {}
    for spec in manifest["profiles"]:
        output, data = analyze_profile(spec); profiles.append(output); raw[spec["id"]] = data
    by_id = {p["id"]: p for p in profiles}
    reference = by_id[manifest["reference"]]
    pairs = manifest.get("comparison_pairs", [[reference["id"], p["id"]] for p in profiles if p != reference])
    output = {"scope": "Read-only placement comparison; missing/incomplete evidence is never treated as a pass. No llama.cpp parity claim.",
              "purpose": manifest.get("purpose"),
              "manifest": provenance, "analyzer_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "profiles": profiles, "comparisons": [compare(by_id[a], by_id[b], raw[a], raw[b],
                    set(manifest.get("permitted_differences", ["cpu_moe", "tp"]))) for a, b in pairs]}
    Path(args.output).write_text(json.dumps(output, indent=2, ensure_ascii=False, allow_nan=False) + "\n")
    for profile in profiles:
        print(profile["id"], "launch issues:", len(profile["launch_issues"]))
        for report in profile["reports"]:
            print(" ", report["role"], f"{report['overall']['passed']}/{report['overall']['cases']} passed; eligible={report['eligible']}")


if __name__ == "__main__":
    main()
