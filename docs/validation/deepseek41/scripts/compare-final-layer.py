#!/usr/bin/env python3
"""Local whole-group final-layer comparison; preserves every failed/different case."""
import hashlib
import json
import math
from pathlib import Path
import re
import statistics

WORK = Path('/tmp/deepseek41-reference')
RAW = WORK / 'final-raw'
DEST = Path('/Users/zhongkaifu/work/TensorSharp/docs/validation/deepseek41/final-placements')
CURATED = DEST.parent / 'full-checkpoint'
FINAL = 'layer8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final'
BASELINES = {'quality': 'layer8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-b26c',
             'steady': 'layer8-context65536-ubatch1024-cpumoe0-sparse1-4608',
             'long-parallel': 'layer8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-b26c'}


def sha(data): return hashlib.sha256(data).hexdigest()
def digest(value): return sha(json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':')).encode())
def source(path): return {'path': str(path), 'sha256': sha(path.read_bytes()), 'bytes': path.stat().st_size}
def read(profile, label): return json.loads((RAW / (profile + '-' + label + '.json')).read_text())
def key(case): return case['scenario'], case['tag'], case['concurrency'], case.get('repeat', 0)
def requests(case): return [t['request'] for t in case['turns']]
def outputs(case): return [(t['metrics'].get('assistant_message'), t['metrics'].get('finish_reason')) for t in case['turns']]
def tokens(case): return [(t['metrics'].get('prompt_tokens'), t['metrics'].get('completion_tokens')) for t in case['turns']]


def qualify(profile, label):
    path = CURATED / (profile + '-' + label + '.json')
    report = json.loads(path.read_text())
    raw = RAW / path.name
    assert report['source_report_sha256'] == sha(raw.read_bytes())
    return {'declared': report.get('performance_qualified') is True, 'source': source(path),
            'scope': 'Qualification declared by root/collector, bound to exact raw report SHA; periodic samples cannot prove every in-flight clock or absence of sub-second contention.'}


def summary(cases, waves):
    # Caller supplies every member of a complete scenario/concurrency group.
    result = {'cases': len(cases), 'passed': sum(c['status'] == 'ok' for c in cases),
              'prompt_completion_tokens': [tokens(c) for c in cases],
              'request_wall_ms_median': statistics.median(c['total_wall_ms'] for c in cases),
              'whole_wave_wall_ms_median': statistics.median(w['wall_ms'] for w in waves),
              'whole_wave_end_to_end_tps_median': statistics.median(w['generated_tokens'] / (w['wall_ms'] / 1000) for w in waves),
              'waves': waves}
    for name in ('ttft_ms', 'decode_tps'):
        values = [c['turns'][0]['metrics'].get(name, 0) for c in cases]
        if all(isinstance(v, (int, float)) and math.isfinite(v) and v > 0 for v in values):
            result[name + '_median'] = statistics.median(values)
    return result


def compare(label, old_profile):
    old, new = read(old_profile, label), read(FINAL, label)
    planned = new['execution_plan']
    assert new['run_complete'] is True
    expected = {(name, f'{name}-c{degree}-r{repeat}-i{i}', degree, repeat)
                for name in planned['scenarios'] for degree in planned['concurrency']
                for repeat in range(planned['repeats']) for i in range(degree)}
    selected_old = [c for c in old['cases'] if c['scenario'] in planned['scenarios']]
    a, b = {key(c): c for c in selected_old}, {key(c): c for c in new['cases']}
    assert len(a) == len(selected_old) and len(b) == len(new['cases'])
    assert set(a) == set(b) == expected
    for field in ('weights_id', 'sampling', 'thinking', 'stream', 'structured_tool_results', 'max_tokens_override'):
        assert old.get(field) == new.get(field), (label, field)
    assert bool(old.get('serial_tool_workflows')) == bool(new.get('serial_tool_workflows'))
    qa, qb = qualify(old_profile, label), qualify(FINAL, label)
    pairs = []
    for k in sorted(expected):
        x, y = a[k], b[k]
        pairs.append({'key': list(k), 'baseline_status': x['status'], 'final_status': y['status'],
                      'initial_request_matches': x['input_sha256'] == y['input_sha256'],
                      'full_turn_requests_match': requests(x) == requests(y),
                      'full_outputs_finish_match': outputs(x) == outputs(y),
                      'all_turn_token_counts_match': tokens(x) == tokens(y),
                      'baseline_request_hashes': [digest(r) for r in requests(x)], 'final_request_hashes': [digest(r) for r in requests(y)],
                      'baseline_output_hash': digest(outputs(x)), 'final_output_hash': digest(outputs(y)),
                      'baseline_token_counts': tokens(x), 'final_token_counts': tokens(y)})
    groups = {}
    for name in planned['scenarios']:
        for degree in planned['concurrency']:
            keys = sorted(k for k in expected if k[0] == name and k[2] == degree)
            aa, bb = [a[k] for k in keys], [b[k] for k in keys]
            selected = [p for p in pairs if p['key'][0] == name and p['key'][2] == degree]
            wa = [w for w in old['waves'] if w['scenario'] == name and w['concurrency'] == degree]
            wb = [w for w in new['waves'] if w['scenario'] == name and w['concurrency'] == degree]
            assert len(wa) == len(wb) == planned['repeats']
            for cases, waves in [(aa, wa), (bb, wb)]:
                assert sorted(w['repeat'] for w in waves) == list(range(planned['repeats']))
                for wave in waves:
                    members = [c for c in cases if c.get('repeat', 0) == wave['repeat']]
                    assert wave['generated_tokens'] == sum(n for c in members for _, n in tokens(c))
                    assert wave['all_passed'] == all(c['status'] == 'ok' for c in members)
            reasons = []
            if not qa['declared'] or not qb['declared']: reasons.append('A measurement window is unqualified')
            for field in ('initial_request_matches', 'full_turn_requests_match', 'full_outputs_finish_match', 'all_turn_token_counts_match'):
                if not all(p[field] for p in selected): reasons.append(field + ' is false for at least one planned member')
            if any(p['baseline_status'] != 'ok' or p['final_status'] != 'ok' for p in selected): reasons.append('At least one planned response failed')
            sa, sb = summary(aa, wa), summary(bb, wb)
            group = {'planned_members': len(keys), 'all_repeats_used': True,
                     'matched_workload_descriptive_ratios_available': not reasons,
                     'withholding_reasons': reasons, 'baseline': sa, 'final': sb,
                     'scope': 'Complete predeclared scenario/concurrency group. Ratios are descriptive across recorded build/config changes, not isolated native or managed causality.'}
            if not reasons:
                ratios = {}
                for field in ('ttft_ms_median', 'request_wall_ms_median', 'whole_wave_wall_ms_median'):
                    if field in sa and field in sb: ratios[field] = sa[field] / sb[field]
                for field in ('decode_tps_median', 'whole_wave_end_to_end_tps_median'):
                    if field in sa and field in sb: ratios[field] = sb[field] / sa[field]
                group['speedup_final_over_baseline'] = ratios
            groups[f'{name}@c{degree}'] = group
    return {'baseline_profile': old_profile, 'final_profile': FINAL,
            'baseline_raw_source': source(RAW / (old_profile + '-' + label + '.json')),
            'final_raw_source': source(RAW / (FINAL + '-' + label + '.json')),
            'baseline_qualification': qa, 'final_qualification': qb,
            'baseline_recorded_run_complete': old.get('run_complete'), 'baseline_recorded_plan': old.get('execution_plan'),
            'baseline_full_report_cases': len(old['cases']), 'baseline_selected_whole_scenario_cases': len(selected_old),
            'exact_case_key_coverage': True, 'case_pairs': pairs, 'groups': groups,
            'failed_cases': {'baseline': [c for c in selected_old if c['status'] != 'ok'], 'final': [c for c in new['cases'] if c['status'] != 'ok']}}


def audit_parallel():
    report, phase = read(FINAL, 'long-parallel'), read(FINAL, 'long-parallel-runs')
    assert phase['run_complete'] and phase['all_passed'] and len(phase['runs']) == 1
    run = phase['runs'][0]
    assert run['exit_code'] == 0 and run['child_validation']['all_passed']
    assert run['child_validation']['report_sha256'] == sha((RAW / (FINAL + '-long-parallel.json')).read_bytes())
    a = run['native_accounting']
    server_path = RAW / (FINAL + '-server.log')
    with server_path.open('rb') as stream:
        stream.seek(a['byte_start']); blob = stream.read(a['byte_end'] - a['byte_start'])
    assert sha(blob) == a['range_sha256']
    forwards = [(int(n), float(t)) for n, t in re.findall(rb'\[dsv4\] forward (\d+) tokens in ([\d.]+)s', blob)]
    chats = [(int(n), int(p), int(k), reason.decode()) for n, p, k, reason in re.findall(rb'chat.complete tokens=(\d+) promptTokens=(\d+) kvReused=(\d+).*?finishReason=(\S+)', blob)]
    native = {'prefill_calls': sum(n > 1 for n, _ in forwards), 'prefill_tokens': sum(n for n, _ in forwards if n > 1),
              'prefill_forward_seconds': sum(t for n, t in forwards if n > 1),
              'decode_calls': sum(n == 1 for n, _ in forwards), 'decode_forward_seconds': sum(t for n, t in forwards if n == 1)}
    assert all(math.isclose(native[k], a[k], rel_tol=1e-12, abs_tol=1e-9) for k in native)
    assert native['prefill_tokens'] == sum(p - k for _, p, k, _ in chats) == 153187
    assert native['decode_calls'] == sum(n + (reason == 'eos') for n, _, _, reason in chats)
    assert len(chats) == 9 and len(report['cases']) == 8
    assert sum(p for c in report['cases'] for p, _ in tokens(c)) == 153164
    assert sum(n for c in report['cases'] for _, n in tokens(c)) == 272
    diagnostic = [x.decode(errors='replace') for x in blob.splitlines() if not any(marker in x for marker in (b'assistantOutput=', b'userInput=', b'fullInput='))]
    preemptions = [x for x in diagnostic if re.search(r'preempting|preempted', x, re.I)]
    errors = [x for x in diagnostic if re.search(r'^\s*(fail|crit|fatal):|^\[dsv4\].*(error|failed)|terminate called|GGML_ASSERT|CUDA error|Segmentation fault', x, re.I)]
    assert not preemptions and not errors and a['preemption_lines'] == []
    return {'independently_validated': True, 'raw_report_source': source(RAW / (FINAL + '-long-parallel.json')),
            'phase_source': source(RAW / (FINAL + '-long-parallel-runs.json')), 'server_source': source(server_path),
            'range': {k: a[k] for k in ('byte_start', 'byte_end', 'range_sha256')}, 'native': native,
            'prompt_tokens_without_warmup': 153164, 'prompt_tokens_including_warmup': 153187,
            'generated_tokens_without_warmup': 272, 'chat_completions_including_warmup': 9,
            'error_count': 0, 'preemption_count': 0,
            'scope': 'Eight final long-parallel responses plus one warmup; exact range bytes, native counters and usage/EOS work reconciled.'}


def main():
    audit = json.loads((DEST / 'layer8-report.json').read_text())
    assert audit['integrity_valid'] and audit['all_audited_scenarios_passed'] and not audit['issues']
    result = {'scope': 'Whole-group historical CPU0 layer comparisons. Full requests, outputs and token counts must match; preserve every failed group and build/scheduler/thread confound. No isolated-fix or llama.cpp parity claim.',
              'analysis_source': source(Path(__file__)), 'control_inventory_source': source(WORK / 'final-layer-controls.json'),
              'final_layer_audit': source(DEST / 'layer8-report.json'),
              'comparisons': {label: compare(label, old) for label, old in BASELINES.items()},
              'final_long_parallel_audit': audit_parallel(),
              'control_configuration_details': json.loads((WORK / 'final-layer-controls.json').read_text())['controls']}
    output = DEST / 'layer8-comparison.json'
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'report': str(output), 'groups': {label: {name: value.get('speedup_final_over_baseline', value['withholding_reasons']) for name, value in comparison['groups'].items()} for label, comparison in result['comparisons'].items()}, 'long_parallel_native': result['final_long_parallel_audit']['native']}, indent=2))


if __name__ == '__main__': main()
