#!/usr/bin/env python3
"""Offline serial-only phase correlation for the two profiled diagnostic jobs."""
import hashlib
import json
from pathlib import Path
import re
import statistics

RAW = Path('/tmp/deepseek41-reference/qwen35-eventpipe-final-native-r1')
OUT = Path('/tmp/deepseek41-reference/qwen35-eventpipe-phase-analysis')


def source(p):
    data = p.read_bytes()
    return {'path': str(p), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def local_source(reference):
    path = RAW / Path(reference['path']).name
    actual = source(path)
    assert actual['sha256'] == reference['sha256'] and actual['bytes'] == reference['bytes']
    return path


manifest = json.loads((RAW / 'run.json').read_text())
assert manifest['run_complete'] and manifest['all_cases_passed'] and len(manifest['runs']) == 2
assert manifest['diagnostic_only'] and not manifest['performance_qualified']
runs = []
for entry in manifest['runs']:
    path = local_source(entry['source'])
    report = json.loads(path.read_text())
    log = local_source(report['server_log'])
    lines = log.read_text().splitlines()
    evidence = json.loads(local_source(report['native_phase_evidence']).read_text())
    assert len(report['cases']) == 15 and all(c['status'] == 'ok' for c in report['cases'])
    assert {(c['tag'], c['concurrency'], c['repeat']) for c in report['cases']} == {
        (f'json-c{degree}-r{repeat}-i{i}', degree, repeat)
        for degree in (1, 4) for repeat in range(3) for i in range(degree)}
    trace_path = local_source(report['eventpipe_evidence'])
    trace = json.loads(trace_path.read_text())
    assert trace['collection_complete'] and not trace['errors']
    assert trace['actual_case_monotonic_interval'] == report['timed_monotonic_interval']
    assert (trace['collection_ready']['monotonic'] <= report['timed_monotonic_interval'][0]
            <= report['timed_monotonic_interval'][1] <= trace['stop_requested']['monotonic'])
    filelog = RAW / next(iter(report['runtime_file_logs']))
    filelog_reference = next(iter(report['runtime_file_logs'].values()))
    assert source(filelog)['sha256'] == filelog_reference['sha256']
    logrecords = [json.loads(line) for line in filelog.read_text().splitlines()]
    modelservice = [r for r in logrecords if r.get('category') == 'TensorSharp.Server.ModelService']
    solo = []
    for case in report['cases']:
        if case['concurrency'] != 1:
            continue
        matches = [i for i, line in enumerate(lines) if 'chat.start arch=qwen35' in line and '[validation ' + case['tag'] + ']' in line]
        assert len(matches) == 1
        start = matches[0]
        following_starts = [i for i in range(start + 1, len(lines)) if 'chat.start arch=qwen35' in lines[i]]
        limit = min(following_starts) if following_starts else len(lines)
        finishes = [i for i in range(start + 1, limit) if 'chat.complete tokens=' in lines[i]]
        assert len(finishes) == 1
        finish = finishes[0]
        phases = [(i, lines[i]) for i in range(start + 1, finish) if '[phase] Qwen3.5 model verify ' in lines[i]]
        assert len(phases) == 1
        phase_line, phase_text = phases[0]
        fields = {k: float(v) for k, v in re.findall(r'([\w+_-]+)=([0-9.]+)(?:ms)?', phase_text)}
        assert set(fields) == {'total', 'build', 'bind', 'optimize', 'alloc', 'upload', 'submit', 'compute+download'}
        assert abs(fields['total'] - sum(v for k, v in fields.items() if k != 'total')) <= .05
        starts = [r for r in modelservice if r.get('event', {}).get('name') == 'ChatStarted'
                  and '[validation ' + case['tag'] + ']' in r['props']['LastUserContent']]
        assert len(starts) == 1
        request_id = starts[0]['scope']['RequestId']
        completes = [r for r in modelservice if r.get('event', {}).get('name') == 'ChatCompleted'
                     and r['scope']['RequestId'] == request_id]
        assert len(completes) == 1
        props = completes[0]['props']
        metrics = case['turns'][0]['metrics']
        assert props['PromptTokens'] == metrics['prompt_tokens'] == 92
        assert props['Tokens'] == metrics['completion_tokens'] == 16
        assert props['KvReusedTokens'] == 0
        assert props['AssistantContent'] == metrics['assistant_message']['content']
        solo.append({'tag': case['tag'], 'request_id': request_id, 'input_sha256': case['input_sha256'],
                     'assistant_content': props['AssistantContent'], 'prompt_tokens': 92, 'completion_tokens': 16,
                     'kv_reused_tokens': 0, 'stdout_lines': {'start': start + 1, 'phase': phase_line + 1, 'complete': finish + 1},
                     'phase_text': phase_text, 'phase_ms': fields,
                     'pipeline_ttft_ms': props['TimeToFirstTokenMs'], 'pipeline_elapsed_ms': props['ElapsedMs'],
                     'client_ttft_ms': metrics['ttft_ms'], 'client_elapsed_ms': metrics['total_wall_ms'],
                     'pipeline_ttft_minus_native_total_ms': props['TimeToFirstTokenMs'] - fields['total']})
    assert len(solo) == 3
    medians = {k: statistics.median(r[k] for r in solo) for k in
               ('pipeline_ttft_ms', 'pipeline_elapsed_ms', 'client_ttft_ms', 'client_elapsed_ms',
                'pipeline_ttft_minus_native_total_ms')}
    medians['phase_ms'] = {k: statistics.median(r['phase_ms'][k] for r in solo) for k in solo[0]['phase_ms']}
    runs.append({'cross': report['cross'], 'report_source': source(path), 'stdout_source': source(log),
                 'file_log_source': source(filelog), 'binary_sha256': report['binary_sha256'],
                 'collector_evidence_source': source(trace_path),
                 'actual_case_monotonic_interval': report['timed_monotonic_interval'],
                 'phase_lines_all_warmup_and_requests': len(evidence['phase_lines']), 'solo_cases': solo, 'solo_medians': medians})

by_cross = {(r['cross']['managed'], r['cross']['native']): r for r in runs}
effects = []
for native in ('final',):
    old, new = [by_cross[(managed, native)]['solo_medians'] for managed in ('baseline', 'final')]
    effects.append({'held_native': native, 'managed_final_minus_baseline_ms':
                    {k: new[k] - old[k] for k in ('client_ttft_ms', 'pipeline_ttft_ms', 'client_elapsed_ms',
                                                 'pipeline_ttft_minus_native_total_ms')},
                    'native_phase_total_median_delta_ms': new['phase_ms']['total'] - old['phase_ms']['total']})
result = {'diagnostic_only': True, 'performance_qualified': False, 'manifest_source': source(RAW / 'run.json'),
          'analyzer_source': source(Path(__file__)), 'all_measured_cases_passed': 30,
          'solo_correlated_cases': 6, 'runs': runs, 'managed_effects_at_fixed_native': effects,
          'finding': 'The earlier solo first-token gap does not persist at the median under EventPipe: client73.54 to71.84ms and pipeline63 to64ms. Native verify rises27.99 to30.42ms; wall133.53 to137.46ms. This profiled run can expose mechanisms and outliers, but cannot establish a cause or resolution for the earlier qualified latency regression that it does not reproduce.',
          'limits': 'Only3 solo requests/job, fixed order,1ms sampled-thread tracing plus native phase timing: diagnostic only. Baseline r2 contains a23.03ms native bind; final r1 has98ms pipeline TTFT with30.42ms native total. The pipeline-minus-native difference is an unresolved residual, not a separately instrumented grammar/scheduler/GC measurement. Asynchronous timing boundaries can overlap. Concurrent phases remain aggregate. Raw trace parse/loss/time-range analysis is owned independently and is not replaced by this log correlation.',
          'identity_scope': 'Same immutable final-native hosts from the preceding crossing; this analysis checks retained manifest/child/log hashes and does not independently inspect process mappings.'}
OUT.mkdir(exist_ok=True)
(OUT / 'analysis.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({'medians': [{'cross': r['cross'], **r['solo_medians']} for r in runs], 'effects': effects}, indent=2))
