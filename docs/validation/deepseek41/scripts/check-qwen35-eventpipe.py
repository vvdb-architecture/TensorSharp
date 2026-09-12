#!/usr/bin/env python3
"""Mocked EventPipe lifecycle guards using the unchanged pinned R2 executor."""
import copy
import importlib.util
import json
from pathlib import Path
import runpy
import signal
import subprocess
import tempfile
import types
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
prior = runpy.run_path(str(HERE / 'check-qwen35-cross-phase.py'))
cross, r2 = prior['m'], prior['r2']
spec = importlib.util.spec_from_file_location('eventpipe_guarded', HERE / 'run-qwen35-eventpipe.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
checks = []
state = {'hosts': [], 'collectors': [], 'events': [], 'mode': 'ok'}


def check(label, condition):
    assert condition, label
    checks.append(label)


def refused(label, action):
    try: action()
    except (ValueError, FileExistsError): checks.append(label)
    else: raise AssertionError(label)


class Host(prior['Process']):
    def __init__(self, command, **kwargs):
        super().__init__(command, **kwargs)
        state['hosts'].append(self)
        state['events'].append(('host_start', prior['state']['version']))


class Collector:
    def __init__(self, command, **kwargs):
        self.command, self.kwargs = command, kwargs
        self.pid = 40000 + len(state['collectors']); self.returncode = None
        self.label = prior['state']['version']; self.stopped = False
        self.host_pid = int(command[command.index('--process-id') + 1])
        self.path = Path(command[command.index('--output') + 1])
        self.stream = kwargs['stdout']
        state['collectors'].append(self)
        state['events'].append(('trace_start', self.label))
        assert sum(v == self.label for v, tag, req in prior['state']['requests']) == 6
        self.path.write_bytes(b'Nettrace simulated raw stream\n')
        self.stream.write('Output File    : ' + str(self.path) + '\n')
        self.stream.flush()
        if state['mode'] == 'early-exit': self.returncode = 3; self.stopped = True
    def poll(self): return self.returncode
    def send_signal(self, sig):
        assert sig == signal.SIGINT
        state['events'].append(('trace_stop_requested', self.label))
    def wait(self, timeout=None):
        if state['mode'] == 'timeout' and not self.stopped:
            raise subprocess.TimeoutExpired(self.command, timeout)
        self.stopped = True; self.returncode = 0
        self.stream.write('Trace completed.\n')
        if state['mode'] == 'broken': self.stream.write('WARNING: potentially broken trace\n')
        self.stream.flush()
        state['events'].append(('trace_finalized', self.label))
        return self.returncode
    def kill(self):
        self.stopped = True; self.returncode = -9
        state['events'].append(('trace_killed', self.label))


def launch(command, **kwargs):
    return Collector(command, **kwargs) if command[0].endswith('fake-dotnet-trace') else Host(command, **kwargs)


def stop_host(process):
    if not process: return
    label = Path(process.command[1]).parent.parent.name
    assert all(c.stopped for c in state['collectors'] if c.host_pid == process.pid)
    state['events'].append(('host_stop', label))
    process.wait()


def engine(url, model, **request):
    tag = request['messages'][0]['content'].split(']')[0].split('[validation ')[-1]
    label = prior['state'].get('version')
    if tag.startswith('json-c'):
        assert any(c.label == label and c.poll() is None for c in state['collectors'])
        state['events'].append(('measured_request', label))
    else: state['events'].append(('warmup_request', label))
    result = prior['engine'](url, model, **request)
    if state['mode'] == 'semantic' and tag == 'json-c4-r1-i2':
        result['assistant_message']['content'] = '{"name":"Earth","moons":2,"habitable":false}'
    return result


with tempfile.TemporaryDirectory() as directory:
    work = Path(directory); old = work / 'r2'; old.mkdir()
    manifest = json.loads((HERE / 'final-json-performance-r2/run.json').read_text())
    for version in ('baseline', 'final'):
        host = work / ('source-' + version); host.mkdir()
        for name in r2.BINARY_NAMES: (host / name).write_bytes((version + name).encode())
        (host / 'settings.json').write_text('{}')
        d = manifest['deployments'][version]
        d.update(target=str(host), files_sha256=cross.tree_hashes(host),
                 expected_core_binaries={name: cross.sha(host / name) for name in r2.BINARY_NAMES})
        report = json.loads((HERE / 'final-json-performance-r2' / ('qwen35-' + version + '.json')).read_text())
        report['binary_sha256'] = d['expected_core_binaries']
        r2.write(old / ('qwen35-' + version + '.json'), report)
        entry = next(r for r in manifest['runs'] if r['model'] == 'qwen35' and r['version'] == version)
        entry['report'] = r2.source(old / ('qwen35-' + version + '.json'))
    r2.write(old / 'run.json', manifest)
    previous = {'run_complete': True, 'all_cases_passed': True,
                'deployments': cross.freeze_cross(r2, manifest, work / 'cross-hosts')}
    cross_root = work / 'cross-results'; cross_root.mkdir(); r2.write(cross_root / 'run.json', previous)
    tool = work / 'fake-dotnet-trace'; tool.write_text('fake profiler apphost')
    tool_dll = work / 'fake-profiler.dll'; tool_dll.write_text('fake profiler implementation')
    help_file = work / 'collect-help.txt'; help_file.write_text('pinned help')
    tool_evidence = {'version': '10.0.745401', 'tool_executable': str(tool),
                     'tool_files': [r2.source(tool), r2.source(tool_dll)], 'help_sources': [r2.source(help_file)]}
    evidence = work / 'tool.json'; r2.write(evidence, tool_evidence)
    args = types.SimpleNamespace(out=work / 'eventpipe-results', r2_manifest=old / 'run.json',
        cross_manifest=cross_root / 'run.json', trace_tool=tool, tool_evidence=evidence, stop_file=work / 'stop')
    weights = copy.deepcopy(report['weights'])
    original_tree = {str(p): cross.sha(p) for p in work.rglob('*') if p.is_file()}
    prior['state']['requests'] = []; prior['state']['processes'] = []; prior['state']['no_phases'] = False
    with patch.object(cross, 'R2_MANIFEST_SHA', cross.sha(old / 'run.json')), \
         patch.object(m, 'CROSS_MANIFEST_SHA', cross.sha(cross_root / 'run.json')), \
         patch.object(m, 'TOOL_EVIDENCE_SHA', cross.sha(evidence)), \
         patch.object(r2.common, 'identity', return_value=weights), patch.object(r2.common, 'WORK', work), \
         patch.object(r2.validation.engines, 'run_openai_chat', side_effect=engine), \
         patch.object(m.subprocess, 'Popen', side_effect=launch), patch.object(r2.socket, 'socket', prior['Socket']), \
         patch.object(r2, 'Telemetry', prior['Monitor']), patch.object(r2, 'gpu_clients', return_value=[]), \
         patch.object(r2.common, 'snapshot', return_value={'simulated': True}), \
         patch.object(r2.common.requests, 'get', return_value=types.SimpleNamespace(status_code=200, json=lambda: {'data': [{'id': 'qwen35'}]})), \
         patch.object(r2.common, 'stop', side_effect=stop_host), \
         patch.object(r2, 'qualify_telemetry', return_value={'qualified': True, 'issues': []}):
        result = m.execute(cross, r2, args)
        check('exact two complete15-case jobs', result['run_complete'] and result['all_cases_passed'] and len(result['runs']) == 2)
        check('exact30 measured plus12 warmups', len(prior['state']['requests']) == 42 and sum(t.startswith('json-c') for _, t, _ in prior['state']['requests']) == 30)
        check('two fresh processes reuse only final-native hosts', len(state['hosts']) == 2 and [Path(p.command[1]).parent.parent.name for p in state['hosts']] == list(m.JOBS))
        check('every host explicit1ms sampling and existing phase mode', all(p.env['DOTNET_EventPipeThreadSamplingRate'] == '1' and p.env['TS_GGML_PHASE_TIMING'] == '1' for p in state['hosts']))
        check('profile and CLR union are exactly preserved', all(c.command[c.command.index('--profile')+1] == m.PROFILES and c.command[c.command.index('--providers')+1] == m.PROVIDERS for c in state['collectors']))
        check('collector uses explicit DOTNET_ROOT', all(c.kwargs['env']['DOTNET_ROOT'] == str(work / 'dotnet') for c in state['collectors']))
        check('all old files unchanged including every unselected host', all(cross.sha(Path(p)) == h for p,h in original_tree.items()))
        check('no performance qualification', result['diagnostic_only'] and not result['performance_qualified'])
        for label in m.JOBS:
            events = [event for event, version in state['events'] if version == label]
            check('attach after exact six warmups: ' + label, events[:7] == ['host_start'] + ['warmup_request']*6 and events[7] == 'trace_start')
            check('all15 requests before trace finalization and host stop: ' + label, events[8:23] == ['measured_request']*15 and events[23:] == ['trace_stop_requested','trace_finalized','host_stop'])
            trace = json.loads((args.out / ('qwen35-' + label + '-eventpipe.json')).read_text())
            start,end = trace['actual_case_monotonic_interval']
            check('readiness and stop anchors enclose measured interval: ' + label, trace['collection_ready']['monotonic'] <= start <= end <= trace['stop_requested']['monotonic'])
            check('rawtrace complete collection but parser integrity pending: ' + label, trace['collection_complete'] and trace['raw_nettrace']['bytes'] > 0 and trace['trace_integrity'].startswith('Pending'))
        refused('existing output refused', lambda: m.execute(cross, r2, args))
        for p in (tool, tool_dll, help_file):
            content=p.read_bytes();p.write_bytes(content+b' changed')
            refused('CLI implementation/help mismatch refused: ' + p.name, lambda: m.validate_tool(cross,args));p.write_bytes(content)
        for mode in ('semantic','early-exit','timeout','broken'):
            state['mode']=mode;state['events']=[];prior['state']['requests']=[]
            bad=copy.copy(args);bad.out=work/('eventpipe-'+mode)
            result=m.execute(cross,r2,bad)
            check('all failures retained: '+mode, not result['all_cases_passed'] and len(result['runs'])==2)
            check('both collector and host cleanup complete: '+mode, all(c.stopped for c in state['collectors']) and all(p.stopped for p in state['hosts']))
            if mode=='semantic':check('semantic failures do not discard full coverage',result['run_complete'] and sum(len(r['failed_cases'])for r in result['runs'])==2)
            if mode=='early-exit':check('no measured requests before collector ready',len(prior['state']['requests'])==12 and not result['run_complete'])
            if mode in ('timeout','broken'):check('bad collection cannot complete diagnostic: '+mode,not result['run_complete'])

r2.write(HERE/'qwen35-eventpipe-checks.json',{'passed':len(checks),'checks':checks,'runner_source':r2.source(HERE/'run-qwen35-eventpipe.py'),
    'guard_source':r2.source(Path(__file__)),'prerequisite_cross_guards':40,
    'scope':'Local simulated collector/HTTP/host lifecycle, real pinned R2 request builder; no EventPipe capture, VM or GPU execution.'})
print('PASS',len(checks),'EventPipe local guards')
