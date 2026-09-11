#!/usr/bin/env python3
"""VM-specific JSON grammar A/B: 45 timed cases/build, 90 total, plus JSON warmups.

Never builds, downloads, changes old hosts/reports, or changes GPU clock settings.
Timings require an externally exclusive window and sampled telemetry checks.
Short JSON decode throughput is not a sustained 512-token decode measurement.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import csv
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import shutil
import socket
import statistics
import subprocess
import sys
import threading
import time

HELPER = Path(__file__).with_name('run-final-existing-models.py')
HELPER_SHA = 'cb1b1a0caf232fb8386c89d87cf09a492b1429c6b8665a14d5ab39b9e215c5e4'
import hashlib
if hashlib.sha256(HELPER.read_bytes()).hexdigest() != HELPER_SHA:
    raise RuntimeError('Reviewed shared runner helper has changed')
spec = importlib.util.spec_from_file_location('final_correctness_runner', HELPER)
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)
validation = common.validation
WORK = common.WORK
BASELINE_NATIVE_SHA = 'e67e4c922138a7038bfc07d110d22eeec0f3f4cf10d545fae2971ca3f7dc3093'
BASELINE_RUNTIME_SHA = 'abde2278d6c9b885c18dc37e2d93c8f273aecc68568f13c49c31cd0edc4bdc4b'
BINARY_NAMES = ('TensorSharp.Server.Host.dll', 'TensorSharp.Runtime.dll', 'TensorSharp.Models.dll',
                'TensorSharp.Chat.dll', 'TensorSharp.Server.dll', 'libGgmlOps.so')
NAMES = ('qwen3', 'qwen35', 'gemma4')
LABEL = 'final3651-native6b3-json-performance'
GPU_FIELDS = 'index,uuid,clocks.sm,clocks.mem,power.draw,power.limit,temperature.gpu,utilization.gpu,memory.used'
write = common.write
source = common.source


def key(case):
    return case['scenario'], case['tag'], case['concurrency'], case.get('repeat', 0)


def planned_keys():
    return {('json', f'json-c{c}-r{r}-i{i}', c, r) for c in (1, 4) for r in range(3) for i in range(c)}


def complete(report):
    actual = [key(c) for c in report.get('cases', [])]
    waves = [(w['concurrency'], w['repeat']) for w in report.get('waves', [])]
    reconciled = all(w.get('generated_tokens') == sum(t['metrics'].get('completion_tokens', 0)
                     for c in report.get('cases', []) if c['concurrency'] == w['concurrency'] and c.get('repeat', 0) == w['repeat']
                     for t in c.get('turns', [])) for w in report.get('waves', []))
    return (report.get('run_complete') is True and reconciled and len(actual) == len(set(actual)) == 15
            and set(actual) == planned_keys() and len(waves) == len(set(waves)) == 6
            and set(waves) == {(c, r) for c in (1, 4) for r in range(3)})


def job_plan():
    return [{'model': model, 'versions': ['baseline', 'final'] if index % 2 == 0 else ['final', 'baseline'],
             'scenario': 'json', 'concurrency': [1, 4], 'repeats': 3,
             'timed_cases_per_version': 15, 'decode512_warmups_per_version': 1, 'json_warmups_per_version': 5}
            for index, model in enumerate(NAMES)]


def capture(command):
    result = subprocess.run(command, text=True, capture_output=True, timeout=15)
    if result.returncode:
        raise RuntimeError(f'{command[0]} query failed: {result.stderr.strip()}')
    return result.stdout


def gpu_clients():
    text = capture(['nvidia-smi', '--query-compute-apps=pid,gpu_uuid', '--format=csv,noheader,nounits'])
    return [{'pid': int(row[0].strip()), 'gpu_uuid': row[1].strip()}
            for row in csv.reader(io.StringIO(text)) if row]


def cpu_ticks():
    """Per-process tick snapshots avoid misleading lifetime ps %CPU values."""
    result = {}
    for directory in Path('/proc').iterdir():
        if not directory.name.isdigit():
            continue
        try:
            text = (directory / 'stat').read_text()
            end = text.rindex(')')
            fields = text[end + 2:].split()
            result[int(directory.name)] = {'comm': text[text.index('(') + 1:end],
                                          'ticks': int(fields[11]) + int(fields[12]),
                                          'start': int(fields[19]), 'ppid': int(fields[1])}
        except (OSError, ValueError, IndexError):
            continue
    return result


def owned_tree(ticks, roots):
    owned = set(roots)
    while True:
        expanded = owned | {pid for pid, row in ticks.items() if row['ppid'] in owned}
        if expanded == owned:
            return owned
        owned = expanded


def telemetry_sample():
    text = capture(['nvidia-smi', '--query-gpu=' + GPU_FIELDS, '--format=csv,noheader,nounits'])
    fields = GPU_FIELDS.split(',')
    return {'monotonic': time.monotonic(), 'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
            'gpus': [dict(zip(fields, (cell.strip() for cell in row))) for row in csv.reader(io.StringIO(text)) if row],
            'compute_clients': gpu_clients(), 'cpu': cpu_ticks()}


class Telemetry:
    def __init__(self, path):
        self.path, self.samples, self.errors = path, [], []
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self.run, name='json-benchmark-telemetry', daemon=True)

    def start(self):
        self.thread.start()

    def run(self):
        with self.path.open('w') as stream:
            while not self.stop_event.is_set():
                try:
                    sample = telemetry_sample()
                    self.samples.append(sample)
                    stream.write(json.dumps(sample) + '\n')
                    stream.flush()
                except Exception as error:
                    self.errors.append(type(error).__name__ + ': ' + str(error))
                self.stop_event.wait(1)

    def stop(self):
        self.stop_event.set()
        self.thread.join(timeout=35)
        if self.thread.is_alive():
            self.errors.append('Telemetry thread did not finish')


def qualify_telemetry(samples, errors, owner_pid, interval, exclusive):
    issues = list(errors)
    sampled = [s for s in samples if interval[0] <= s['monotonic'] <= interval[1]]
    if not exclusive:
        issues.append('Externally exclusive window was not declared')
    if len(sampled) < 2:
        issues.append('Fewer than two telemetry samples in the timed interval')
    gpu7, competitors, cpu_competitors = [], [], []
    for sample in sampled:
        competitors += [row for row in sample['compute_clients'] if row['pid'] != owner_pid]
        rows = [row for row in sample['gpus'] if row['index'] == '7']
        if len(rows) != 1:
            issues.append('GPU7 missing or duplicated in telemetry')
        else:
            gpu7 += rows
            if not any(row['pid'] == owner_pid and row['gpu_uuid'] == rows[0]['uuid'] for row in sample['compute_clients']):
                issues.append('Owned server was not recorded as a GPU7 compute client')
    if competitors:
        issues.append('A foreign GPU compute client was sampled')
    for before, after in zip(sampled, sampled[1:]):
        elapsed = after['monotonic'] - before['monotonic']
        allowed = owned_tree(after['cpu'], {owner_pid, os.getpid()})
        for pid, row in after['cpu'].items():
            old = before['cpu'].get(pid)
            if pid not in allowed and old and old['start'] == row['start'] and elapsed > 0:
                pct = max(0, row['ticks'] - old['ticks']) / os.sysconf('SC_CLK_TCK') / elapsed * 100
                if pct > 10:
                    cpu_competitors.append({'pid': pid, 'comm': row['comm'], 'one_core_cpu_percent': pct})
    if cpu_competitors:
        issues.append('A foreign CPU process exceeded 10% of one core between telemetry samples')
    stats = {}
    for field in GPU_FIELDS.split(',')[2:]:
        try:
            values = [float(row[field]) for row in gpu7]
            if any(not math.isfinite(value) for value in values):
                raise ValueError('Nonfinite telemetry')
            if values:
                stats[field] = {'min': min(values), 'median': statistics.median(values), 'max': max(values)}
        except ValueError:
            issues.append('Nonnumeric GPU telemetry field: ' + field)
    for field, spread in [('clocks.sm', .03), ('clocks.mem', .005)]:
        value = stats.get(field)
        if not value or value['min'] <= 0 or (value['max'] - value['min']) / value['median'] > spread:
            issues.append('Sampled clock spread exceeds policy: ' + field)
    return {'qualified': not issues, 'issues': sorted(set(issues)), 'samples_in_timed_interval': len(sampled),
            'gpu7': stats, 'foreign_gpu_clients': competitors, 'foreign_cpu_processes': cpu_competitors,
            'policy': {'external_exclusive_declaration': exclusive, 'sample_period_seconds': 1,
                       'maximum_sm_clock_spread_fraction': .03, 'maximum_memory_clock_spread_fraction': .005,
                       'foreign_cpu_limit_percent_of_one_core': 10},
            'limitation': 'Sampling cannot prove every in-flight clock or exclude sub-second contention. No clocks/power limits are changed.'}


def freeze_version(host, target, expected, native=None):
    if target.exists():
        raise FileExistsError('Refusing existing deployment: ' + str(target))
    actual = {name: common.file_sha(host / name) for name in BINARY_NAMES}
    for name in BINARY_NAMES:
        if name == 'libGgmlOps.so' and native:
            continue
        if actual[name] != expected[name]:
            raise ValueError('Source host differs from reviewed evidence: ' + name)
    if native and common.file_sha(native) != expected['libGgmlOps.so']:
        raise ValueError('Replacement native differs from reviewed evidence')
    shutil.copytree(host, target, symlinks=False)
    if native:
        shutil.copyfile(native, target / 'libGgmlOps.so')
    manifest = {'source_host': str(host), 'target': str(target),
                'files_sha256': {str(p.relative_to(target)): common.file_sha(p) for p in sorted(target.rglob('*')) if p.is_file()},
                'expected_core_binaries': expected}
    write(target.parent / 'deployment.json', manifest)
    return manifest


def request_identity(case):
    return {'initial_sha256': case.get('input_sha256'),
            'turn_requests': [turn['request'] for turn in case.get('turns', [])]}


def output_identity(case):
    return [{'assistant_message': t['metrics'].get('assistant_message'),
             'finish_reason': t['metrics'].get('finish_reason'),
             'prompt_tokens': t['metrics'].get('prompt_tokens'),
             'completion_tokens': t['metrics'].get('completion_tokens'),
             'usage_present': t['metrics'].get('usage_present')}
            for t in case.get('turns', [])]


def compare(baseline, final):
    issues = []
    for version, report in [('baseline', baseline), ('final', final)]:
        if not complete(report):
            issues.append(version + ' does not have all 15 unique cases and six waves')
        if not report.get('telemetry_qualification', {}).get('qualified'):
            issues.append(version + ' telemetry is unqualified')
        if report.get('deployment_changed_files') or report.get('error'):
            issues.append(version + ' run/deployment integrity failed')
        if len(report.get('warmups', [])) != 5 or any(c['status'] != 'ok' for c in report.get('warmups', [])):
            issues.append(version + ' JSON warmup failed or is incomplete')
        warm = report.get('decode512_warmup', {})
        if (warm.get('status') != 'ok' or len(warm.get('turns', [])) != 1
                or warm['turns'][0]['metrics'].get('completion_tokens') != 512):
            issues.append(version + ' 512-token decode warmup failed or is incomplete')
    for field in ('weights_id', 'profile', 'sampling', 'thinking', 'stream', 'environment', 'harness_sha256'):
        if baseline.get(field) != final.get(field):
            issues.append(field + ' differs')
    for field, tolerance in [('clocks.sm', .03), ('clocks.mem', .005)]:
        a = baseline.get('telemetry_qualification', {}).get('gpu7', {}).get(field, {}).get('median', 0)
        b = final.get('telemetry_qualification', {}).get('gpu7', {}).get(field, {}).get('median', 0)
        if not all(isinstance(v, (int, float)) and math.isfinite(v) and v > 0 for v in (a, b)) or abs(a - b) / a > tolerance:
            issues.append('Paired median clock differs: ' + field)
    a = {key(c): c for c in baseline.get('cases', [])}
    b = {key(c): c for c in final.get('cases', [])}
    pairs, groups = [], {}
    for k in sorted(planned_keys()):
        x, y = a.get(k), b.get(k)
        reasons = []
        if x is None or y is None:
            reasons.append('Missing planned case')
        else:
            if x['status'] != 'ok' or y['status'] != 'ok':
                reasons.append('A response failed the unchanged semantic/structural validator')
            if request_identity(x) != request_identity(y):
                reasons.append('Request/hash mismatch')
            if output_identity(x) != output_identity(y):
                reasons.append('Exact response or token-count mismatch')
            for case in (x, y):
                if len(case.get('turns', [])) != 1:
                    reasons.append('Expected exactly one JSON response turn')
                for turn in case.get('turns', []):
                    metrics = turn['metrics']
                    if metrics.get('usage_present') is not True or metrics.get('prompt_tokens', 0) <= 0 or metrics.get('completion_tokens', 0) <= 1:
                        reasons.append('Missing positive prompt/decode token counts')
        pairs.append({'key': list(k), 'comparable': not reasons, 'reasons': sorted(set(reasons)),
                      'baseline_status': x.get('status') if x else None, 'final_status': y.get('status') if y else None,
                      'baseline_output_sha256': validation.digest(output_identity(x)) if x else None,
                      'final_output_sha256': validation.digest(output_identity(y)) if y else None})
    for degree in (1, 4):
        selected = [pair for pair in pairs if pair['key'][2] == degree]
        group_issues = issues + [str(p['key']) + ': ' + ', '.join(p['reasons']) for p in selected if not p['comparable']]
        group = {'comparable': not group_issues, 'issues': group_issues, 'expected_cases_per_build': 3 * degree,
                 'scope': 'Every response and prompt/completion count in this concurrency group must match; no selected successful subset.'}
        if not group_issues:
            summaries = {}
            metric_issues = {name: [] for name in ('ttft', 'json_decode', 'request_wall', 'whole_wave_wall', 'whole_wave_end_to_end_tps')}
            for pair in selected:
                k = tuple(pair['key'])
                if a[k]['turns'][0]['metrics'].get('decode_timing_source') != b[k]['turns'][0]['metrics'].get('decode_timing_source'):
                    metric_issues['json_decode'].append(str(k) + ': decode timing source differs')
            for version, report, cases in [('baseline', baseline, a), ('final', final, b)]:
                values = [cases[tuple(p['key'])]['turns'][0]['metrics'] for p in selected]
                waves = [w for w in report['waves'] if w['concurrency'] == degree]
                summary = {'completion_tokens': [m['completion_tokens'] for m in values]}
                for metric, field, label in [('ttft', 'ttft_ms', 'ttft_ms_median'), ('json_decode', 'decode_tps', 'json_decode_tps_median'), ('request_wall', 'total_wall_ms', 'request_wall_ms_median')]:
                    numbers = [m.get(field, 0) for m in values]
                    if any(not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0 for v in numbers):
                        metric_issues[metric].append(version + ': missing/invalid ' + field)
                    else:
                        summary[label] = statistics.median(numbers)
                for m in values:
                    timer = m.get('decode_timing_source')
                    first, last = m.get('t_first_abs'), m.get('t_last_abs')
                    if timer not in ('stream_window', 'server'):
                        metric_issues['json_decode'].append(version + ': unknown decode timer source')
                    elif timer == 'stream_window' and (not all(isinstance(t, (int, float)) and math.isfinite(t) for t in (first, last)) or last - first < .05):
                        metric_issues['json_decode'].append(version + ': streamed decode window shorter than50ms or missing timestamps')
                if any(not isinstance(w.get('wall_ms'), (int, float)) or not math.isfinite(w['wall_ms']) or w['wall_ms'] <= 0 for w in waves):
                    metric_issues['whole_wave_wall'].append(version + ': invalid wave wall time')
                    metric_issues['whole_wave_end_to_end_tps'].append(version + ': invalid wave wall time')
                else:
                    summary['whole_wave_wall_ms_median'] = statistics.median(w['wall_ms'] for w in waves)
                    summary['whole_wave_end_to_end_tps_median'] = statistics.median(w['generated_tokens'] / (w['wall_ms'] / 1000) for w in waves)
                summaries[version] = summary
            old, new = summaries['baseline'], summaries['final']
            ratios = {}
            for metric, field in [('ttft', 'ttft_ms_median'), ('json_decode', 'json_decode_tps_median'), ('request_wall', 'request_wall_ms_median'), ('whole_wave_wall', 'whole_wave_wall_ms_median'), ('whole_wave_end_to_end_tps', 'whole_wave_end_to_end_tps_median')]:
                if not metric_issues[metric]:
                    ratios[metric] = new[field] / old[field] if metric in ('json_decode', 'whole_wave_end_to_end_tps') else old[field] / new[field]
            group.update(summaries=summaries, metric_issues=metric_issues, speedup_final_over_baseline=ratios)
        groups[str(degree)] = group
    primary_metrics = {'ttft', 'request_wall', 'whole_wave_wall', 'whole_wave_end_to_end_tps'}
    return {'global_issues': issues, 'case_pairs': pairs, 'concurrency_groups': groups,
            'all_groups_comparable': all(g['comparable'] and primary_metrics <= g.get('speedup_final_over_baseline', {}).keys() for g in groups.values()),
            'failed_cases': {v: [c for c in r.get('cases', []) if c['status'] != 'ok'] for v, r in [('baseline', baseline), ('final', final)]},
            'scope': 'Whole-build regression comparison, not isolated Unicode-only causal attribution; native and other managed files differ. JSON responses are short, not a sustained 512-token decode workload.'}


def run_wave(url, model, degree, repeat, warmup=False):
    started = time.monotonic()
    with ThreadPoolExecutor(max_workers=degree) as pool:
        jobs = [pool.submit(validation.run_case, url, model, 'tensorsharp', 'json',
                            f'json-warmup-c{degree}-i{i}' if warmup else f'json-c{degree}-r{repeat}-i{i}',
                            timeout=90) for i in range(degree)]
        cases = [job.result() for job in jobs]
    wall_ms = (time.monotonic() - started) * 1000
    for case in cases:
        case.update(concurrency=degree, repeat=repeat)
    return cases, {'concurrency': degree, 'repeat': repeat, 'wall_ms': wall_ms,
                   'generated_tokens': sum(t['metrics'].get('completion_tokens', 0) for c in cases for t in c['turns']),
                   'all_passed': all(c['status'] == 'ok' for c in cases)}


def run_version(args, name, version, target, deployment, out, weights, historical, harness):
    path = out / f'{name}-{version}.json'
    report = {'model': name, 'version': version, 'run_complete': False, 'status': 'not_started',
              'execution_plan': {'scenario': 'json', 'concurrency': [1, 4], 'repeats': 3, 'expected_cases': 15, 'decode512_warmups': 1, 'json_warmups': 5},
              'weights_id': weights['sha256'], 'weights': weights, 'profile': common.PROFILE, 'thinking': False,
              'stream': True, 'sampling': validation.SAMPLING, 'environment': common.ENV,
              'harness_sha256': harness, 'binary_sha256': deployment['expected_core_binaries'],
              'cases': [], 'waves': [], 'warmups': [], 'historical_reference': historical['source']}
    command = common.launch_command(target, common.MODELS[name])
    report['launch'] = command
    env = {k: v for k, v in os.environ.items() if not k.startswith(('TS_', 'TENSORSHARP_', 'GGML_', 'KV_CACHE_', 'MAX_CONTEXT'))}
    env.update(common.ENV)
    process, monitor, interval = None, None, [0, 0]
    log = out / f'{name}-{version}-server.log'
    write(path, report)
    try:
        if args.stop_file.exists():
            raise InterruptedError('Resource window closed')
        if common.unchanged(target, deployment):
            raise RuntimeError('Frozen deployment changed before launch')
        if gpu_clients():
            raise RuntimeError('Foreign GPU client is resident; no endpoint was started')
        with socket.socket() as probe:
            if probe.connect_ex(('127.0.0.1', 5011)) == 0:
                raise RuntimeError('Port5011 occupied; refusing another endpoint')
        report['safe_before_snapshot'] = common.snapshot()
        with log.open('w') as stream:
            process = subprocess.Popen(command, env=env, cwd=target, stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
        write(out / 'active-server.json', {'pid': process.pid, 'model': name, 'version': version, 'command': command})
        url, model_id, deadline = 'http://127.0.0.1:5011', None, time.monotonic() + 180
        while time.monotonic() < deadline:
            if args.stop_file.exists():
                raise InterruptedError('Resource window closed')
            if process.poll() is not None:
                raise RuntimeError('Owned server exited before readiness')
            try:
                response = common.requests.get(url + '/v1/models', timeout=2)
                rows = response.json().get('data', []) if response.status_code == 200 else []
                if rows:
                    model_id = rows[0]['id']
                    break
            except (common.requests.RequestException, ValueError):
                pass
            time.sleep(.5)
        if model_id is None:
            raise TimeoutError('Server startup exceeded180seconds')
        report.update(status='running', served_model_id=model_id)
        report['decode512_warmup'] = validation.run_case(url, model_id, 'tensorsharp', 'decode', 'warmup-decode', timeout=90)
        write(path, report)
        for degree in (1, 4):
            cases, _ = run_wave(url, model_id, degree, 0, warmup=True)
            report['warmups'] += cases
            write(path, report)
        monitor = Telemetry(out / f'{name}-{version}-telemetry.jsonl')
        monitor.start()
        interval[0] = time.monotonic()
        for degree in (1, 4):
            for repeat in range(3):
                if args.stop_file.exists():
                    raise InterruptedError('Resource window closed')
                cases, wave = run_wave(url, model_id, degree, repeat)
                report['cases'] += cases
                report['waves'].append(wave)
                write(path, report)
                print(name, version, degree, repeat, [c['status'] for c in cases], flush=True)
        interval[1] = time.monotonic()
        report['run_complete'] = True
        report['run_complete'] = complete(report)
    except Exception as error:
        report['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        if not interval[1]:
            interval[1] = time.monotonic()
        report['timed_monotonic_interval'] = interval
        try:
            if monitor:
                monitor.stop()
                report['telemetry_source'] = source(monitor.path)
                report['telemetry_qualification'] = qualify_telemetry(monitor.samples, monitor.errors, process.pid, interval, args.exclusive_window)
        except Exception as error:
            report['telemetry_finalization_error'] = type(error).__name__ + ': ' + str(error)
            report['telemetry_qualification'] = {'qualified': False, 'issues': [report['telemetry_finalization_error']]}
        finally:
            # Never let evidence-file or monitor failures leave our model resident.
            common.stop(process)
        (out / 'active-server.json').unlink(missing_ok=True)
        report['deployment_changed_files'] = common.unchanged(target, deployment)
        report['safe_after_snapshot'] = common.snapshot()
        if log.exists():
            report['server_log'] = source(log)
        report['status'] = 'ok' if complete(report) and not report.get('error') and not report['deployment_changed_files'] and report.get('decode512_warmup', {}).get('status') == 'ok' and all(c['status'] == 'ok' for c in report['cases'] + report['warmups']) else 'fail'
        report['summary'] = validation.summarize(report['cases'])
        write(path, report)
    return report


def validate_inputs(args):
    quality = json.loads(args.quality_run.read_text())
    if (quality.get('run_complete') is not True or quality.get('expected_quality_cases') != 75
            or quality.get('expected_unicode_cases') != 15 or quality.get('planned_models') != list(NAMES)
            or len(quality.get('runs', [])) != 3):
        raise ValueError('Prior final75+15 quality run must be complete before this performance run')
    baseline, final_binaries = {}, None
    for name in NAMES:
        path = args.baseline_dir / f'{name}-tensorsharp.json'
        old = json.loads(path.read_text())
        json_cases = [c for c in old['cases'] if c['scenario'] == 'json']
        if len(json_cases) != 15 or {key(c) for c in json_cases} != planned_keys():
            raise ValueError('Historical performance reference lacks15unique JSON cases: ' + name)
        if old['binary_sha256']['libGgmlOps.so'] != BASELINE_NATIVE_SHA:
            raise ValueError('Historical baseline native does not match preserved HEAD control')
        if old['binary_sha256']['TensorSharp.Runtime.dll'] != BASELINE_RUNTIME_SHA:
            raise ValueError('Historical baseline Runtime does not match preserved HEAD control')
        expected = {'profile': common.PROFILE, 'environment': common.ENV, 'sampling': validation.SAMPLING,
                    'thinking': False, 'stream': True, 'engine': 'tensorsharp'}
        if any(old.get(k) != v for k, v in expected.items()):
            raise ValueError('Historical baseline settings differ from the reviewed old profile: ' + name)
        for case in json_cases:
            initial = {**validation.case_spec('json', case['tag']), 'sampling': validation.SAMPLING, 'thinking': False, 'stream': True}
            if case['input_sha256'] != validation.digest(initial):
                raise ValueError('Portable JSON request changed from old benchmark: ' + case['tag'])
        entry = next((r for r in quality['runs'] if r['model'] == name), None)
        if entry is None:
            raise ValueError('Final quality manifest is missing a model')
        for suite, count in [('quality', 25), ('unicode', 5)]:
            meta = entry['suites'][suite]
            child_path = Path(meta['source']['path'])
            child = json.loads(child_path.read_text())
            if (not meta['complete'] or meta['cases'] != count or common.file_sha(child_path) != meta['source']['sha256']
                    or child.get('run_complete') is not True or not common.coverage(child, common.SUITES[suite])):
                raise ValueError('Final quality child missing/incomplete/changed: ' + name + ' ' + suite)
            if final_binaries is not None and final_binaries != child['binary_sha256']:
                raise ValueError('Final quality children used different binaries')
            final_binaries = child['binary_sha256']
        baseline[name] = {'report': old, 'source': source(path)}
    if final_binaries['libGgmlOps.so'] != common.NATIVE_SHA:
        raise ValueError('Final quality native is not6b3')
    return baseline, final_binaries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-host-dir', type=Path, default=Path('/workspace/TensorSharp-baseline/TensorSharp.Server.Host/bin'))
    parser.add_argument('--final-host-dir', type=Path, default=WORK / 'regression-final3651-native6b3/TensorSharp.Server.Host/bin')
    parser.add_argument('--final-native', type=Path, default=WORK / 'native-v41-shared-pins-6b3b5ab3.so')
    parser.add_argument('--baseline-dir', type=Path, default=WORK / 'existing-regressions/performance-baseline')
    parser.add_argument('--quality-run', type=Path, default=WORK / 'existing-regressions/final3651-native6b3/run.json')
    parser.add_argument('--label', default=LABEL)
    parser.add_argument('--stop-file', type=Path)
    parser.add_argument('--exclusive-window', action='store_true', help='Declare root has reserved CPU/GPU/I/O; sampled checks are still required')
    parser.add_argument('--plan-only', action='store_true', help='Print the planned jobs without reading VM paths or creating files')
    args = parser.parse_args()
    plan = job_plan()
    if args.plan_only:
        print(json.dumps({'planned_jobs': plan, 'timed_cases': 90, 'excluded_decode512_warmups': 6, 'excluded_json_warmups': 30, 'runner_source': source(Path(__file__)), 'shared_helper': source(HELPER)}, indent=2))
        return 0
    if not args.label or Path(args.label).name != args.label:
        parser.error('Invalid label')
    out = WORK / 'existing-regressions' / args.label
    deployments = WORK / ('regression-' + args.label)
    if out.exists() or deployments.exists():
        parser.error('Output/deployment already exists; use a new label')
    args.stop_file = args.stop_file or out / 'STOP'
    if args.stop_file.exists():
        parser.error('STOP already exists; no deployment or endpoint created')
    historical, final_binaries = validate_inputs(args)
    weights = {name: common.identity(name, common.MODELS[name]) for name in NAMES}
    for name in NAMES:
        if weights[name]['sha256'] != historical[name]['report']['weights_id']:
            raise ValueError('Model bytes differ from historical reference: ' + name)
    baseline_binaries = historical[NAMES[0]]['report']['binary_sha256']
    if any(historical[name]['report']['binary_sha256'] != baseline_binaries for name in NAMES):
        raise ValueError('Historical baseline binaries differ between models')
    targets = {version: deployments / version / 'bin' for version in ('baseline', 'final')}
    frozen = {'baseline': freeze_version(args.baseline_host_dir, targets['baseline'], baseline_binaries),
              'final': freeze_version(args.final_host_dir, targets['final'], final_binaries, args.final_native)}
    out.mkdir(parents=True)
    harness = {Path(p).name: common.file_sha(Path(p)) for p in (validation.__file__, validation.engines.__file__, validation.scenarios.__file__)}
    overall = {'label': args.label, 'run_complete': False, 'all_cases_passed': False, 'all_groups_comparable': False,
               'planned_jobs': plan, 'expected_timed_cases': 90, 'excluded_decode512_warmups': 6, 'excluded_json_warmups': 30,
               'runner_source': source(Path(__file__)), 'shared_helper': source(HELPER), 'harness_sha256': harness,
               'preceding_quality_run': source(args.quality_run), 'deployments': frozen, 'runs': [], 'comparisons': {}}
    write(out / 'run.json', overall)
    for job in plan:
        reports = {}
        for version in job['versions']:
            report = run_version(args, job['model'], version, targets[version], frozen[version], out, weights[job['model']], historical[job['model']], harness)
            reports[version] = report
            overall['runs'].append({'model': job['model'], 'version': version, 'report': source(out / f"{job['model']}-{version}.json"),
                                    'run_complete': report['run_complete'], 'status': report['status'], 'cases': len(report['cases'])})
            write(out / 'run.json', overall)
        overall['comparisons'][job['model']] = compare(reports['baseline'], reports['final'])
        write(out / 'run.json', overall)
    overall['run_complete'] = len(overall['runs']) == 6 and all(r['run_complete'] for r in overall['runs']) and sum(r['cases'] for r in overall['runs']) == 90
    overall['all_cases_passed'] = overall['run_complete'] and all(r['status'] == 'ok' for r in overall['runs'])
    overall['all_groups_comparable'] = overall['run_complete'] and all(c['all_groups_comparable'] for c in overall['comparisons'].values())
    overall['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    write(out / 'run.json', overall)
    print(json.dumps({'report': str(out / 'run.json'), 'run_complete': overall['run_complete'], 'all_cases_passed': overall['all_cases_passed'], 'all_groups_comparable': overall['all_groups_comparable']}, indent=2))
    return 0 if overall['all_cases_passed'] and overall['all_groups_comparable'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
