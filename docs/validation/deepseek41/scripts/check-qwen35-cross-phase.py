#!/usr/bin/env python3
"""Local runner guards with real pinned R2 requests and simulated HTTP/processes."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import types
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('cross_guarded', HERE / 'run-qwen35-cross-phase.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
r2 = m.load_runner(Path('/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison'))
checks = []
state = {'requests': [], 'processes': [], 'change': False, 'no_phases': False, 'stop_early': False}


def check(label, condition):
    assert condition, label
    checks.append(label)


def refused(label, action, expected=(ValueError, FileExistsError)):
    try:
        action()
    except expected:
        checks.append(label)
    else:
        raise AssertionError(label)


class Socket:
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def connect_ex(self, *args): return 1


class Process:
    def __init__(self, command, **kwargs):
        self.pid = 30000 + len(state['processes'])
        self.stopped = False
        self.command, self.env = command, kwargs['env']
        state['processes'].append(self)
        state['version'] = Path(command[1]).parent.parent.name
        logdir = Path(self.env['TENSORSHARP_LOG_DIR'])
        assert Path(kwargs['cwd']).resolve() not in logdir.resolve().parents
        logdir.mkdir(parents=True)
        (logdir / 'server.jsonl').write_text('{"simulated":true}\n')
        if not state['no_phases']:
            kwargs['stdout'].write('[phase] Qwen3.5 model verify total=10.50ms build=1.00 bind=0.50 optimize=0.25 alloc=0.75 upload=0.50 submit=0.50 compute+download=7.00\n')
        kwargs['stdout'].flush()
    def poll(self): return 0 if self.stopped else None
    def wait(self, **kwargs): self.stopped = True; return 0


class Monitor:
    def __init__(self, path): self.path, self.samples, self.errors = path, [], []
    def start(self): self.path.write_text('{"simulated":true}\n')
    def stop(self): pass


def engine(url, model, **request):
    tag = request['messages'][0]['content'].split(']')[0].split('[validation ')[-1]
    state['requests'].append((state['version'], tag, copy.deepcopy(request)))
    decode = tag == 'warmup-decode'
    text = 'hash collision ' * 40 if decode else '{"name":"Mars","moons":2,"habitable":false}'
    if state['change'] and state['version'] == 'baseline-managed_final-native' and tag == 'json-c4-r1-i2':
        text = '{"name":"Earth","moons":2,"habitable":false}'
    return {'assistant_message': {'role': 'assistant', 'content': text}, 'finish_reason': 'length' if decode else 'stop',
            'usage_present': True, 'prompt_tokens': 92, 'completion_tokens': 512 if decode else 16,
            'ttft_ms': 10, 'decode_tps': 100, 'total_wall_ms': 300, 'decode_timing_source': 'stream_window',
            't_first_abs': 1, 't_last_abs': 1.23, 'prefill_tps': 9200, 'output_text': text}


with tempfile.TemporaryDirectory() as directory:
    work = Path(directory)
    old = work / 'r2'; old.mkdir()
    original_manifest = json.loads((HERE / 'final-json-performance-r2/run.json').read_text())
    manifest = copy.deepcopy(original_manifest)
    for version in ('baseline', 'final'):
        host = work / ('source-' + version); host.mkdir()
        for name in r2.BINARY_NAMES: (host / name).write_bytes((version + name).encode())
        (host / 'appsettings.json').write_text('{}')
        (host / 'logs').mkdir()
        (host / 'logs/inherited.jsonl').write_text('must stay immutable\n')
        deployment = manifest['deployments'][version]
        deployment.update(target=str(host), files_sha256=m.tree_hashes(host),
                          expected_core_binaries={name: m.sha(host / name) for name in r2.BINARY_NAMES})
        report = json.loads((HERE / 'final-json-performance-r2' / ('qwen35-' + version + '.json')).read_text())
        report['binary_sha256'] = deployment['expected_core_binaries']
        r2.write(old / ('qwen35-' + version + '.json'), report)
        entry = next(r for r in manifest['runs'] if r['model'] == 'qwen35' and r['version'] == version)
        entry['report'] = r2.source(old / ('qwen35-' + version + '.json'))
    r2.write(old / 'run.json', manifest)
    weights = copy.deepcopy(report['weights'])
    (work / 'dotnet').mkdir(); (work / 'dotnet/dotnet').write_text('fake dotnet\n')
    args = types.SimpleNamespace(r2_manifest=old / 'run.json', out=work / 'results', deployments=work / 'hosts', stop_file=work / 'stop')
    with patch.object(m, 'R2_MANIFEST_SHA', m.sha(old / 'run.json')), \
         patch.object(r2.common, 'identity', return_value=weights), patch.object(r2.common, 'WORK', work), \
         patch.object(r2.validation.engines, 'run_openai_chat', side_effect=engine), \
         patch.object(r2.subprocess, 'Popen', Process), patch.object(r2.socket, 'socket', Socket), \
         patch.object(r2, 'Telemetry', Monitor), patch.object(r2, 'gpu_clients', return_value=[]), \
         patch.object(r2.common, 'snapshot', return_value={'simulated': True}), \
         patch.object(r2.common.requests, 'get', return_value=types.SimpleNamespace(status_code=200, json=lambda: {'data': [{'id': 'qwen35'}]})), \
         patch.object(r2.common, 'stop', side_effect=lambda p: p.wait() if p else None), \
         patch.object(r2, 'qualify_telemetry', return_value={'qualified': True, 'issues': []}):
        result = m.execute(r2, args)
        check('four complete successful jobs', result['run_complete'] and result['all_cases_passed'] and len(result['runs']) == 4)
        check('60 measured requests plus24 warmups', len(state['requests']) == 84 and sum(t.startswith('json-c') for _, t, _ in state['requests']) == 60)
        check('all four exact managed/native pairs', [(v['managed_version'], v['native_version']) for v in result['deployments'].values()] == list(m.JOBS))
        check('all owned servers stopped', len(state['processes']) == 4 and all(p.stopped for p in state['processes']))
        check('every launch has only diagnostic phase env addition', all({k: p.env[k] for k in r2.common.ENV} == r2.common.ENV and p.env['TS_GGML_PHASE_TIMING'] == '1' for p in state['processes']))
        check('R2 source files unchanged', not result['source_r2_changed_files'])
        check('all diagnostic copies unchanged', not any(result['diagnostic_host_changed_files'].values()))
        check('no top-level qualified timing claim', result['diagnostic_only'] and not result['performance_qualified'])
        reports = [json.loads(Path(entry['source']['path']).read_text()) for entry in result['runs']]
        check('no child qualified timing claim', all(r['diagnostic_only'] and not r['performance_qualified'] and not r['telemetry_qualification']['qualified'] for r in reports))
        check('native phase fields and full lines captured', all(json.loads(Path(r['native_phase_evidence']['path']).read_text())['phase_lines'][0]['milliseconds']['compute+download'] == 7 for r in reports))
        check('mutable runtime logs captured outside frozen host', all(len(r['runtime_file_logs']) == 1 for r in reports))
        for deployment in result['deployments'].values():
            expected = dict(manifest['deployments'][deployment['managed_version']]['files_sha256'])
            expected['libGgmlOps.so'] = manifest['deployments'][deployment['native_version']]['files_sha256']['libGgmlOps.so']
            check('cross replaces exactly native: ' + deployment['managed_version'] + '/' + deployment['native_version'], deployment['files_sha256'] == expected)
        for version in result['deployments']:
            rows = [(tag, request) for v, tag, request in state['requests'] if v == version]
            check('exact R2 measured identities: ' + version, {tag for tag, _ in rows if tag.startswith('json-c')} == {k[1] for k in r2.planned_keys()})
            check('one decode512 and five JSON warmups: ' + version, sum(t == 'warmup-decode' and r['max_tokens'] == 512 for t, r in rows) == 1 and sum(t.startswith('json-warmup') for t, _ in rows) == 5)
        refused('existing output/host reuse refused', lambda: m.execute(r2, args))
        bad = copy.copy(args); bad.out = work / 'bad-out'; bad.deployments = Path(manifest['deployments']['baseline']['target']) / 'new-hosts'
        refused('old source host overlap refused before writes', lambda: m.execute(r2, bad))
        check('overlap refusal has no output writes', not bad.out.exists() and not bad.deployments.exists())
        source = Path(manifest['deployments']['baseline']['target'])
        for name in ('TensorSharp.Runtime.dll', 'appsettings.json', 'logs/inherited.jsonl'):
            p = source / name; prior = p.read_bytes(); p.write_bytes(prior + b'tamper')
            refused('source tamper refused: ' + name, lambda: m.validate_inputs(r2, args))
            p.write_bytes(prior)
        (source / 'new-untracked-file').write_text('unexpected')
        refused('new source files refused', lambda: m.validate_inputs(r2, args))
        (source / 'new-untracked-file').unlink()
        with patch.object(m, 'R2_MANIFEST_SHA', '0' * 64):
            refused('R2 manifest hash mismatch refused', lambda: m.validate_inputs(r2, args))
        wrong_weights = dict(weights, sha256='0' * 64)
        with patch.object(r2.common, 'identity', return_value=wrong_weights):
            refused('model SHA mismatch refused', lambda: m.validate_inputs(r2, args))
        p = old / 'qwen35-final.json'; prior = p.read_bytes(); p.write_bytes(prior + b' ')
        refused('R2 child report tamper refused', lambda: m.validate_inputs(r2, args)); p.write_bytes(prior)
        state['change'] = True
        bad = copy.copy(args); bad.out = work / 'failed-case'; bad.deployments = work / 'failed-case-hosts'
        changed = m.execute(r2, bad)
        check('semantic failure retained, all four jobs still complete', changed['run_complete'] and not changed['all_cases_passed'] and len(changed['runs']) == 4 and sum(len(r['failed_cases']) for r in changed['runs']) == 1)
        state['change'] = False; state['no_phases'] = True
        bad = copy.copy(args); bad.out = work / 'missing-phase'; bad.deployments = work / 'missing-phase-hosts'
        missing = m.execute(r2, bad)
        check('missing phase evidence cannot complete diagnostic', not missing['run_complete'] and len(missing['runs']) == 4)
        state['no_phases'] = False
        bad = copy.copy(args); bad.out = work / 'stopped'; bad.deployments = work / 'stopped-hosts'; bad.stop_file.touch()
        before = len(state['processes']); stopped = m.execute(r2, bad)
        check('closed resource window launches no process and preserves incomplete report', not stopped['run_complete'] and len(state['processes']) == before and 'InterruptedError' in stopped['error'])
        bad.stop_file.unlink()
    for constant in ('R2_RUNNER_SHA', 'HELPER_SHA'):
        with patch.object(m, constant, '0' * 64):
            refused('pinned import mismatch refused: ' + constant, lambda: m.load_runner(Path('/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison')))
    with patch.object(m, 'HARNESS_SHA', dict(m.HARNESS_SHA, **{'engines.py': '0' * 64})):
        refused('harness hash mismatch refused before import', lambda: m.load_runner(Path('/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison')))
    check('request failures never leave owned processes running', all(p.stopped for p in state['processes']))

r2.write(HERE / 'qwen35-cross-phase-checks.json', {'passed': len(checks), 'checks': checks,
          'runner_source': r2.source(HERE / 'run-qwen35-cross-phase.py'), 'guard_source': r2.source(Path(__file__)),
          'scope': 'Local mocked process/HTTP/telemetry guards, real pinned R2 request builder and source hashes; no GPU or VM execution.'})
print('PASS', len(checks), 'local guards')
