#!/usr/bin/env python3
"""Offline audit of the four-job uninstrumented solo JSON control.

Requires the independently reviewed runner SHA. Never starts a host or rereads VM files.
"""
import argparse
from datetime import datetime
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import statistics
import sys

WORK = Path('/tmp/deepseek41-reference')
ROOT = Path('/Users/zhongkaifu/work/TensorSharp')
sys.path.insert(0, str(ROOT / 'benchmarks/engine_comparison'))


def source(p):
    data = p.read_bytes()
    return {'path': str(p), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def read(p): return json.loads(p.read_text())


def load(name, p):
    spec = importlib.util.spec_from_file_location(name, p)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def bound(p, item):
    actual = source(p)
    assert all(actual[k] == item[k] for k in ('bytes', 'sha256')), str(p)
    return actual


def summarize(cases):
    result = {'cases': len(cases), 'logical_ids': [c['logical_id'] for c in cases],
              'statuses': [c['status'] for c in cases],
              'scope': 'All specified observations retained, descriptive only.'}
    for field in ('ttft_ms', 'total_wall_ms', 'decode_tps', 'wave_wall_ms'):
        values = [c.get(field) if field == 'wave_wall_ms' else c['turns'][0]['metrics'].get(field) if len(c.get('turns', [])) == 1 else None for c in cases]
        valid = bool(values) and all(isinstance(x, (int, float)) and math.isfinite(x) and x > 0 for x in values)
        result[field] = {'values': values, 'all_valid': valid, 'median': statistics.median(values) if valid else None}
    result['completion_tokens'] = [c['turns'][0]['metrics'].get('completion_tokens') if len(c.get('turns', [])) == 1 else None for c in cases]
    return result


def telemetry(r2, raw, label, report, owner):
    p = raw / (label + '-telemetry.jsonl')
    bound(p, report['telemetry_source'])
    start, end = report['timed_monotonic_interval']
    rows = [json.loads(s) for s in p.read_text().splitlines()]
    rows = [r for r in rows if start <= r['monotonic'] <= end]
    q = report['telemetry_qualification']
    assert q['samples_in_timed_interval'] == len(rows)
    gpus, foreign, nonzero = [], [], []
    for r in rows:
        selected = [g for g in r['gpus'] if g['index'] == '7']
        assert len(selected) == 1
        gpus += selected
        foreign += [c for c in r['compute_clients'] if c['pid'] != owner]
        if q['qualified']:
            assert len(r['compute_clients']) == 1 and r['compute_clients'][0]['pid'] == owner
            assert r['compute_clients'][0]['gpu_uuid'] == selected[0]['uuid']
    stats = {}
    if gpus:
        for field in r2.GPU_FIELDS.split(',')[2:]:
            values = [float(g[field]) for g in gpus]
            assert all(math.isfinite(v) for v in values)
            stats[field] = {'min': min(values), 'median': statistics.median(values), 'max': max(values)}
        assert q['gpu7'] == stats
    for old, new in zip(rows, rows[1:]):
        owner_state = new['cpu'].get(str(owner))
        assert owner_state
        owned = {owner, owner_state['ppid']}
        while True:
            more = owned | {int(pid) for pid, r in new['cpu'].items() if r['ppid'] in owned}
            if more == owned: break
            owned = more
        for pid, r in new['cpu'].items():
            before = old['cpu'].get(pid)
            if int(pid) not in owned and before and before['start'] == r['start']:
                ticks = max(0, r['ticks'] - before['ticks'])
                if ticks:
                    nonzero.append({'pid': int(pid), 'comm': r['comm'], 'ticks': ticks,
                                    'interval_seconds': new['monotonic'] - old['monotonic']})
    if q['qualified']:
        assert len(rows) >= 2 and not q['issues'] and not foreign
        assert q['policy']['external_exclusive_declaration'] is True
        for field, limit in [('clocks.sm', .03), ('clocks.mem', .005)]:
            d = stats[field]
            assert d['min'] > 0 and (d['max'] - d['min']) / d['median'] <= limit
        assert q['foreign_gpu_clients'] == [] and q['foreign_cpu_processes'] == []
    return {'source': source(p), 'producer_qualified': q['qualified'], 'samples': len(rows),
            'owner_pid': owner, 'recomputed_gpu7': stats, 'foreign_gpu_clients': foreign,
            'raw_non_owned_cpu_tick_changes': nonzero,
            'scope': 'Clock/client values recomputed. Producer CPU policy retained; raw tick deltas shown without assuming the local OS tick rate. Sampling does not prove every in-flight condition.'}


def log_content(value):
    # Exact LoggingExtensions.AppendEscaped behavior for these untruncated inputs.
    return ''.join({'\r': r'\r', '\n': r'\n', '\t': r'\t'}.get(c, '?' if ord(c) < 0x20 else c) for c in value)


def utc(value):
    # .NET emits seven fractional digits; Python 3.9 accepts six. These
    # ordering checks have millisecond-or-larger margins, not sub-microsecond claims.
    value = re.sub(r'(\.\d{6})\d+(?=Z|[+-]\d\d:)', r'\1', value)
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def server_correlations(raw, report):
    """Repeated tags are matched by occurrence/order and structured RequestId."""
    sources, records = [], []
    for item in report['runtime_file_logs']:
        p = raw / Path(item['path']).parent.name / Path(item['path']).name
        bound(p, item)
        sources.append(source(p))
        records += [json.loads(line) for line in p.read_text().splitlines()]
    records = [r for r in records if r.get('category') == 'TensorSharp.Server.ModelService']
    starts = [r for r in records if r.get('event', {}).get('name') == 'ChatStarted']
    completes = [r for r in records if r.get('event', {}).get('name') == 'ChatCompleted']
    assert len(starts) == len(completes) == 24
    by_id = {}
    for c in completes:
        pid = c['scope']['RequestId']
        assert pid not in by_id
        by_id[pid] = c
    assert len({r['scope']['RequestId'] for r in starts}) == 24
    assert {r['scope']['RequestId'] for r in starts} == set(by_id)
    for start in starts:
        assert utc(start['ts']) < utc(by_id[start['scope']['RequestId']]['ts'])
    assert utc(report['native_mapping_observation']['observed_utc']) <= min(utc(r['ts']) for r in starts)
    warmup_pairs = []
    for case in [report['decode512_warmup']] + report['warmups']:
        expected_content = log_content(case['turns'][0]['request']['messages'][-1]['content'])
        matches = [r for r in starts if r['props']['LastUserContent'] == expected_content]
        assert len(matches) == 1
        start = matches[0]
        complete = by_id[start['scope']['RequestId']]
        metrics = case['turns'][0]['metrics']
        assert complete['props']['PromptTokens'] == metrics['prompt_tokens']
        assert complete['props']['Tokens'] == metrics['completion_tokens']
        assert complete['props']['FinishReason'] == {'length': 'max_tokens', 'stop': 'eos'}[metrics['finish_reason']]
        assert complete['props']['AssistantContent'] == log_content(metrics['assistant_message']['content'])
        warmup_pairs.append({'tag': case['tag'], 'request_id': start['scope']['RequestId'], 'start_utc': start['ts'], 'completed_utc': complete['ts'], 'prompt_tokens': metrics['prompt_tokens'], 'completion_tokens': metrics['completion_tokens'], 'status': case['status']})
    result = []
    measured = [r for r in starts if re.search(r'\[validation json-c1-r[012]-i0\]', r['props']['LastUserContent'])]
    assert len(measured) == 18
    for iteration, (start, case) in enumerate(zip(measured, report['cases'])):
        request_id = start['scope']['RequestId']
        completed = by_id[request_id]
        assert '[validation ' + case['tag'] + ']' in start['props']['LastUserContent']
        # InferenceTelemetry uses LoggingExtensions.SanitizeForLog, which
        # renders control characters as literal backslash escapes in this log property.
        wire_content = case['turns'][0]['request']['messages'][-1]['content']
        logged_content = log_content(wire_content)
        assert start['props']['LastUserContent'] == logged_content
        assert start['ts'] < completed['ts']
        if iteration + 1 < len(measured): assert completed['ts'] < measured[iteration + 1]['ts']
        props = completed['props']
        metrics = case['turns'][0]['metrics']
        assert props['PromptTokens'] == metrics['prompt_tokens']
        assert props['Tokens'] == metrics['completion_tokens']
        assert props['FinishReason'] == 'eos' and metrics['finish_reason'] == 'stop'
        assert props['AssistantContent'] == log_content(metrics['assistant_message']['content'])
        result.append({'logical_id': case['logical_id'], 'iteration': iteration, 'tag': case['tag'],
                       'request_id': request_id, 'start_utc': start['ts'], 'completed_utc': completed['ts'],
                       'prompt_tokens': props['PromptTokens'], 'completion_tokens': props['Tokens'],
                       'kv_reused_tokens': props['KvReusedTokens'], 'pipeline_ttft_ms': props['TimeToFirstTokenMs'],
                       'pipeline_elapsed_ms': props['ElapsedMs'], 'assistant_content': props['AssistantContent']})
    return {'sources': sources, 'all_24_start_complete_pairs': True, 'warmups': warmup_pairs, 'measured': result,
            'all_measured_kv_zero': all(r['kv_reused_tokens'] == 0 for r in result)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runner-sha', required=True, help='Exact independently reviewed/staged runner SHA256')
    parser.add_argument('--raw', type=Path, default=WORK / 'qwen35-solo72-final-native-r1')
    parser.add_argument('--out', type=Path, default=ROOT / 'docs/validation/deepseek41/json-performance/qwen35-solo72')
    args = parser.parse_args()
    assert re.fullmatch('[0-9a-f]{64}', args.runner_sha)
    runner_path = WORK / 'run-qwen35-solo72.py'
    assert source(runner_path)['sha256'] == args.runner_sha
    M = load('solo72_frozen_runner', runner_path)
    A = load('r2_independent_case_auditor', WORK / 'audit-json-performance-r2.py')
    C = M.load_cross()
    r2 = A.m
    raw = args.raw
    result = read(raw / 'run.json')
    cross = read(WORK / 'qwen35-managed-native-cross-phase-r1/run.json')
    r2root = read(WORK / 'final-json-performance-r2/run.json')
    assert result['runner_source']['sha256'] == args.runner_sha
    for field, path, expected in [('runner_source', runner_path, args.runner_sha),
            ('cross_runner', WORK / 'run-qwen35-cross-phase.py', M.CROSS_RUNNER_SHA),
            ('r2_runner', WORK / 'run-final-json-performance.py', C.R2_RUNNER_SHA),
            ('shared_helper', WORK / 'run-final-existing-models.py', C.HELPER_SHA),
            ('cross_manifest', WORK / 'qwen35-managed-native-cross-phase-r1/run.json', M.CROSS_MANIFEST_SHA),
            ('r2_manifest', WORK / 'final-json-performance-r2/run.json', C.R2_MANIFEST_SHA)]:
        assert bound(path, result[field])['sha256'] == expected
    assert result['planned_versions'] == list(M.VERSIONS) and result['expected_cases'] == 72
    assert result['excluded_decode512_warmups'] == 4 and result['excluded_json_warmups'] == 20
    assert result['dotnet_executable'] == cross['dotnet_executable'] and result['dotnet_changed'] is False
    assert result['weights']['sha256'] == C.MODEL_SHA
    assert not result['original_r2_changed_files'] and not result['cross_host_changed_files']
    assert len(result['runs']) == 4
    for name, sha in result['harness_sha256'].items():
        assert source(ROOT / 'benchmarks/engine_comparison' / name)['sha256'] == sha == C.HARNESS_SHA[name]
    reports, rows, identities, failed = [], [], [], []
    for i, (entry, version) in enumerate(zip(result['runs'], M.VERSIONS)):
        label = 'job' + str(i) + '-' + version
        assert entry['job'] == i and entry['version'] == version
        child_path = raw / (label + '.json')
        bound(child_path, entry['source'])
        d = read(child_path)
        assert M.complete(d) and entry['complete']
        assert d['job'] == i and d['version'] == version and d['cross'] == {'managed': version, 'native': 'final'}
        dep = result['deployments'][version]
        assert dep == cross['deployments'][version + '-managed_final-native']
        assert dep['expected_core_binaries'] == d['binary_sha256']
        assert d['binary_sha256']['libGgmlOps.so'] == r2root['deployments']['final']['expected_core_binaries']['libGgmlOps.so']
        assert all(dep['files_sha256'][k] == v for k, v in d['binary_sha256'].items())
        assert d['deployment_changed_files'] == [] and d.get('unexpected_native_phase_lines') == []
        assert d['finalization_errors'] == [] and d['owned_process_exit_observed'] is True
        assert d['sampling'] == r2.validation.SAMPLING and d['thinking'] is False and d['stream'] is True
        assert d['profile'] == r2.common.PROFILE and d['environment'] == r2.common.ENV
        assert d['runtime_environment_policy']['stripped_prefixes'] == list(M.ENV_PREFIXES)
        assert d['runtime_environment_policy']['deliberate_DOTNET_COMPlus_CORECLR_settings'] == {}
        assert d['launch'] == r2.common.launch_command(Path(dep['target']), r2.common.MODELS['qwen35'])
        assert d['weights'] == result['weights'] and d['weights_id'] == C.MODEL_SHA and d['harness_sha256'] == result['harness_sha256']
        ref_path = WORK / 'final-json-performance-r2' / ('qwen35-' + version + '.json')
        bound(ref_path, d['historical_reference'])
        ref = read(ref_path)
        references = {c['tag']: c for c in ref['cases']}
        expected_ids = [label + '-iteration' + str(n).zfill(2) for n in range(18)]
        assert [c['logical_id'] for c in d['cases']] == expected_ids
        start, end = d['timed_monotonic_interval']
        assert math.isfinite(start) and math.isfinite(end) and start < end
        for n, c in enumerate(d['cases']):
            A.check_case(c)
            assert c['job'] == i and c['iteration'] == n and c['cycle'] == n // 3 and c['repeat'] == n % 3 and c['concurrency'] == 1
            assert c['tag'] == 'json-c1-r' + str(n % 3) + '-i0' and c['original_request_matches'] is True
            assert r2.request_identity(c) == r2.request_identity(references[c['tag']])
            metrics = c['turns'][0]['metrics']
            assert start <= metrics['t_start_abs'] <= metrics['t_first_abs'] <= metrics['t_last_abs'] <= metrics['t_end_abs'] <= end
            if n: assert d['cases'][n - 1]['turns'][0]['metrics']['t_end_abs'] <= metrics['t_start_abs']
            identities.append({'logical_id': c['logical_id'], 'tag': c['tag'], 'initial_sha256': c['input_sha256'],
                               'http_request_sha256': r2.validation.digest(c['turns'][0]['request']),
                               'status': c['status'], 'output_matches_r2': r2.output_identity(c) == r2.output_identity(references[c['tag']]),
                               'prompt_tokens': metrics['prompt_tokens'], 'completion_tokens': metrics['completion_tokens']})
            if c['status'] != 'ok': failed.append(c)
        assert len(d['waves']) == 18
        for n, (c, wave) in enumerate(zip(d['cases'], d['waves'])):
            assert wave['job'] == i and wave['iteration'] == n and wave['cycle'] == n // 3
            assert wave['logical_id'] == c['logical_id'] and wave['concurrency'] == 1 and wave['repeat'] == n % 3
            assert wave['generated_tokens'] == c['turns'][0]['metrics']['completion_tokens']
            assert wave['all_passed'] == (c['status'] == 'ok') and wave['wall_ms'] == c['wave_wall_ms']
            assert wave['wall_ms'] >= c['turns'][0]['metrics']['total_wall_ms']
        assert len(d['warmups']) == 5
        for c, old in zip(d['warmups'], ref['warmups']):
            A.check_case(c)
            assert r2.request_identity(c) == r2.request_identity(old)
            assert c['turns'][0]['metrics']['t_end_abs'] <= start
        warm = d['decode512_warmup']
        assert r2.request_identity(warm) == r2.request_identity(ref['decode512_warmup'])
        assert warm['turns'][0]['metrics']['completion_tokens'] == 512
        assert warm['turns'][0]['metrics']['t_end_abs'] <= d['warmups'][0]['turns'][0]['metrics']['t_start_abs']
        mapping = d['native_mapping_observation']
        assert mapping['library_sha256'] == d['binary_sha256']['libGgmlOps.so']
        assert mapping['selected_library_paths'] == [str(Path(dep['target']) / 'libGgmlOps.so')]
        assert {line.split(maxsplit=5)[5] for line in mapping['mapping_lines']} == set(mapping['selected_library_paths'])
        log = raw / (label + '-server.log')
        bound(log, d['server_log'])
        assert '[phase]' not in log.read_text()
        correlation = server_correlations(raw, d)
        summaries = {name: summarize(cases) for name, cases in [('all18', d['cases']), ('first3', d['cases'][:3]), ('later15', d['cases'][3:])]}
        assert summaries == d['descriptive_groups']
        q = telemetry(r2, raw, label, d, mapping['pid'])
        after = {int(s.split()[0]) for s in d['safe_after_snapshot']['processes'].splitlines()[1:] if s.strip()}
        assert mapping['pid'] not in after
        expected_status = 'ok' if M.complete(d) and d['warmup_complete'] and not d.get('error') and all(c['status'] == 'ok' for c in d['cases']) else 'fail'
        assert d['status'] == entry['status'] == expected_status
        assert entry['failed_logical_ids'] == [c['logical_id'] for c in d['cases'] if c['status'] != 'ok']
        rows.append({'job': i, 'version': version, 'source': source(child_path), 'binary_sha256': d['binary_sha256'],
                     'status': d['status'], 'native_mapping_observation': mapping, 'groups': summaries, 'server_correlation': correlation,
                     'telemetry': q, 'whole_request_interval_seconds': end - start,
                     'warmup_statuses': [warm['status']] + [c['status'] for c in d['warmups']],
                     'warmup_outputs_match_reference': [r2.output_identity(warm) == r2.output_identity(ref['decode512_warmup'])] + [r2.output_identity(c) == r2.output_identity(old) for c, old in zip(d['warmups'], ref['warmups'])]})
        reports.append(d)
    assert len(identities) == len({r['logical_id'] for r in identities}) == 72
    assert len({r['native_mapping_observation']['pid'] for r in rows}) == 4
    comparisons = [M.compare(r2, reports[0], reports[1]), M.compare(r2, reports[3], reports[2])]
    assert comparisons == result['comparisons']
    for comp in comparisons:
        if comp['whole18_comparable']:
            b, f = comp['summaries']['baseline']['all18'], comp['summaries']['final']['all18']
            assert comp['whole18_speedup_final_over_baseline'] == {k: b[k]['median'] / f[k]['median'] for k in ('ttft_ms', 'total_wall_ms', 'wave_wall_ms')}
            assert len(comp['case_pairs']) == 18 and all(c['comparable'] for c in comp['case_pairs'])
        else: assert comp['whole18_speedup_final_over_baseline'] == {}
    assert result['run_complete'] == all(M.complete(d) for d in reports)
    assert result['all_cases_passed'] == all(d['status'] == 'ok' for d in reports)
    assert result['all_pairs_comparable'] == all(c['whole18_comparable'] for c in comparisons)
    audit = {'scope': 'Independent offline audit of all 72 logical iterations and 24 separate warmups. Repeated wire tags remain unchanged; per-occurrence server RequestId correlation verifies usage and KV reuse. Whole-18 comparisons retain every observation; first3/later15 are descriptive only.',
             'integrity_valid': True, 'run_complete': result['run_complete'], 'all_cases_passed': result['all_cases_passed'],
             'all_pairs_comparable': result['all_pairs_comparable'], 'analysis_source': source(Path(__file__)),
             'runner_source': source(runner_path), 'case_auditor_source': source(WORK / 'audit-json-performance-r2.py'),
             'run_source': source(raw / 'run.json'),
             'log_representation_sources': [source(ROOT / p) for p in ('TensorSharp.Chat/InferenceTelemetry.cs', 'TensorSharp.Runtime.Logging/LoggingExtensions.cs', 'TensorSharp.Chat/ProtocolAdapters/FinishReasonMapper.cs')],
             'rows': rows, 'case_identities': identities,
             'failed_cases': failed, 'comparisons': comparisons,
             'all_measured_kv_zero': all(r['server_correlation']['all_measured_kv_zero'] for r in rows),
             'sources': [source(p) for p in sorted(raw.rglob('*')) if p.is_file()],
             'limits': 'No new VM reads or binary/model rehash. Producer frozen-file verification and actual pre-warmup mapping are checked against retained evidence. Periodic resource sampling cannot exclude every transient. Runtime/native differences from other experiments are preserved; descriptive subgroups are not promoted to isolated fixes.'}
    args.out.mkdir(parents=True, exist_ok=True)
    for p in raw.rglob('*'):
        if not p.is_file(): continue
        assert p.suffix not in ('.nettrace', '.etlx'), 'Unexpected instrumentation artifact'
        q = args.out / p.relative_to(raw); q.parent.mkdir(parents=True, exist_ok=True)
        if q.exists(): assert q.read_bytes() == p.read_bytes()
        else: q.write_bytes(p.read_bytes())
    (args.out / 'audit.json').write_text(json.dumps(audit, indent=2) + '\n')
    print(json.dumps({'cases': 72, 'failed_cases': len(failed), 'all_pairs_comparable': audit['all_pairs_comparable'],
                      'all_measured_kv_zero': audit['all_measured_kv_zero']}, indent=2))


if __name__ == '__main__':
    main()
