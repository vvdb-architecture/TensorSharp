import copy
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import types
from unittest.mock import patch

sys.path.insert(0, '/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison')
path = Path('/tmp/deepseek41-reference/run-final-json-performance.py')
spec = importlib.util.spec_from_file_location('json_performance', path)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
checks = []
state = {'version': 'baseline', 'change': False}


def engine(url, model, **request):
    tag = request['messages'][0]['content'].split(']')[0].split('[validation ')[-1]
    decode = tag == 'warmup-decode'
    message = ('hash collision ' * 40) if decode else '{"name":"Mars","moons":2,"habitable":false}'
    tokens = 512 if decode else 24
    if state['change'] and state['version'] == 'final' and tag == 'json-c4-r1-i2':
        message = '{"habitable":false,"moons":2,"name":"Mars"}'
    assert request['extra_body'] == {**m.validation.SAMPLING, **m.validation.engines.thinking_body('tensorsharp', False)}
    if not decode:
        assert request['response_format'] == {'type': 'json_object'}
    return {'assistant_message': {'role': 'assistant', 'content': message}, 'finish_reason': 'length' if decode else 'stop',
            'usage_present': True, 'prompt_tokens': 50, 'completion_tokens': tokens, 'ttft_ms': 10, 'decode_tps': 100,
            'total_wall_ms': 300, 'decode_timing_source': 'stream_window', 't_first_abs': 1, 't_last_abs': 1.23,
            'prefill_tps': 5000, 'output_text': message}


class Socket:
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def connect_ex(self, *args): return 1


class Process:
    next_pid = 10000
    def __init__(self, command, **kwargs):
        self.pid = Process.next_pid
        Process.next_pid += 1
        self.stopped = False
        state['version'] = Path(command[1]).parents[1].name
        file_logs = Path(kwargs['env']['TENSORSHARP_LOG_DIR'])
        assert Path(kwargs['cwd']).resolve() not in file_logs.resolve().parents
        file_logs.mkdir(parents=True)
        (file_logs / 'tensorsharp-server-20260911.jsonl').write_text('actual simulated runtime log write\n')
    def poll(self): return 0 if self.stopped else None
    def wait(self, **kwargs): self.stopped = True; return 0


class Monitor:
    def __init__(self, path): self.path = path; self.samples = []; self.errors = []
    def start(self): self.path.write_text('{"simulated":true}\n')
    def stop(self): pass


qualified = {'qualified': True, 'gpu7': {'clocks.sm': {'median': 1740}, 'clocks.mem': {'median': 7251}}}
with tempfile.TemporaryDirectory() as directory:
    work = Path(directory)
    hosts = {v: work / ('source-' + v) for v in ('baseline', 'final')}
    binaries = {}
    for version, host in hosts.items():
        host.mkdir()
        for name in m.BINARY_NAMES: (host / name).write_bytes((version + name).encode())
        (host / 'appsettings.json').write_text('{\"Logging\":{}}')
        (host / 'logs').mkdir()
        (host / 'logs/tensorsharp-server-20260911.jsonl').write_text('inherited frozen log\n')
        binaries[version] = {name: m.common.file_sha(host / name) for name in m.BINARY_NAMES}
    original = {str(p): p.read_bytes() for host in hosts.values() for p in host.rglob('*') if p.is_file()}
    weights = {name: work / (name + '.gguf') for name in m.NAMES}
    for weight in weights.values(): weight.write_bytes(b'same weights')
    old = work / 'old'; old.mkdir()
    preceding = work / 'preceding'; preceding.mkdir()
    quality = {'run_complete': True, 'expected_quality_cases': 75, 'expected_unicode_cases': 15, 'planned_models': list(m.NAMES), 'runs': []}
    with patch.object(m.validation.engines, 'run_openai_chat', side_effect=engine):
        for name in m.NAMES:
            cases = []
            for scenario, tag, degree, repeat in sorted(m.planned_keys()):
                case = m.validation.run_case('http://fake', name, 'tensorsharp', scenario, tag)
                case.update(concurrency=degree, repeat=repeat)
                cases.append(case)
            m.write(old / (name + '-tensorsharp.json'), {'cases': cases, 'weights_id': m.common.file_sha(weights[name]), 'binary_sha256': binaries['baseline'], 'profile': m.common.PROFILE, 'environment': m.common.ENV, 'sampling': m.validation.SAMPLING, 'thinking': False, 'stream': True, 'engine': 'tensorsharp'})
            entry = {'model': name, 'suites': {}}
            for suite in ('quality', 'unicode'):
                child_path = preceding / (name + '-' + suite + '.json')
                child = {'run_complete': True, 'binary_sha256': binaries['final'], 'cases': [dict(scenario=s, tag=t, concurrency=c, repeat=r) for s,t,c,r in m.common.keys(m.common.SUITES[suite])]}
                m.write(child_path, child)
                entry['suites'][suite] = {'complete': True, 'cases': len(child['cases']), 'source': m.source(child_path)}
            quality['runs'].append(entry)
    quality_path = preceding / 'run.json'; m.write(quality_path, quality)
    saved = {}
    for mode in ('all-matched', 'changed-valid-response', 'telemetry-finalization-failure'):
        state['change'] = mode == 'changed-valid-response'
        processes = []
        def launch(command, **kwargs):
            p = Process(command, **kwargs); processes.append(p); return p
        def kill(pid, *args):
            next(p for p in processes if p.pid == pid).stopped = True
        args = [str(path), '--baseline-host-dir', str(hosts['baseline']), '--final-host-dir', str(hosts['final']), '--final-native', str(hosts['final'] / 'libGgmlOps.so'), '--baseline-dir', str(old), '--quality-run', str(quality_path), '--label', mode, '--exclusive-window']
        with patch.object(m, 'WORK', work), patch.object(m.common, 'WORK', work), patch.object(m.common, 'MODELS', weights), patch.object(m, 'BASELINE_NATIVE_SHA', binaries['baseline']['libGgmlOps.so']), patch.object(m, 'BASELINE_RUNTIME_SHA', binaries['baseline']['TensorSharp.Runtime.dll']), patch.object(m.common, 'NATIVE_SHA', binaries['final']['libGgmlOps.so']), patch.object(m.common, 'snapshot', return_value={'simulated': True}), patch.object(m, 'gpu_clients', return_value=[]), patch.object(m, 'Telemetry', Monitor), patch.object(m, 'qualify_telemetry', return_value=qualified, side_effect=OSError('injected telemetry finalization failure') if mode == 'telemetry-finalization-failure' else None), patch.object(m.socket, 'socket', Socket), patch.object(m.subprocess, 'Popen', side_effect=launch), patch.object(m.os, 'killpg', side_effect=kill), patch.object(m.common.requests, 'get', return_value=types.SimpleNamespace(status_code=200, json=lambda: {'data': [{'id': 'fixture'}]})), patch.object(m.validation.engines, 'run_openai_chat', side_effect=engine), patch.object(sys, 'argv', args), patch('builtins.print'):
            code = m.main()
        result = json.loads((work / 'existing-regressions' / mode / 'run.json').read_text())
        assert result['run_complete'] and result['all_cases_passed']
        assert sum(x['cases'] for x in result['runs']) == 90 and len(processes) == 6 and all(p.stopped for p in processes)
        for run in result['runs']:
            report = json.loads(Path(run['report']['path']).read_text())
            file_logs = Path(report['logging_environment']['TENSORSHARP_LOG_DIR'])
            assert (file_logs / 'tensorsharp-server-20260911.jsonl').read_text() == 'actual simulated runtime log write\n'
            assert report['deployment_changed_files'] == []
            assert len(report['warmups']) == 5 and report['decode512_warmup']['turns'][0]['metrics']['completion_tokens'] == 512
        assert [(r['model'], r['version']) for r in result['runs']] == [(j['model'], v) for j in m.job_plan() for v in j['versions']]
        assert {str(p): p.read_bytes() for host in hosts.values() for p in host.rglob('*') if p.is_file()} == original
        assert code == int(mode != 'all-matched')
        assert result['all_groups_comparable'] == (mode == 'all-matched')
        if state['change']:
            for comparison in result['comparisons'].values():
                assert comparison['concurrency_groups']['1']['comparable']
                assert not comparison['concurrency_groups']['4']['comparable']
                assert 'speedup_final_over_baseline' not in comparison['concurrency_groups']['4']
        elif mode == 'all-matched':
            saved = {v: json.loads((work / 'existing-regressions' / mode / ('qwen3-' + v + '.json')).read_text()) for v in ('baseline', 'final')}
        else:
            assert all('telemetry_finalization_error' in json.loads(Path(run['report']['path']).read_text()) for run in result['runs'])
        checks.append({'name': mode + '-90-timed-plus36-warmups', 'passed': True})
    checks.append({'name': 'external-runtime-log-writes-all-six-jobs-keep-all-frozen-files-intact', 'passed': True})
    target = work / 'tamper' / 'bin'
    manifest = m.freeze_version(hosts['baseline'], target, binaries['baseline'])
    for name in ('TensorSharp.Runtime.dll', 'appsettings.json', 'logs/tensorsharp-server-20260911.jsonl'):
        assert name in manifest['files_sha256']
        original_bytes = (target / name).read_bytes()
        (target / name).write_bytes(original_bytes + b'tampered')
        assert m.common.unchanged(target, manifest) == [name]
        (target / name).write_bytes(original_bytes)
        assert m.common.unchanged(target, manifest) == []
        checks.append({'name': 'frozen-tamper-rejected-' + name, 'passed': True})
    old_report, new_report = saved['baseline'], saved['final']
    for name, mutate in [
        ('token-count-mismatch', lambda r: r['cases'][0]['turns'][0]['metrics'].update(completion_tokens=25)),
        ('request-hash-mismatch', lambda r: r['cases'][0].update(input_sha256='wrong')),
        ('missing-case', lambda r: r['cases'].pop()),
        ('duplicate-case', lambda r: r['cases'].__setitem__(1, copy.deepcopy(r['cases'][0]))),
        ('failed-semantic-case', lambda r: r['cases'][0].update(status='fail')),
        ('unqualified-telemetry', lambda r: r['telemetry_qualification'].update(qualified=False)),
        ('deployment-change', lambda r: r.update(deployment_changed_files=['TensorSharp.Runtime.dll'])),
        ('wave-token-accounting-mismatch', lambda r: r['waves'][0].update(generated_tokens=9999)),
        ('nonfinite-paired-clock', lambda r: r['telemetry_qualification']['gpu7']['clocks.sm'].update(median=float('nan'))),
    ]:
        changed = copy.deepcopy(new_report); mutate(changed)
        result = m.compare(old_report, changed)
        assert not result['all_groups_comparable']
        checks.append({'name': name + '-not-credited', 'passed': True})
    for name, mutate in [('tiny-stream-window', lambda r: [c['turns'][0]['metrics'].update(t_last_abs=1.001) for c in r['cases']]), ('decode-source-mismatch', lambda r: [c['turns'][0]['metrics'].update(decode_timing_source='server') for c in r['cases']])]:
        changed = copy.deepcopy(new_report); mutate(changed)
        result = m.compare(old_report, changed)
        for group in result['concurrency_groups'].values():
            assert group['comparable'] and 'json_decode' not in group['speedup_final_over_baseline']
            assert {'ttft', 'request_wall', 'whole_wave_wall', 'whole_wave_end_to_end_tps'} <= group['speedup_final_over_baseline'].keys()
        checks.append({'name': name + '-only-decode-ratio-withheld', 'passed': True})
    # Coverage gates precede deployment creation and native startup.
    broken = copy.deepcopy(quality); broken['run_complete'] = False; m.write(quality_path, broken)
    args = types.SimpleNamespace(quality_run=quality_path, baseline_dir=old)
    try: m.validate_inputs(args)
    except ValueError: checks.append({'name': 'preceding-quality-incomplete-rejected', 'passed': True})
    else: raise AssertionError('accepted incomplete prerequisite')

def sample(t, gpu_pid=123, sm=1740, foreign_ticks=0):
    return {'monotonic': t, 'gpus': [{'index': '7', 'uuid': 'gpu7', 'clocks.sm': str(sm), 'clocks.mem': '7251', 'power.draw': '200', 'power.limit': '300', 'temperature.gpu': '45', 'utilization.gpu': '70', 'memory.used': '5000'}], 'compute_clients': [{'pid': gpu_pid, 'gpu_uuid': 'gpu7'}], 'cpu': {900: {'comm': 'foreign-build', 'ppid': 1, 'start': 1, 'ticks': foreign_ticks}}}
for name, samples, declared, passed in [
    ('stable-owned-only', [sample(1), sample(2)], True, True),
    ('undeclared-window', [sample(1), sample(2)], False, False),
    ('foreign-gpu', [sample(1), sample(2, gpu_pid=555)], True, False),
    ('clock-drift', [sample(1), sample(2, sm=1200)], True, False),
    ('nan-clock', [sample(1), sample(2, sm='nan')], True, False),
    ('inf-clock', [sample(1), sample(2, sm='inf')], True, False),
    ('foreign-cpu-busy', [sample(1), sample(2, foreign_ticks=os.sysconf('SC_CLK_TCK'))], True, False),
    ('too-few-samples', [sample(1)], True, False),
]:
    result = m.qualify_telemetry(samples, [], 123, [0, 3], declared)
    assert result['qualified'] == passed, (name, result)
    checks.append({'name': name, 'passed': True})

output = Path('/tmp/deepseek41-reference/final-json-performance-r2-checks.json')
m.write(output, {'scope': 'Local simulated HTTP/process/telemetry orchestration with actual portable request generation and response validation. No real model, VM compute, clock mutation, or benchmark executed.', 'runner_source': m.source(path), 'test_source': m.source(Path(__file__)), 'checks': checks})
print(len(checks), 'checks passed')
