#!/usr/bin/env python3
"""Offline exact repeated-request correlation against structured and stdout logs."""
import hashlib
import json
from pathlib import Path
import re
import statistics

RAW = Path('/tmp/deepseek41-reference/qwen35-solo72-final-native-r1')
OUT = Path('/tmp/deepseek41-reference/qwen35-solo72-analysis')


def source(path):
    data = path.read_bytes()
    return {'path': str(path), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def local(reference, relative=None):
    path = RAW / (relative or Path(reference['path']).name)
    observed = source(path)
    assert observed['sha256'] == reference['sha256'] and observed['bytes'] == reference['bytes']
    return path


manifest = json.loads((RAW / 'run.json').read_text())
assert manifest['run_complete'] and manifest['all_cases_passed'] and manifest['all_pairs_comparable']
assert len(manifest['runs']) == 4 and manifest['expected_cases'] == 72
assert manifest['runner_source']['sha256'] == '344382bbf8b4eb1170886c3cafbad5c0774544e18ade9a0485a06e85950432f4'
results = []
for entry in manifest['runs']:
    report_path = local(entry['source'])
    report = json.loads(report_path.read_text())
    stdout_path = local(report['server_log'])
    assert report['run_complete'] and report['status'] == 'ok' and report['telemetry_qualification']['qualified']
    assert len(report['runtime_file_logs']) == 1
    file_reference = report['runtime_file_logs'][0]
    file_path = local(file_reference, Path(file_reference['path']).parent.name + '/' + Path(file_reference['path']).name)
    records = []
    for line, text in enumerate(file_path.read_text().splitlines(), 1):
        row = json.loads(text)
        if row.get('category') == 'TensorSharp.Server.ModelService':
            records.append((line, row))
    starts = [(line, row) for line, row in records if row.get('event', {}).get('name') == 'ChatStarted'
               and '[validation json-c1-r' in row['props']['LastUserContent']]
    assert len(starts) == 18
    stdout = stdout_path.read_text().splitlines()
    assert not any('[phase]' in line for line in stdout)
    stdout_starts = [i for i, text in enumerate(stdout) if 'chat.start arch=qwen35' in text and '[validation json-c1-r' in text]
    assert len(stdout_starts) == 18
    cases = []
    for iteration, case in enumerate(report['cases']):
        assert case['iteration'] == iteration and case['original_request_matches'] is True
        line, start = starts[iteration]
        tag = re.search(r'\[validation ([^]]+)\]', start['props']['LastUserContent']).group(1)
        assert tag == case['tag'] == 'json-c1-r' + str(iteration % 3) + '-i0'
        request_id = start['scope']['RequestId']
        matches = [(line, row) for line, row in records if row.get('event', {}).get('name') == 'ChatCompleted'
                   and row['scope']['RequestId'] == request_id]
        assert len(matches) == 1
        finish_line, finish = matches[0]
        assert line < finish_line and (iteration == 17 or finish_line < starts[iteration + 1][0])
        props = finish['props']
        metrics = case['turns'][0]['metrics']
        assert props['PromptTokens'] == metrics['prompt_tokens'] == 92
        assert props['Tokens'] == metrics['completion_tokens'] == 16
        assert props['KvReusedTokens'] == 0
        assert props['AssistantContent'] == metrics['assistant_message']['content'] == '{"name":"Mars","moons":2,"habitable":false}'
        stdout_start = stdout_starts[iteration]
        stdout_end = stdout_starts[iteration + 1] if iteration < 17 else len(stdout)
        stdout_finishes = [(i, stdout[i]) for i in range(stdout_start + 1, stdout_end) if 'chat.complete tokens=' in stdout[i]]
        assert len(stdout_finishes) == 1
        stdout_finish, completion_text = stdout_finishes[0]
        printed = re.search(r'ttftMs=(\d+) elapsedMs=([\d.]+)', completion_text)
        assert printed and int(printed.group(1)) == props['TimeToFirstTokenMs']
        assert abs(float(printed.group(2)) - props['ElapsedMs']) <= .050001
        request_bytes = json.dumps(case['turns'][0]['request'], sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode()
        cases.append({'logical_id': case['logical_id'], 'iteration': iteration, 'cycle': case['cycle'], 'tag': tag,
                      'request_id': request_id, 'input_sha256': case['input_sha256'],
                      'canonical_http_request_sha256': hashlib.sha256(request_bytes).hexdigest(),
                      'structured_lines': {'start': line, 'complete': finish_line},
                      'stdout_lines': {'start': stdout_start + 1, 'complete': stdout_finish + 1},
                      'start_utc': start['ts'], 'complete_utc': finish['ts'],
                      'pipeline_ttft_ms': props['TimeToFirstTokenMs'], 'pipeline_elapsed_ms': props['ElapsedMs'],
                      'client_ttft_ms': metrics['ttft_ms'], 'client_elapsed_ms': metrics['total_wall_ms'],
                      'wave_wall_ms': case['wave_wall_ms'], 'prompt_tokens': 92, 'completion_tokens': 16,
                      'kv_reused_tokens': 0, 'assistant_content_sha256': hashlib.sha256(props['AssistantContent'].encode()).hexdigest()})
    assert len({c['logical_id'] for c in cases}) == len({c['request_id'] for c in cases}) == 18
    for repeat in range(3):
        assert len({c['canonical_http_request_sha256'] for c in cases if c['iteration'] % 3 == repeat}) == 1
    summaries = {}
    for group, rows in [('all18', cases), ('first3', cases[:3]), ('later15', cases[3:])]:
        summaries[group] = {'cases': len(rows), 'scope': 'All observations retained; subgroup is descriptive only.',
                            **{field: {'values': [c[field] for c in rows], 'median': statistics.median(c[field] for c in rows)}
                               for field in ('pipeline_ttft_ms', 'pipeline_elapsed_ms', 'client_ttft_ms', 'client_elapsed_ms', 'wave_wall_ms')}}
    results.append({'job': report['job'], 'cross': report['cross'], 'report_source': source(report_path),
                    'file_log_source': source(file_path), 'stdout_source': source(stdout_path),
                    'binary_sha256': report['binary_sha256'], 'cases': cases, 'summaries': summaries})

comparisons = []
for baseline_job, final_job in [(0, 1), (3, 2)]:
    baseline, final = results[baseline_job], results[final_job]
    assert all(a['canonical_http_request_sha256'] == b['canonical_http_request_sha256']
               for a, b in zip(baseline['cases'], final['cases']))
    comparison = {'baseline_job': baseline_job, 'final_job': final_job, 'groups': {}}
    for group in ('all18', 'first3', 'later15'):
        comparison['groups'][group] = {field: {'baseline_median': baseline['summaries'][group][field]['median'],
                                               'final_median': final['summaries'][group][field]['median'],
                                               'delta_final_minus_baseline_ms': final['summaries'][group][field]['median'] - baseline['summaries'][group][field]['median']}
                                        for field in ('pipeline_ttft_ms', 'pipeline_elapsed_ms', 'client_ttft_ms', 'client_elapsed_ms', 'wave_wall_ms')}
    comparisons.append(comparison)
result = {'manifest_source': source(RAW / 'run.json'), 'analyzer_source': source(Path(__file__)),
          'matched_cases': 72, 'qualified_whole18_pairs': 2, 'native_phase_lines': 0,
          'findings': 'The earlier approximately15ms client first-token increase is absent in both uninstrumented full18-case fixed-native comparisons. All72 requests retain exact R2 payloads,92prompt/16completion/zeroKVreuse and identical correct content. This broader control is separate evidence and does not erase the earlier measured slower runs or establish a source-level fix.',
          'limits': 'Four fresh processes with final native held fixed; no new production patch. First3/later15 are predeclared descriptive groups, never selected successes or outliers. No native phase instrumentation was enabled, so pipeline timing cannot be attributed to native, grammar, reset, GC or scheduling internals.',
          'runs': results, 'comparisons': comparisons}
OUT.mkdir(exist_ok=True)
(OUT / 'analysis.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(comparisons, indent=2))
