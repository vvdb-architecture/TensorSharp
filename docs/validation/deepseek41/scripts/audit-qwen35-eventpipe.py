#!/usr/bin/env python3
"""Offline request/identity/capture audit; raw EventPipe parsing is separate."""
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
RUNNER_SHA = 'd4cf63ed593fde16596b433c488470704ccaa9d2180a949b12625e07eff5492f'
sys.path.insert(0, str(ROOT / 'benchmarks/engine_comparison'))


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


A = load('r2_case_auditor', WORK / 'audit-json-performance-r2.py')
E = load('eventpipe_runner', WORK / 'run-qwen35-eventpipe.py')
C = load('cross_runner', WORK / 'run-qwen35-cross-phase.py')


def source(path):
    data = path.read_bytes()
    return {'path': str(path), 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}


def read(path):
    return json.loads(path.read_text())


def exact(path, recorded):
    actual = source(path)
    assert all(actual[k] == recorded[k] for k in ('sha256', 'bytes')), str(path)
    return actual


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--raw', type=Path, default=WORK / 'qwen35-eventpipe-final-native-r1')
    parser.add_argument('--out', type=Path, default=ROOT / 'docs/validation/deepseek41/json-performance/qwen35-eventpipe')
    args = parser.parse_args()
    raw, out = args.raw, args.out
    manifest = read(raw / 'run.json')
    prior = read(WORK / 'qwen35-managed-native-cross-phase-r1/run.json')
    assert manifest['run_complete'] and manifest['all_cases_passed'] and manifest['expected_cases'] == 30
    assert manifest['diagnostic_only'] and manifest['performance_qualified'] is False
    assert manifest['planned_jobs'] == list(E.JOBS) and len(manifest['runs']) == 2
    assert not any(manifest[k] for k in ('original_r2_changed_files', 'cross_host_changed_files', 'tool_changed_files'))
    assert manifest['excluded_decode512_warmups'] == 2 and manifest['excluded_json_warmups'] == 10
    assert manifest['profiles'] == E.PROFILES and manifest['providers'] == E.PROVIDERS
    pins = [('runner_source', 'run-qwen35-eventpipe.py', RUNNER_SHA),
            ('cross_manifest', 'qwen35-managed-native-cross-phase-r1/run.json', E.CROSS_MANIFEST_SHA),
            ('cross_runner', 'run-qwen35-cross-phase.py', E.CROSS_RUNNER_SHA),
            ('r2_runner', 'run-final-json-performance.py', C.R2_RUNNER_SHA),
            ('shared_helper', 'run-final-existing-models.py', C.HELPER_SHA),
            ('r2_manifest', 'final-json-performance-r2/run.json', C.R2_MANIFEST_SHA),
            ('tool_evidence', 'qwen35-eventpipe-tool-evidence.json', E.TOOL_EVIDENCE_SHA)]
    for field, local, expected in pins:
        assert exact(WORK / local, manifest[field])['sha256'] == expected
    for name, sha in manifest['harness_sha256'].items():
        assert source(ROOT / 'benchmarks/engine_comparison' / name)['sha256'] == sha == C.HARNESS_SHA[name]
    tool = read(WORK / 'qwen35-eventpipe-tool-evidence.json')
    assert manifest['trace_tool'] == tool and tool['version'] == '10.0.745401'
    assert len(tool['tool_files']) == 54 and len(tool['help_sources']) == 5
    assert len({r['path'] for r in tool['tool_files'] + tool['help_sources']}) == 59
    assert manifest['weights']['sha256'] == C.MODEL_SHA
    rows, case_keys = [], []
    retained_paths = {raw / 'run.json'}
    raw_trace_paths = set()
    for entry, label in zip(manifest['runs'], E.JOBS):
        assert entry['version'] == label and entry['diagnostic_complete'] and entry['status'] == 'ok' and entry['failed_cases'] == []
        prefix = 'qwen35-' + label
        child_path = raw / (prefix + '.json')
        exact(child_path, entry['source'])
        child = read(child_path)
        assert all(child[k] for k in ('run_complete', 'diagnostic_complete', 'eventpipe_complete', 'warmup_coverage_ok', 'native_phase_capture_ok'))
        assert A.m.complete(child) and child['status'] == 'ok' and not child.get('error')
        assert child['diagnostic_only'] and child['performance_qualified'] is False
        assert child['telemetry_qualification']['qualified'] is False and child['deployment_changed_files'] == []
        deployment = manifest['deployments'][label]
        assert deployment == prior['deployments'][label] and deployment['native_version'] == 'final'
        assert child['binary_sha256'] == deployment['expected_core_binaries']
        for binary, sha in child['binary_sha256'].items():
            assert deployment['files_sha256'][binary] == sha
        assert child['environment'] == {**A.m.common.ENV, 'TS_GGML_PHASE_TIMING': '1', 'DOTNET_EventPipeThreadSamplingRate': '1'}
        assert child['sampling'] == A.m.validation.SAMPLING and child['thinking'] is False and child['stream'] is True
        assert child['profile'] == A.m.common.PROFILE and child['harness_sha256'] == manifest['harness_sha256']
        assert child['weights'] == manifest['weights'] and child['weights_id'] == C.MODEL_SHA
        assert child['launch'] == A.m.common.launch_command(Path(deployment['target']), A.m.common.MODELS['qwen35'])
        assert Path(child['logging_environment']['TENSORSHARP_LOG_DIR']).name == prefix + '-file-logs'
        reference = read(WORK / 'final-json-performance-r2' / ('qwen35-' + deployment['managed_version'] + '.json'))
        reference_cases = {A.key(c): c for c in reference['cases']}
        assert child['historical_reference']['sha256'] == source(WORK / 'final-json-performance-r2' / ('qwen35-' + deployment['managed_version'] + '.json'))['sha256']
        for c in child['cases']:
            A.check_case(c)
            assert c['status'] == 'ok' and A.m.request_identity(c) == A.m.request_identity(reference_cases[A.key(c)])
            assert A.m.output_identity(c) == A.m.output_identity(reference_cases[A.key(c)])
            assert c['turns'][0]['metrics']['prompt_tokens'] == 92 and c['turns'][0]['metrics']['completion_tokens'] == 16
            case_keys.append((label, *A.key(c)))
        assert len(child['warmups']) == 5 and len(child['waves']) == 6
        for c, old in zip(child['warmups'], reference['warmups']):
            A.check_case(c)
            assert c['status'] == 'ok' and A.m.request_identity(c) == A.m.request_identity(old) and A.m.output_identity(c) == A.m.output_identity(old)
        warm = child['decode512_warmup']
        assert warm['status'] == 'ok' and warm['turns'][0]['metrics']['completion_tokens'] == 512
        assert A.m.request_identity(warm) == A.m.request_identity(reference['decode512_warmup'])
        assert A.m.output_identity(warm) == A.m.output_identity(reference['decode512_warmup'])
        for wave in child['waves']:
            cases = [c for c in child['cases'] if c['concurrency'] == wave['concurrency'] and c['repeat'] == wave['repeat']]
            assert len(cases) == wave['concurrency'] and wave['all_passed'] and wave['generated_tokens'] == 16 * len(cases)
        trace_path = raw / (prefix + '-eventpipe.json')
        exact(trace_path, child['eventpipe_evidence'])
        trace = read(trace_path)
        assert trace['collection_complete'] and trace['errors'] == [] and trace['collector_exit_code'] == 0 and trace['completed_message_present']
        assert trace['diagnostic_only'] and trace['expected_measured_cases'] == 15 and trace['profiles'] == E.PROFILES
        assert trace['version'] == label and trace['host_sampling_rate_ms'] == 1
        assert trace['actual_case_monotonic_interval'] == child['timed_monotonic_interval']
        expected_command = [tool['tool_executable'], 'collect', '--process-id', str(trace['host_pid']), '--profile', E.PROFILES,
                            '--providers', E.PROVIDERS, '--format', 'NetTrace', '--output', trace['raw_nettrace']['path'], '--buffersize', '128']
        assert trace['command'] == expected_command
        anchors = [trace[k] for k in ('attach_requested', 'collection_ready', 'stop_requested', 'finalized')]
        assert all(math.isfinite(a[f]) for a in anchors for f in ('monotonic', 'unix_seconds', 'anchor_uncertainty_seconds'))
        assert all(a['anchor_uncertainty_seconds'] >= 0 and abs(datetime.fromisoformat(a['utc']).timestamp() - a['unix_seconds']) < .000002 for a in anchors)
        offsets = [a['unix_seconds'] - a['monotonic'] for a in anchors]
        assert max(offsets) - min(offsets) < .001
        start, end = child['timed_monotonic_interval']
        assert anchors[0]['monotonic'] <= anchors[1]['monotonic'] <= start < end <= anchors[2]['monotonic'] <= anchors[3]['monotonic']
        warm_ends = [c['turns'][0]['metrics']['t_end_abs'] for c in [warm] + child['warmups']]
        assert max(warm_ends) <= anchors[0]['monotonic']
        for c in child['cases']:
            m = c['turns'][0]['metrics']
            assert start <= m['t_start_abs'] <= m['t_first_abs'] <= m['t_last_abs'] <= m['t_end_abs'] <= end
        collector_log = raw / (prefix + '-collector.log')
        exact(collector_log, trace['collector_log'])
        collector_text = collector_log.read_text()
        assert 'Trace completed.' in collector_text and trace['raw_nettrace']['path'] in collector_text
        assert '0x000000100003C01D' in collector_text and 'Microsoft-DotNETCore-SampleProfiler' in collector_text
        assert not any(s in collector_text.lower() for s in ('[error]', 'potentially broken', 'lost events', 'trace collection canceled'))
        raw_trace = exact(raw / (prefix + '.nettrace'), trace['raw_nettrace'])
        log = raw / (prefix + '-server.log')
        exact(log, child['server_log'])
        phases_path = raw / (prefix + '-server-phases.json')
        exact(phases_path, child['native_phase_evidence'])
        phases = read(phases_path)
        assert phases['phase_lines'] == C.phase_evidence(A.m, log)['phase_lines'] and phases['qwen35_verify_lines'] > 0
        exact(raw / (prefix + '-telemetry.jsonl'), child['telemetry_source'])
        retained_paths.update((child_path, trace_path, collector_log, log, phases_path, raw / (prefix + '-telemetry.jsonl')))
        raw_trace_paths.add(raw / (prefix + '.nettrace'))
        records = []
        for name, info in child['runtime_file_logs'].items():
            exact(raw / name, info)
            retained_paths.add(raw / name)
            records += [json.loads(s) for s in (raw / name).read_text().splitlines()]
        shutdown = [datetime.fromisoformat(re.sub(r'(\.\d{6})\d+', r'\1', r['ts'])).timestamp() for r in records if r.get('message') == 'Application is shutting down...']
        assert len(shutdown) == 1 and shutdown[0] >= trace['finalized']['unix_seconds']
        after_pids = {int(line.split()[0]) for line in child['safe_after_snapshot']['processes'].splitlines()[1:] if line.strip()}
        assert trace['host_pid'] not in after_pids and trace['collector_pid'] not in after_pids
        groups = {}
        for degree in (1, 4):
            metrics = [c['turns'][0]['metrics'] for c in child['cases'] if c['concurrency'] == degree]
            groups[str(degree)] = {field: {'values': [m[field] for m in metrics], 'median': statistics.median(m[field] for m in metrics)}
                                   for field in ('ttft_ms', 'total_wall_ms', 'decode_tps')}
            groups[str(degree)]['whole_wave_wall_ms'] = [w['wall_ms'] for w in child['waves'] if w['concurrency'] == degree]
        rows.append({'label': label, 'source': source(child_path), 'binary_sha256': child['binary_sha256'],
                     'all_15_cases_and_six_warmups_match_reference': True, 'groups': groups,
                     'collection_source': source(trace_path), 'raw_trace_source': raw_trace,
                     'collection_anchors': {k: trace[k] for k in ('attach_requested', 'collection_ready', 'stop_requested', 'finalized')},
                     'actual_case_monotonic_interval': [start, end], 'last_warmup_end_monotonic': max(warm_ends),
                     'shutdown_unix_seconds': shutdown[0], 'owned_host_and_collector_absent_after': True,
                     'request_interval_seconds': end - start, 'collector_stop_to_finalize_ms': (anchors[3]['monotonic'] - anchors[2]['monotonic']) * 1000,
                     'telemetry_qualification': child['telemetry_qualification'], 'raw_trace_parser_integrity_scope': trace['trace_integrity']})
    assert len(case_keys) == len(set(case_keys)) == 30
    result = {'scope': 'Independent local audit of all 30 exact requests/responses, 12 warmups, recorded frozen deployments, tool/source hashes and collection anchors. No VM rehash, raw trace parse or performance qualification.',
              'integrity_valid': True, 'run_complete': True, 'all_30_cases_passed': True, 'performance_qualified': False,
              'analysis_source': source(Path(__file__)), 'case_auditor_source': source(WORK / 'audit-json-performance-r2.py'),
              'run_source': source(raw / 'run.json'), 'tool_evidence_source': source(WORK / 'qwen35-eventpipe-tool-evidence.json'),
              'rows': rows, 'sources': [source(p) for p in sorted(retained_paths | raw_trace_paths)],
              'identity_scope': 'Producer verified every frozen host/tool file. This audit hashes local pulled evidence and raw nettrace files, checks manifests and CLI output, and does not reread VM binaries. Model hash reuse is explicitly recorded in the producer manifest.',
              'trace_scope': 'Warmups end before attach; all measured client intervals lie after readiness and before stop; collector finalization precedes host shutdown. Raw parser/event loss/stack integrity and EventPipe-to-wall-clock coverage are a separate audit.'}
    out.mkdir(parents=True, exist_ok=True)
    for p in sorted(retained_paths):
        q = out / p.relative_to(raw)
        q.parent.mkdir(parents=True, exist_ok=True)
        if q.exists():
            assert q.read_bytes() == p.read_bytes()
        else:
            q.write_bytes(p.read_bytes())
    (out / 'audit.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'cases': 30, 'warmups': 12, 'rows': [{'label': r['label'], 'seconds': r['request_interval_seconds'], 'c1_ttft_ms': r['groups']['1']['ttft_ms']['median']} for r in rows]}, indent=2))


if __name__ == '__main__':
    main()
