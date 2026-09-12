#!/usr/bin/env python3
"""Uninstrumented B/F/F/B control: final native fixed,18 original solo requests/job."""
import argparse
import importlib.util
import json
import math
import os
from pathlib import Path
import socket
import statistics
import subprocess
import time

HERE = Path(__file__).resolve().parent
CROSS_RUNNER_SHA = '32219050b755a6b28a08de3c0255d929d53813e5378b022b00d435c4484104b2'
CROSS_MANIFEST_SHA = '2dff39c993f0d53868b9ddcc41fa5ca2681a10a9dfa3ea8a4c4e5c8c2a241994'
VERSIONS = ('baseline', 'final', 'final', 'baseline')
ENV_PREFIXES = ('TS_', 'TENSORSHARP_', 'GGML_', 'KV_CACHE_', 'MAX_CONTEXT', 'DOTNET_', 'COMPlus_', 'CORECLR_')
SCOPE = ('Four fresh processes in B/F/F/B order, final native6b3 fixed. Each retains exact R2 '
         'decode512 and five JSON warmups, then repeats its original r0/r1/r2 solo requests six times. '
         'All18 requests participate in each paired comparison; first3/later15 are predeclared descriptive groups.')


def load_cross():
    import hashlib
    path = HERE / 'run-qwen35-cross-phase.py'
    if hashlib.sha256(path.read_bytes()).hexdigest() != CROSS_RUNNER_SHA:
        raise ValueError('Reviewed helper changed')
    spec = importlib.util.spec_from_file_location('solo72_cross', path)
    cross = importlib.util.module_from_spec(spec); spec.loader.exec_module(cross)
    return cross


def clean_environment(profile, logdir):
    # Diagnostic/runtime prefixes are removed rather than recording unrelated
    # caller values. No profiler/debug/native-phase setting is reintroduced.
    env = {k: v for k, v in os.environ.items() if not k.startswith(ENV_PREFIXES)}
    env.update(profile)
    env['TENSORSHARP_LOG_DIR'] = str(logdir.resolve())
    return env


def verify_native_mapping(cross, pid, target, expected, proc_root=Path('/proc')):
    maps = (proc_root / str(pid) / 'maps').read_text()
    lines = [line for line in maps.splitlines() if 'libGgmlOps.so' in line]
    paths = sorted({line.split(maxsplit=5)[5] for line in lines})
    if paths != [str((target / 'libGgmlOps.so').resolve())]:
        raise ValueError('Loaded native path differs or is missing/deleted: ' + repr(paths))
    cross.pin(target / 'libGgmlOps.so', expected)
    return {'pid': pid, 'selected_library_paths': paths, 'mapping_lines': lines,
            'library_sha256': expected, 'observed_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
            'scope': 'One owned-process maps read after readiness and before warmups; no observer in the measured interval.'}


def complete(report):
    cases = report.get('cases', [])
    waves = report.get('waves', [])
    return (report.get('run_complete') is True and len(cases) == 18
            and len(waves) == 18 and [w.get('iteration') for w in waves] == list(range(18))
            and all(w.get('generated_tokens') == sum(t['metrics'].get('completion_tokens', 0) for t in c.get('turns', []))
                    and w.get('logical_id') == c.get('logical_id') for w, c in zip(waves, cases))
            and [c.get('iteration') for c in cases] == list(range(18))
            and len({c.get('logical_id') for c in cases}) == 18
            and all(c.get('concurrency') == 1 and c.get('repeat') == c['iteration'] % 3
                    and c.get('cycle') == c['iteration'] // 3
                    and c.get('original_request_matches') is True
                    and c.get('tag') == 'json-c1-r' + str(c['iteration'] % 3) + '-i0'
                    for c in cases))


def numeric_summary(cases):
    result = {'cases': len(cases), 'logical_ids': [c['logical_id'] for c in cases],
              'statuses': [c['status'] for c in cases], 'scope': 'All specified observations retained, descriptive only.'}
    for field in ('ttft_ms', 'total_wall_ms', 'decode_tps', 'wave_wall_ms'):
        values = [c.get(field) if field == 'wave_wall_ms' else
                  c['turns'][0]['metrics'].get(field) if len(c.get('turns', [])) == 1 else None for c in cases]
        valid = bool(values) and all(isinstance(v, (int, float)) and math.isfinite(v) and v > 0 for v in values)
        result[field] = {'values': values, 'all_valid': valid, 'median': statistics.median(values) if valid else None}
    result['completion_tokens'] = [c['turns'][0]['metrics'].get('completion_tokens') if len(c.get('turns', [])) == 1 else None for c in cases]
    return result


def run_job(cross, r2, args, index, version, deployment, refs, weights):
    label = 'job' + str(index) + '-' + version
    target = Path(deployment['target'])
    path = args.out / (label + '.json')
    log = args.out / (label + '-server.log')
    logdir = args.out / (label + '-file-logs')
    command = r2.common.launch_command(target, r2.common.MODELS['qwen35'])
    historical_cases = {c['tag']: c for c in refs[version]['report']['cases'] if c['concurrency'] == 1}
    env = clean_environment(r2.common.ENV, logdir)
    report = {'job': index, 'version': version, 'run_complete': False, 'status': 'not_started', 'scope': SCOPE,
              'execution_plan': {'case_count': 18, 'concurrency': 1, 'exact_tags': ['json-c1-r0-i0', 'json-c1-r1-i0', 'json-c1-r2-i0'],
                                 'cycles': 6, 'warmups': {'decode512': 1, 'json': 5}, 'descriptive_groups': [3, 15]},
              'cross': {'managed': version, 'native': 'final'}, 'binary_sha256': deployment['expected_core_binaries'],
              'weights_id': weights['sha256'], 'weights': weights, 'harness_sha256': cross.HARNESS_SHA,
              'sampling': r2.validation.SAMPLING, 'thinking': False, 'stream': True,
              'profile': r2.common.PROFILE, 'environment': dict(r2.common.ENV),
              'runtime_environment_policy': {'stripped_prefixes': list(ENV_PREFIXES),
                    'deliberate_DOTNET_COMPlus_CORECLR_settings': {}, 'runtime_defaults': 'No inherited override from these prefixes'},
              'logging_environment': {'TENSORSHARP_LOG_DIR': str(logdir.resolve())},
              'launch': command, 'cases': [], 'waves': [], 'warmups': [], 'historical_reference': refs[version]['source']}
    process, monitor, interval = None, None, [0, 0]
    r2.write(path, report)
    try:
        if args.stop_file.exists(): raise InterruptedError('Resource window closed')
        if cross.tree_hashes(target) != deployment['files_sha256']:
            raise ValueError('Frozen selected host changed before launch')
        if r2.gpu_clients(): raise RuntimeError('Foreign GPU client present; no endpoint started')
        with socket.socket() as probe:
            if probe.connect_ex(('127.0.0.1', 5011)) == 0:
                raise RuntimeError('Port5011 occupied; no endpoint started')
        report['safe_before_snapshot'] = r2.common.snapshot()
        with log.open('w') as stream:
            process = subprocess.Popen(command, env=env, cwd=target, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        r2.write(args.out / 'active-server.json', {'pid': process.pid, 'job': index, 'version': version, 'command': command})
        url, model, deadline = 'http://127.0.0.1:5011', None, time.monotonic() + 180
        while time.monotonic() < deadline:
            if args.stop_file.exists(): raise InterruptedError('Resource window closed')
            if process.poll() is not None: raise RuntimeError('Owned server exited before readiness')
            try:
                response = r2.common.requests.get(url + '/v1/models', timeout=2)
                rows = response.json().get('data', []) if response.status_code == 200 else []
                if rows:
                    model = rows[0]['id']; break
            except (r2.common.requests.RequestException, ValueError): pass
            time.sleep(.5)
        if model is None: raise TimeoutError('Server startup exceeded180seconds')
        report['native_mapping_observation'] = verify_native_mapping(cross, process.pid, target, deployment['expected_core_binaries']['libGgmlOps.so'])
        report.update(status='running', served_model_id=model)
        report['decode512_warmup'] = r2.validation.run_case(url, model, 'tensorsharp', 'decode', 'warmup-decode', timeout=90)
        r2.write(path, report)
        for degree in (1, 4):
            cases, _ = r2.run_wave(url, model, degree, 0, warmup=True)
            report['warmups'] += cases
            r2.write(path, report)
        monitor = r2.Telemetry(args.out / (label + '-telemetry.jsonl'))
        monitor.start()
        interval[0] = time.monotonic()
        for iteration in range(18):
            if args.stop_file.exists(): raise InterruptedError('Resource window closed')
            repeat = iteration % 3
            cases, wave = r2.run_wave(url, model, 1, repeat)
            case = cases[0]
            case.update(job=index, iteration=iteration, cycle=iteration // 3, repeat=repeat, concurrency=1,
                        logical_id=label + '-iteration' + str(iteration).zfill(2), wave_wall_ms=wave['wall_ms'])
            wave.update(job=index, iteration=iteration, cycle=iteration // 3, logical_id=case['logical_id'])
            report['waves'].append(wave)
            case['original_request_matches'] = r2.request_identity(case) == r2.request_identity(historical_cases[case['tag']])
            report['cases'].append(case)
            r2.write(path, report)
            print(label, iteration, case['status'], flush=True)
        interval[1] = time.monotonic()
        report['run_complete'] = True
        report['run_complete'] = complete(report)
    except Exception as error:
        report['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        finalization_errors = []
        def failed(label, error):
            finalization_errors.append(label + ': ' + type(error).__name__ + ': ' + str(error))
        if not interval[1]: interval[1] = time.monotonic()
        report['timed_monotonic_interval'] = interval
        try:
            if monitor:
                monitor.stop()
                report['telemetry_source'] = r2.source(monitor.path)
                report['telemetry_qualification'] = r2.qualify_telemetry(monitor.samples, monitor.errors, process.pid, interval, args.exclusive_window)
        except Exception as error:
            report['telemetry_finalization_error'] = type(error).__name__ + ': ' + str(error)
            report['telemetry_qualification'] = {'qualified': False, 'issues': [report['telemetry_finalization_error']]}
            failed('telemetry finalization', error)
        finally:
            try:
                r2.common.stop(process)
            except Exception as error:
                failed('owned cleanup', error)
        # Do not clear a prior failed-live job's marker on a later prelaunch
        # refusal. Only the owned process whose exit is observed clears it.
        if process is not None:
            try:
                report['owned_process_exit_observed'] = process.poll() is not None
                if report['owned_process_exit_observed']:
                    (args.out / 'active-server.json').unlink(missing_ok=True)
                else:
                    finalization_errors.append('Owned process exit not observed; active marker retained')
            except Exception as error:
                failed('owned exit/marker', error)
        report['deployment_changed_files'] = None
        try:
            report['deployment_changed_files'] = sorted(set(cross.tree_hashes(target).items()) ^ set(deployment['files_sha256'].items()))
        except Exception as error:
            failed('deployment verification', error)
        try:
            report['safe_after_snapshot'] = r2.common.snapshot()
        except Exception as error:
            failed('after snapshot', error)
        try:
            if log.exists():
                report['server_log'] = r2.source(log)
                report['unexpected_native_phase_lines'] = [line for line in log.read_text(errors='replace').splitlines() if '[phase]' in line]
            report['runtime_file_logs'] = [r2.source(p) for p in sorted(logdir.rglob('*')) if p.is_file()]
        except Exception as error:
            failed('log/source evidence', error)
        warm = report.get('decode512_warmup', {})
        report['warmup_complete'] = (warm.get('status') == 'ok' and len(warm.get('turns', [])) == 1
                                     and warm['turns'][0]['metrics'].get('completion_tokens') == 512
                                     and len(report['warmups']) == 5 and all(c['status'] == 'ok' for c in report['warmups']))
        report['finalization_errors'] = finalization_errors
        report['run_complete'] = complete(report) and not finalization_errors
        report['status'] = 'ok' if complete(report) and report['warmup_complete'] and not report.get('error') and report['deployment_changed_files'] == [] and not report.get('unexpected_native_phase_lines') and all(c['status'] == 'ok' for c in report['cases']) else 'fail'
        report['descriptive_groups'] = {name: numeric_summary(rows) for name, rows in
                                        [('all18', report['cases']), ('first3', report['cases'][:3]), ('later15', report['cases'][3:])]}
        r2.write(path, report)
    return report


def compare(r2, baseline, final):
    issues = []
    for version, report in [('baseline', baseline), ('final', final)]:
        if not complete(report): issues.append(version + ': incomplete18-case coverage')
        if report.get('status') != 'ok': issues.append(version + ': response/warmup/host failure')
        if not report.get('telemetry_qualification', {}).get('qualified'): issues.append(version + ': telemetry unqualified')
    for field in ('weights_id', 'sampling', 'thinking', 'stream', 'environment', 'harness_sha256', 'runtime_environment_policy'):
        if baseline.get(field) != final.get(field): issues.append(field + ': mismatch')
    for field, tolerance in [('clocks.sm', .03), ('clocks.mem', .005)]:
        values = [r.get('telemetry_qualification', {}).get('gpu7', {}).get(field, {}).get('median', 0) for r in (baseline, final)]
        if any(not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0 for v in values) or abs(values[0] - values[1]) / values[0] > tolerance:
            issues.append('Paired clock mismatch: ' + field)
    pairs = []
    for i in range(18):
        a = next((c for c in baseline['cases'] if c['iteration'] == i), None)
        b = next((c for c in final['cases'] if c['iteration'] == i), None)
        errors = []
        if not a or not b: errors.append('Missing logical iteration')
        else:
            if a['status'] != 'ok' or b['status'] != 'ok': errors.append('Semantic/structural failure')
            if r2.request_identity(a) != r2.request_identity(b): errors.append('Exact request mismatch')
            if r2.output_identity(a) != r2.output_identity(b): errors.append('Exact output/token/finish mismatch')
            if any(len(c.get('turns', [])) != 1 or c['turns'][0]['metrics'].get('prompt_tokens') != 92
                   or c['turns'][0]['metrics'].get('completion_tokens') != 16 for c in (a,b)):
                errors.append('Expected92prompt/16completion counts differ')
        pairs.append({'iteration': i, 'baseline_id': a.get('logical_id') if a else None,
                      'final_id': b.get('logical_id') if b else None, 'comparable': not errors, 'issues': errors})
    if any(not p['comparable'] for p in pairs): issues.append('At least one of all18 paired requests is not comparable')
    summaries = {version: report['descriptive_groups'] for version, report in [('baseline', baseline), ('final', final)]}
    for version in summaries:
        if any(not summaries[version]['all18'][field]['all_valid'] for field in ('ttft_ms', 'total_wall_ms', 'wave_wall_ms')):
            issues.append(version + ': invalid timing values')
    ratios = {} if issues else {field: summaries['baseline']['all18'][field]['median'] / summaries['final']['all18'][field]['median'] for field in ('ttft_ms', 'total_wall_ms', 'wave_wall_ms')}
    return {'baseline_job': baseline['job'], 'final_job': final['job'], 'whole18_comparable': not issues,
            'issues': issues, 'case_pairs': pairs, 'summaries': summaries,
            'whole18_speedup_final_over_baseline': ratios,
            'scope': 'No selected successful subset: all18 pairs must match and pass telemetry. First3/later15 summaries are descriptive only; all outliers retained. Short JSON decode is not sustained decode performance.'}


def execute(cross, r2, args):
    if not args.exclusive_window: raise ValueError('An explicitly released exclusive window is required before launching')
    if args.out.exists(): raise FileExistsError('Refusing existing output')
    cross.pin(args.cross_manifest, CROSS_MANIFEST_SHA)
    previous = json.loads(args.cross_manifest.read_text())
    if not previous['run_complete'] or not previous['all_cases_passed']: raise ValueError('Prior crossing incomplete')
    original, refs, weights = cross.validate_inputs(r2, args)
    cross.pin(r2.common.WORK / 'dotnet/dotnet', previous['dotnet_executable']['sha256'])
    if cross.source_hosts_unchanged(previous): raise ValueError('Frozen cross hosts changed')
    protected = [args.cross_manifest.parent.resolve(), args.r2_manifest.parent.resolve()] + [Path(d['target']).resolve() for d in list(previous['deployments'].values()) + list(original['deployments'].values())]
    if any(args.out.resolve() == p or p in args.out.resolve().parents or args.out.resolve() in p.parents for p in protected): raise ValueError('Output overlaps retained host/evidence')
    args.out.mkdir(parents=True)
    result = {'scope': SCOPE, 'run_complete': False, 'all_cases_passed': False, 'all_pairs_comparable': False,
              'runner_source': r2.source(Path(__file__)), 'cross_runner': r2.source(HERE / 'run-qwen35-cross-phase.py'),
              'r2_runner': r2.source(HERE / 'run-final-json-performance.py'), 'shared_helper': r2.source(HERE / 'run-final-existing-models.py'),
              'cross_manifest': r2.source(args.cross_manifest), 'r2_manifest': r2.source(args.r2_manifest),
              'dotnet_executable': previous['dotnet_executable'], 'harness_sha256': cross.HARNESS_SHA,
              'weights': weights, 'planned_versions': list(VERSIONS), 'expected_cases': 72,
              'excluded_decode512_warmups': 4, 'excluded_json_warmups': 20,
              'deployments': {v: previous['deployments'][v + '-managed_final-native'] for v in ('baseline','final')},
              'runs': [], 'comparisons': []}
    path = args.out / 'run.json'; r2.write(path, result)
    reports = []
    try:
        for index, version in enumerate(VERSIONS):
            if args.stop_file.exists(): raise InterruptedError('Resource window closed')
            report = run_job(cross, r2, args, index, version, result['deployments'][version], refs, weights)
            reports.append(report)
            report_path = args.out / ('job' + str(index) + '-' + version + '.json')
            result['runs'].append({'job': index, 'version': version, 'source': r2.source(report_path),
                                   'complete': complete(report), 'status': report['status'],
                                   'failed_logical_ids': [c['logical_id'] for c in report['cases'] if c['status'] != 'ok']})
            r2.write(path, result)
            if report['deployment_changed_files']: raise RuntimeError('Frozen host mutated; remaining jobs refused')
            if report.get('owned_process_exit_observed') is False:
                raise RuntimeError('Owned host still alive; marker retained and remaining jobs refused')
        result['run_complete'] = len(reports) == 4 and all(complete(r) for r in reports)
        result['all_cases_passed'] = result['run_complete'] and all(r['status'] == 'ok' for r in reports)
        result['comparisons'] = [compare(r2, reports[0], reports[1]), compare(r2, reports[3], reports[2])]
        result['all_pairs_comparable'] = all(r['whole18_comparable'] for r in result['comparisons'])
    except Exception as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        result['original_r2_changed_files'] = cross.source_hosts_unchanged(original)
        result['cross_host_changed_files'] = cross.source_hosts_unchanged(previous)
        result['dotnet_changed'] = cross.sha(r2.common.WORK / 'dotnet/dotnet') != previous['dotnet_executable']['sha256']
        if result['original_r2_changed_files'] or result['cross_host_changed_files'] or result['dotnet_changed']:
            result['run_complete'] = result['all_cases_passed'] = result['all_pairs_comparable'] = False
        result['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        r2.write(path, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    work = Path('/workspace/deepseek41-work')
    parser.add_argument('--r2-manifest', type=Path, default=work / 'existing-regressions/final3651-native6b3-json-performance-r2/run.json')
    parser.add_argument('--cross-manifest', type=Path, default=work / 'existing-regressions/qwen35-managed-native-cross-phase-r1/run.json')
    parser.add_argument('--harness-dir', type=Path, default=Path('/workspace/TensorSharp/benchmarks/engine_comparison'))
    parser.add_argument('--out', type=Path, default=work / 'existing-regressions/qwen35-solo72-final-native-r1')
    parser.add_argument('--stop-file', type=Path, default=work / 'stop-qwen35-solo72')
    parser.add_argument('--exclusive-window', action='store_true')
    args = parser.parse_args(); cross = load_cross()
    result = execute(cross, cross.load_runner(args.harness_dir), args)
    return 0 if result['all_cases_passed'] and result['all_pairs_comparable'] else 1


if __name__ == '__main__': raise SystemExit(main())
