#!/usr/bin/env python3
"""Diagnostic only: exact retained Qwen3 JSON request, 3 chunk limits x 2 builds x 2 repetitions.
No copying/building/host mutation. No qualified timing or semantic-success override.
"""
import argparse
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import time

RUNNER = Path(__file__).with_name('run-final-json-performance.py')
RUNNER_SHA = '10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5'
PRIOR_SHA = '7ba46725c2a73a7131b859855533b09cf864e4f5b6d64ecb494eb6f1eeb16763'
REQUEST_SHA = 'e1c5ae800ba0b4c69abce54cb2dd6301f7a8a6329b7a9892d83b8514eefaacfc'
INPUT_SHA = '820cc3973263c3e195f42ed80a0e365680660fab31f290df81e48eb9922ce237'
if hashlib.sha256(RUNNER.read_bytes()).hexdigest() != RUNNER_SHA:
    raise RuntimeError('Reviewed R2 runner changed')
spec = importlib.util.spec_from_file_location('reviewed_json_r2', RUNNER)
r2 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r2)
common, validation = r2.common, r2.validation
write, source = common.write, common.source
TAG = 'json-c4-r0-i0'
LABEL = 'final3651-native6b3-qwen3-json-chunks'


def digest(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def plan():
    return [{'chunk': chunk, 'version': version, 'repetitions': 2, 'concurrency': 1}
            for index, chunk in enumerate((256, 76, 64))
            for version in (('baseline', 'final') if index % 2 == 0 else ('final', 'baseline'))]


def load_inputs(path):
    if common.file_sha(path) != PRIOR_SHA:
        raise ValueError('Completed R2 manifest changed')
    prior = json.loads(path.read_text())
    if prior.get('run_complete') is not True or prior.get('expected_timed_cases') != 90 or len(prior.get('runs', [])) != 6:
        raise ValueError('Completed90-case R2 prerequisite missing')
    harness = {Path(p).name: common.file_sha(Path(p)) for p in (validation.__file__, validation.engines.__file__, validation.scenarios.__file__)}
    if harness != prior['harness_sha256']:
        raise ValueError('Request/response harness changed from completed R2')
    children = {}
    for item in prior['runs']:
        p = Path(item['report']['path'])
        if common.file_sha(p) != item['report']['sha256']:
            raise ValueError('R2 child report changed')
        child = json.loads(p.read_text())
        if not r2.complete(child):
            raise ValueError('R2 child coverage incomplete')
        if item['model'] == 'qwen3':
            children[item['version']] = child
    expected_request = None
    for version in ('baseline', 'final'):
        child = children[version]
        case = next(c for c in child['cases'] if c['tag'] == TAG)
        request = case['turns'][0]['request']
        if case['input_sha256'] != INPUT_SHA or digest(request) != REQUEST_SHA:
            raise ValueError('Retained exact request changed')
        if expected_request is not None and request != expected_request:
            raise ValueError('Baseline/final recorded requests differ')
        expected_request = request
        deployment = prior['deployments'][version]
        if child['binary_sha256'] != deployment['expected_core_binaries']:
            raise ValueError('Frozen deployment does not describe the tested binaries')
        if common.unchanged(Path(deployment['target']), deployment):
            raise ValueError('Frozen deployment changed: ' + version)
    weights = common.identity('qwen3', common.MODELS['qwen3'])
    if any(c['weights_id'] != weights['sha256'] for c in children.values()):
        raise ValueError('Qwen3 weights differ from retained R2')
    return prior, children, expected_request, weights, harness


def execute_case(url, model, request, repeat):
    if digest(request) != REQUEST_SHA:
        raise ValueError('Exact request guard failed before HTTP')
    case = {'scenario': 'json', 'tag': TAG, 'concurrency': 1, 'repeat': repeat,
            'input_sha256': INPUT_SHA, 'recorded_request_sha256': REQUEST_SHA,
            'status': 'fail', 'turns': []}
    started = time.monotonic()
    try:
        metrics = validation.engines.run_openai_chat(url, model, timeout_s=90, **copy.deepcopy(request))
        case['turns'].append({'request': copy.deepcopy(request), 'metrics': metrics})
        if not metrics.get('usage_present') or not metrics.get('finish_reason'):
            raise ValueError('Missing usage/finish reason')
        if metrics.get('prompt_tokens') != 90:
            raise ValueError('Exact request tokenized differently: expected90 prompt tokens')
        case['validated_content'] = validation.assistant_content(metrics)
        if not validation.check_answer('json', case['validated_content']):
            raise ValueError("final answer failed the unchanged semantic/structural check")
        if metrics['finish_reason'] == 'length':
            raise ValueError('Answer reached token limit')
        case['status'] = 'ok'
    except Exception as error:
        case['detail'] = type(error).__name__ + ': ' + str(error)
    case['total_wall_ms'] = (time.monotonic() - started) * 1000
    return case


def observe_chunks(text, chunk):
    requests = {}
    for line in text.splitlines():
        if '[cb] ' not in line or ' work=[' not in line:
            continue
        if not re.search(r'\[cb\] SOLO step n=1 ', line):
            raise ValueError('Diagnostic request did not stay on the solo execution path')
        payload = line.split(' work=[', 1)[1].rsplit(']', 1)[0]
        match = re.fullmatch(r'([^,]+):([PD])@(\d+)', payload)
        if not match:
            raise ValueError('Malformed/ambiguous scheduler trace')
        key, phase, position = match.groups()
        requests.setdefault(key, []).append((phase, int(position)))
    expected = [0] if chunk >= 90 else [0, chunk]
    observations = []
    for key, steps in requests.items():
        prefills = [position for phase, position in steps if phase == 'P']
        decodes = [position for phase, position in steps if phase == 'D']
        decode_started = False
        ordered = True
        for phase, _ in steps:
            if phase == 'D':
                decode_started = True
            elif decode_started:
                ordered = False
        complete = ordered and prefills == expected and bool(decodes) and decodes[0] == 90
        observations.append({'request_id': key, 'prefill_positions': prefills,
                             'first_decode_position': decodes[0] if decodes else None,
                             'observed_prefill_lengths': [b-a for a,b in zip(prefills, prefills[1:]+[90])] if complete else None,
                             'phase_order_valid': ordered, 'matches_expected_chunking': complete})
    return {'requests': observations, 'complete': len(observations) == 2 and all(x['matches_expected_chunking'] for x in observations),
            'method': 'Consecutive logged P@ positions followed by first D@90, not a claimed native per-kernel trace.'}


def run_job(args, job, prior, request, weights, harness, out):
    chunk, version = job['chunk'], job['version']
    key = f'{version}-chunk{chunk}'
    deployment = prior['deployments'][version]
    target = Path(deployment['target'])
    command = common.launch_command(target, common.MODELS['qwen3'])
    index = command.index('--prefill-chunk-size')
    command[index+1] = str(chunk)
    settings = {**common.ENV, 'TS_SCHED_PREFILL_CHUNK': str(chunk), 'TS_SCHED_SOLO_PREFILL_CHUNK': str(chunk), 'TS_CB_DEBUG': '1',
                'TENSORSHARP_LOG_DIR': str((out/(key+'-file-logs')).resolve())}
    env = {k:v for k,v in os.environ.items() if not k.startswith(('TS_', 'TENSORSHARP_', 'GGML_', 'KV_CACHE_', 'MAX_CONTEXT'))}
    env.update(settings)
    log, path = out/(key+'-server.log'), out/(key+'.json')
    report = {'job': job, 'launch': command, 'environment': settings, 'binary_sha256': deployment['expected_core_binaries'],
              'weights': weights, 'harness_sha256': harness, 'cases': [], 'run_complete': False,
              'timings_qualified_for_comparison': False, 'timing_note': 'Cold first request, warm second request, debug logging; diagnostic only.',
              'request_sha256': REQUEST_SHA, 'source_deployment': target.as_posix()}
    write(path, report)
    process = None
    try:
        if args.stop_file.exists():
            raise InterruptedError('Resource window closed')
        if common.unchanged(target, deployment):
            raise RuntimeError('Frozen deployment changed before launch')
        if r2.gpu_clients():
            raise RuntimeError('Foreign GPU client is resident')
        with socket.socket() as probe:
            if probe.connect_ex(('127.0.0.1', 5011)) == 0:
                raise RuntimeError('Port5011 occupied')
        report['before'] = common.snapshot()
        with log.open('w') as stream:
            process = subprocess.Popen(command, env=env, cwd=target, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        write(out/'active-server.json', {'pid': process.pid, 'job': job, 'command': command})
        url, model, deadline = 'http://127.0.0.1:5011', None, time.monotonic()+180
        while time.monotonic() < deadline:
            if args.stop_file.exists():
                raise InterruptedError('Resource window closed')
            if process.poll() is not None:
                raise RuntimeError('Owned server exited before readiness')
            try:
                response = common.requests.get(url+'/v1/models', timeout=2)
                rows = response.json().get('data', []) if response.status_code == 200 else []
                if rows:
                    model = rows[0]['id']
                    break
            except (common.requests.RequestException, ValueError):
                pass
            time.sleep(.5)
        if model is None:
            raise TimeoutError('Server startup exceeded180seconds')
        report['served_model_id'] = model
        for repeat in range(2):
            if args.stop_file.exists():
                raise InterruptedError('Resource window closed')
            report['cases'].append(execute_case(url, model, request, repeat))
            write(path, report)
            print(key, repeat, report['cases'][-1]['status'], flush=True)
    except Exception as error:
        report['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        finalization_errors = []
        def failed(label, error):
            finalization_errors.append(label + ': ' + type(error).__name__ + ': ' + str(error))
        try:
            common.stop(process)
        except Exception as error:
            failed('owned cleanup', error)
        # Only remove our own marker after exit is observed. A failed cleanup
        # keeps its marker; a later job's prelaunch refusal must not erase it.
        if process is not None:
            try:
                report['owned_process_exit_observed'] = process.poll() is not None
                if report['owned_process_exit_observed']:
                    (out/'active-server.json').unlink(missing_ok=True)
                else:
                    finalization_errors.append('Owned process exit was not observed; active marker retained')
            except Exception as error:
                failed('owned exit/marker', error)
        try:
            report['deployment_changed_files'] = common.unchanged(target, deployment)
        except Exception as error:
            failed('deployment verification', error)
        try:
            report['after'] = common.snapshot()
        except Exception as error:
            failed('after snapshot', error)
        try:
            if log.exists():
                report['server_log'] = source(log)
                report['observed_chunks'] = observe_chunks(log.read_text(), chunk)
        except Exception as error:
            failed('trace/source evidence', error)
        report['finalization_errors'] = finalization_errors
        report['run_complete'] = (len(report['cases']) == 2 and not report.get('error') and not finalization_errors
                                  and report.get('deployment_changed_files') == []
                                  and report.get('observed_chunks', {}).get('complete', False))
        report['all_cases_passed'] = report['run_complete'] and all(c['status'] == 'ok' for c in report['cases'])
        write(path, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prior-run', type=Path, default=common.WORK/'existing-regressions/final3651-native6b3-json-performance-r2/run.json')
    parser.add_argument('--label', default=LABEL)
    parser.add_argument('--stop-file', type=Path)
    parser.add_argument('--plan-only', action='store_true')
    args = parser.parse_args()
    if args.plan_only:
        print(json.dumps({'jobs': plan(), 'expected_cases': 12, 'request_sha256': REQUEST_SHA,
                          'request_tag_unchanged': TAG, 'warmups': 0, 'timings_qualified_for_comparison': False,
                          'trace': 'TS_CB_DEBUG=1; observedP positions followed by D@90', 'runner_source': source(Path(__file__))}, indent=2))
        return 0
    if not args.label or Path(args.label).name != args.label:
        parser.error('Invalid label')
    out = common.WORK/'existing-regressions'/args.label
    if out.exists():
        parser.error('Refusing existing output')
    args.stop_file = args.stop_file or out/'STOP'
    if args.stop_file.exists():
        parser.error('STOP already exists')
    prior, children, request, weights, harness = load_inputs(args.prior_run)
    out.mkdir(parents=True)
    report = {'run_complete': False, 'expected_cases': 12, 'jobs': plan(), 'runs': [],
              'runner_source': source(Path(__file__)), 'reviewed_r2_runner': source(RUNNER), 'prior_r2_manifest': source(args.prior_run),
              'request': request, 'request_sha256': REQUEST_SHA, 'timings_qualified_for_comparison': False,
              'interpretation': 'Different chunk geometry can alter arithmetic. This control does not reproduce prior concurrent arrival/owner-migration state.'}
    write(out/'run.json', report)
    for job in plan():
        result = run_job(args, job, prior, request, weights, harness, out)
        name = f"{job['version']}-chunk{job['chunk']}.json"
        report['runs'].append({'job': job, 'source': source(out/name), 'run_complete': result['run_complete'],
                               'all_cases_passed': result['all_cases_passed'], 'cases': result['cases'], 'observed_chunks': result.get('observed_chunks')})
        write(out/'run.json', report)
        if args.stop_file.exists():
            break
    report['run_complete'] = len(report['runs']) == 6 and all(r['run_complete'] for r in report['runs']) and sum(len(r['cases']) for r in report['runs']) == 12
    report['all_cases_passed'] = report['run_complete'] and all(r['all_cases_passed'] for r in report['runs'])
    report['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    write(out/'run.json', report)
    print(json.dumps({'report': str(out/'run.json'), 'run_complete': report['run_complete'], 'all_cases_passed': report['all_cases_passed']}))
    return 0 if report['run_complete'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
