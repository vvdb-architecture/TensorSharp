#!/usr/bin/env python3
"""Read-only inventory of preserved controls for the pending final layer audit."""
import hashlib
import json
from pathlib import Path
import statistics
import sys

REPO = Path('/Users/zhongkaifu/work/TensorSharp')
WORK = Path('/tmp/deepseek41-reference')
RAW = WORK / 'final-raw'
CURATED = REPO / 'docs/validation/deepseek41/full-checkpoint'
sys.path.insert(0, str(REPO / 'benchmarks/engine_comparison'))
import validate_inference as validation

GROUPS = {
    'quality': (['short', 'json', 'json_schema', 'multi_turn', 'tool_round_trip', 'agentic'], [1, 4], 1, True, False),
    'steady': (['decode'], [1, 4], 3, False, False),
    'long': (['long_8k', 'long_32k'], [1], 1, False, False),
    'serial-tools': (['tool_round_trip', 'agentic'], [1, 4], 1, True, True),
    'long-parallel': (['long_8k', 'long_32k'], [4], 1, False, False),
}
PROFILES = {
    'closest_cpu0_compact': 'layer8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-b26c',
    'closest_cpu0_steady': 'layer8-context65536-ubatch1024-cpumoe0-sparse1-4608',
    'older_cpu0_ubatch256': 'layer8-context65536-ubatch256-cpumoe0-sparse1-warm',
    'same_native_cpu4_placement': 'layer8-context65536-ubatch1024-cpumoe4-cputhreads48-sparse1-compact1-chunk1024-6b3-final',
    'same_build_tp8_placement': 'tp8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final',
}


def source(path):
    raw = path.read_bytes()
    return {'path': str(path), 'sha256': hashlib.sha256(raw).hexdigest(), 'bytes': len(raw)}


def key(case):
    return case['scenario'], case['tag'], case['concurrency'], case.get('repeat', 0)


def expected(group):
    names, degrees, repeats, structured, serial = GROUPS[group]
    result = {}
    for name in names:
        for degree in degrees:
            for repeat in range(repeats):
                for index in range(degree):
                    tag = f'{name}-c{degree}-r{repeat}-i{index}'
                    value = {**validation.case_spec(name, tag), 'sampling': validation.SAMPLING, 'thinking': False, 'stream': True}
                    if structured:
                        value['structured_tool_results'] = True
                    if serial:
                        value['serial_tool_workflows'] = True
                    result[(name, tag, degree, repeat)] = validation.digest(value)
    return result


def summarize(path, group):
    data = json.loads(path.read_text())
    names = GROUPS[group][0]
    cases = [c for c in data['cases'] if c['scenario'] in names]
    planned = expected(group)
    keys = [key(c) for c in cases]
    complete = len(keys) == len(set(keys)) and set(keys) == set(planned)
    matches = sum(key(c) in planned and c['input_sha256'] == planned[key(c)] for c in cases)
    curated_path = CURATED / path.name
    curated = json.loads(curated_path.read_text()) if curated_path.exists() else {}
    curated_bound = curated.get('source_report_sha256') == source(path)['sha256']
    subsets = {}
    for name in names:
        for degree in GROUPS[group][1]:
            rows = [c for c in cases if c['scenario'] == name and c['concurrency'] == degree]
            if not rows:
                continue
            waves = [w for w in data.get('waves', []) if w['scenario'] == name and w['concurrency'] == degree]
            values = [c['turns'][0]['metrics'] for c in rows if c.get('turns')]
            subset = {'cases': len(rows), 'passed': sum(c['status'] == 'ok' for c in rows),
                      'repeats': sorted({c.get('repeat', 0) for c in rows}),
                      'all_case_ttft_ms_median': statistics.median(v['ttft_ms'] for v in values) if values else None,
                      'all_case_decode_tps_median': statistics.median(v.get('decode_tps', 0) for v in values) if values else None,
                      'all_wave_wall_ms': [w['wall_ms'] for w in waves],
                      'completion_tokens': [v.get('completion_tokens') for v in values],
                      'prompt_tokens': [v.get('prompt_tokens') for v in values],
                      'scope': 'Descriptive historical values include failed cases. No final ratio or selected successful subset.'}
            subsets[f'{name}@c{degree}'] = subset
    return {'source': source(path), 'recorded_run_complete': data.get('run_complete'),
            'recorded_plan': data.get('execution_plan'), 'full_report_cases': len(data['cases']),
            'selected_scenario_cases': len(cases), 'other_scenario_cases': len(data['cases']) - len(cases),
            'whole_planned_scenario_group_coverage': complete, 'expected_final_group_cases': len(planned),
            'matching_planned_initial_request_hashes': matches,
            'missing_final_keys': [list(k) for k in sorted(set(planned) - set(keys))],
            'extra_same_scenario_keys': [list(k) for k in sorted(set(keys) - set(planned))],
            'selected_group_passed': sum(c['status'] == 'ok' for c in cases),
            'curated_source_hash_matches_raw': curated_bound,
            'historical_performance_qualified_declared': curated.get('performance_qualified') if curated_bound else None,
            'curated_source': source(curated_path) if curated_path.exists() else None,
            'failed_cases': [{'key': list(key(c)), 'detail': c.get('detail'),
                              'input_sha256': c['input_sha256'], 'output_sha256': [validation.digest(t['metrics'].get('assistant_message')) for t in c.get('turns', [])],
                              'assistant_messages': [t['metrics'].get('assistant_message') for t in c.get('turns', [])]}
                             for c in cases if c['status'] != 'ok'],
            'subgroups': subsets}


def main():
    manifest_path = WORK / 'final-layer8-audit-manifest.json'
    target = json.loads(manifest_path.read_text())
    reports = {}
    for alias, profile in PROFILES.items():
        launch_path = RAW / (profile + '-launch.json')
        launch = json.loads(launch_path.read_text())
        env = launch['environment']
        cmd = launch['command']
        flags = {value: cmd[i + 1] for i, value in enumerate(cmd[:-1]) if value.startswith('--') and not cmd[i + 1].startswith('--')}
        reports[alias] = {
            'profile': profile, 'launch_source': source(launch_path),
            'binaries': {k: launch['sha256'][k] for k in ['libGgmlOps.so', 'TensorSharp.Runtime.dll', 'TensorSharp.Chat.dll', 'TensorSharp.Server.dll']},
            'environment': env, 'command_flags': flags,
            'environment_differences_from_final_manifest': {k: {'historical': env.get(k), 'final': v} for k, v in target['expected']['environment'].items() if env.get(k) != v},
            'flag_differences_from_final_manifest': {k: {'historical': flags.get(k), 'final': v} for k, v in target['expected']['command_flags'].items() if flags.get(k) != v},
            'groups': {label: summarize(RAW / (profile + '-' + label + '.json'), label) for label in GROUPS if (RAW / (profile + '-' + label + '.json')).exists()},
        }
    result = {
        'scope': 'Local preserved-control inventory against the intended final layer request plan. The final layer report has not been pulled or audited here, so no final speedup/regression ratios are asserted.',
        'target_manifest': source(manifest_path), 'target_profile': target['profile'], 'target_expected': target['expected'],
        'controls': reports, 'analysis_source': source(Path(__file__)),
        'portable_harness_source': source(Path(validation.__file__)),
        'additional_native_accounting_sources': [source(RAW / name) for name in [
            PROFILES['closest_cpu0_compact'] + '-quality-runs.json',
            PROFILES['closest_cpu0_compact'] + '-long-parallel-runs.json',
            PROFILES['closest_cpu0_steady'] + '-parallel-native-accounting.json']],
        'comparison_rules': [
            'Use every case/repeat in each predeclared scenario/concurrency group. The15 decode cases inside old30 are a complete scenario; do not select favorable repeats or successful requests.',
            'The older CPU0 long reports have six cases, not the final two. Two r0 hashes match, but selecting only r0 would discard four same-scenario repeats; withhold exact whole-group performance ratios.',
            'Initial request hashes do not prove identical rendered token histories or generated work. Final full per-turn request/output/token counts, no-preemption/error accounting and telemetry must be checked before ratios.',
            'CPU4/TP8 controls are placement comparisons, not historical CPU0 regression controls. Preserve every quality failure, including failed8k parallel formatting.',
            'Absent legacy run_complete/plan fields are not retroactively claimed present. Record reconstructed complete scenario/wave coverage separately.',
            'Older CLI CPU-MoE48 did not necessarily set native48; before the native getter fix the inferred auto default was32, not directly observed on those full hosts.',
            'The b26c CPU0 full server log is absent from this local raw directory. Its recorded native counters/range hashes and no-preemption list are preserved, but the range bytes were not independently rehashed during this inventory.',
        ],
    }
    (WORK / 'final-layer-controls.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({alias: {label: {'cases': g['selected_scenario_cases'], 'passed': g['selected_group_passed'], 'hash_matches': g['matching_planned_initial_request_hashes'], 'whole_group': g['whole_planned_scenario_group_coverage']} for label, g in value['groups'].items()} for alias, value in reports.items()}, indent=2))


if __name__ == '__main__':
    main()
