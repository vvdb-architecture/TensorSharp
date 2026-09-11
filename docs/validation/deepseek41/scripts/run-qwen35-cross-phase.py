#!/usr/bin/env python3
"""Diagnostic managed/native 2x2 using the immutable R2 request runner.

No builds, downloads, edits of old hosts, or qualified performance claims.
Each fresh process receives the exact R2 decode512 + five JSON warmups +15 cases.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import sys
import time

HERE = Path(__file__).resolve().parent
R2_MANIFEST_SHA = '7ba46725c2a73a7131b859855533b09cf864e4f5b6d64ecb494eb6f1eeb16763'
R2_RUNNER_SHA = '10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5'
HELPER_SHA = 'cb1b1a0caf232fb8386c89d87cf09a492b1429c6b8665a14d5ab39b9e215c5e4'
HARNESS_SHA = {
    'validate_inference.py': 'b791d4f6474f3ad188bd1e5145296d3b06879b768f94a16ca74a83fe38f6b077',
    'engines.py': '7a49619e5df4b9e2eeafad2ba32a802bc16ae44a32d35cbc639e5bab1c8956e0',
    'scenarios.py': 'b327e85d6292702e0addf678ddbb72b708e179a4b4c1ea3caeeaebe72e8233b8',
}
MODEL_SHA = '0ad885ffd4bb022fc4f0d33a3308fa108ef8613159d3b3a67e23abca056b7a6c'
JOBS = (('baseline', 'baseline'), ('baseline', 'final'), ('final', 'baseline'), ('final', 'final'))
SCOPE = ('Diagnostic only: TS_GGML_PHASE_TIMING=1 instruments every fresh host. '
         'Managed/native crossing isolates build components, not individual source edits. '
         'One fixed-order four-job run cannot establish a qualified latency regression or fix.')


def sha(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def pin(path, expected):
    if sha(path) != expected:
        raise ValueError('Pinned source changed: ' + str(path))


def load_runner(harness_dir):
    runner = HERE / 'run-final-json-performance.py'
    pin(runner, R2_RUNNER_SHA)
    pin(HERE / 'run-final-existing-models.py', HELPER_SHA)
    for name, expected in HARNESS_SHA.items():
        pin(harness_dir / name, expected)
    sys.path.insert(0, str(harness_dir))
    spec = importlib.util.spec_from_file_location('reviewed_r2_cross', runner)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # The helper inserts its normal VM harness directory. Verify the files that
    # Python actually imported, as well as the requested pinned source directory.
    for name, imported in [('validate_inference.py', module.validation),
                           ('engines.py', module.validation.engines),
                           ('scenarios.py', sys.modules['scenarios'])]:
        pin(Path(imported.__file__), HARNESS_SHA[name])
    return module


def tree_hashes(root):
    return {str(p.relative_to(root)): sha(p) for p in sorted(root.rglob('*')) if p.is_file()}


def source_hosts_unchanged(manifest):
    differences = {}
    for version, deployment in manifest['deployments'].items():
        actual = tree_hashes(Path(deployment['target']))
        expected = deployment['files_sha256']
        changed = sorted(k for k in actual.keys() | expected.keys() if actual.get(k) != expected.get(k))
        if changed:
            differences[version] = changed
    return differences


def validate_inputs(r2, args):
    pin(args.r2_manifest, R2_MANIFEST_SHA)
    manifest = json.loads(args.r2_manifest.read_text())
    if manifest.get('run_complete') is not True or manifest.get('expected_timed_cases') != 90:
        raise ValueError('Pinned R2 run is incomplete')
    if manifest['runner_source']['sha256'] != R2_RUNNER_SHA or manifest['shared_helper']['sha256'] != HELPER_SHA:
        raise ValueError('Pinned R2 implementation differs')
    if manifest['harness_sha256'] != HARNESS_SHA:
        raise ValueError('Pinned R2 harness differs')
    refs = {}
    for version in ('baseline', 'final'):
        entry = next(r for r in manifest['runs'] if r['model'] == 'qwen35' and r['version'] == version)
        path = args.r2_manifest.parent / Path(entry['report']['path']).name
        pin(path, entry['report']['sha256'])
        report = json.loads(path.read_text())
        if not r2.complete(report) or report['environment'] != r2.common.ENV:
            raise ValueError('Pinned Qwen3.5 request profile differs or is incomplete')
        if report['binary_sha256'] != manifest['deployments'][version]['expected_core_binaries']:
            raise ValueError('Pinned Qwen3.5 core binaries differ')
        if report['weights_id'] != MODEL_SHA or report['harness_sha256'] != HARNESS_SHA:
            raise ValueError('Pinned Qwen3.5 model or harness differs')
        for case in report['cases']:
            initial = {**r2.validation.case_spec('json', case['tag']), 'sampling': r2.validation.SAMPLING,
                       'thinking': False, 'stream': True}
            if case['input_sha256'] != r2.validation.digest(initial):
                raise ValueError('Pinned JSON request changed: ' + case['tag'])
        refs[version] = {'source': r2.source(path), 'report': report}
    changed = source_hosts_unchanged(manifest)
    if changed:
        raise ValueError('Frozen R2 hosts differ: ' + json.dumps(changed))
    weights = r2.common.identity('qwen35', r2.common.MODELS['qwen35'])
    if weights['sha256'] != MODEL_SHA:
        raise ValueError('Actual Qwen3.5 model differs')
    for field in ('path', 'bytes', 'mtime_ns'):
        if weights[field] != refs['baseline']['report']['weights'][field]:
            raise ValueError('Qwen3.5 model identity changed: ' + field)
    return manifest, refs, weights


def freeze_cross(r2, manifest, deployment_root):
    if deployment_root.exists():
        raise FileExistsError('Refusing to reuse any diagnostic host: ' + str(deployment_root))
    if source_hosts_unchanged(manifest):
        raise ValueError('R2 host changed before copying')
    deployments = {}
    for managed, native in JOBS:
        label = managed + '-managed_' + native + '-native'
        src = manifest['deployments'][managed]
        native_src = Path(manifest['deployments'][native]['target']) / 'libGgmlOps.so'
        expected = dict(src['expected_core_binaries'])
        expected['libGgmlOps.so'] = manifest['deployments'][native]['expected_core_binaries']['libGgmlOps.so']
        target = deployment_root / label / 'bin'
        target.parent.mkdir(parents=True)
        deployment = r2.freeze_version(Path(src['target']), target, expected,
                                       native=native_src if managed != native else None)
        expected_all = dict(src['files_sha256'])
        expected_all['libGgmlOps.so'] = expected['libGgmlOps.so']
        if deployment['files_sha256'] != expected_all:
            raise ValueError('Copy changed files other than the explicit native library: ' + label)
        deployment.update(managed_version=managed, native_version=native,
                          native_source=r2.source(native_src), source_r2_files_sha256=src['files_sha256'])
        r2.write(target.parent / 'deployment.json', deployment)
        deployments[label] = deployment
    if source_hosts_unchanged(manifest):
        raise ValueError('R2 host changed while copying')
    return deployments


def phase_evidence(r2, log):
    rows = []
    if log.is_file():
        for number, line in enumerate(log.read_text(errors='replace').splitlines(), 1):
            if '[phase]' not in line:
                continue
            fields = {key: float(value) for key, value in re.findall(r'([\w+_-]+)=([0-9.]+)(?:ms)?', line)}
            rows.append({'line': number, 'text': line, 'milliseconds': fields})
    verify = [r for r in rows if '[phase] Qwen3.5 model verify ' in r['text']]
    return {'source': r2.source(log) if log.is_file() else None, 'phase_lines': rows,
            'qwen35_verify_lines': len(verify), 'diagnostic_only': True,
            'scope': 'Includes startup/warmup and all requests. Native lines lack request IDs; do not assign them to individual concurrent requests without additional correlation.'}


def finish_report(r2, report, log, deployment, out):
    original = report.get('telemetry_qualification', {})
    report.update(diagnostic_only=True, performance_qualified=False, diagnostic_scope=SCOPE,
                  cross={'managed': deployment['managed_version'], 'native': deployment['native_version']})
    report['telemetry_qualification'] = {**original, 'qualified': False,
                                        'issues': list(original.get('issues', [])) + ['Native phase instrumentation; diagnostic timing only']}
    phases = phase_evidence(r2, log)
    phase_path = out / (log.stem + '-phases.json')
    r2.write(phase_path, phases)
    report['native_phase_evidence'] = r2.source(phase_path)
    report['native_phase_capture_ok'] = phases['qwen35_verify_lines'] > 0
    report['runtime_file_logs'] = {str(p.relative_to(out)): r2.source(p) for p in
                                 sorted((out / (log.stem.replace('-server', '') + '-file-logs')).rglob('*')) if p.is_file()}
    warm = report.get('decode512_warmup', {})
    report['warmup_coverage_ok'] = (len(report.get('warmups', [])) == 5 and len(warm.get('turns', [])) == 1
                                   and warm['turns'][0]['metrics'].get('completion_tokens') == 512)
    report['diagnostic_complete'] = (r2.complete(report) and report['native_phase_capture_ok']
                                      and report['warmup_coverage_ok'] and not report.get('error')
                                      and not report.get('deployment_changed_files'))
    r2.write(out / ('qwen35-' + report['version'] + '.json'), report)
    return report


def execute(r2, args):
    if args.out.exists() or args.deployments.exists():
        raise FileExistsError('Refusing to reuse any diagnostic output or host directory')
    manifest, refs, weights = validate_inputs(r2, args)
    destinations = [args.out.resolve(), args.deployments.resolve()]
    protected = [args.r2_manifest.parent.resolve()] + [Path(d['target']).resolve() for d in manifest['deployments'].values()]
    if any(a == b or a in b.parents or b in a.parents for a in destinations for b in protected):
        raise ValueError('Diagnostic destinations overlap retained R2 evidence or hosts')
    if destinations[0] == destinations[1] or destinations[0] in destinations[1].parents or destinations[1] in destinations[0].parents:
        raise ValueError('Diagnostic output and host trees must be separate')
    args.out.mkdir(parents=True)
    result = {'diagnostic_only': True, 'performance_qualified': False, 'scope': SCOPE, 'run_complete': False,
              'r2_manifest': r2.source(args.r2_manifest), 'runner_source': r2.source(Path(__file__)),
              'r2_runner': r2.source(HERE / 'run-final-json-performance.py'),
              'shared_helper': r2.source(HERE / 'run-final-existing-models.py'), 'harness_sha256': HARNESS_SHA,
              'dotnet_executable': r2.source(r2.common.WORK / 'dotnet/dotnet'),
              'weights': weights, 'expected_cases': 60, 'excluded_decode512_warmups': 4, 'excluded_json_warmups': 20,
              'planned_jobs': [{'managed': m, 'native': n} for m, n in JOBS], 'runs': []}
    path = args.out / 'run.json'
    r2.write(path, result)
    original_env = dict(r2.common.ENV)
    try:
        deployments = freeze_cross(r2, manifest, args.deployments)
        result['deployments'] = deployments
        r2.common.ENV['TS_GGML_PHASE_TIMING'] = '1'
        args.exclusive_window = False  # Instrumented timings never enter qualified comparisons.
        r2.write(path, result)
        for label, deployment in deployments.items():
            if args.stop_file.exists():
                raise InterruptedError('Resource window closed')
            report = r2.run_version(args, 'qwen35', label, Path(deployment['target']), deployment,
                                    args.out, weights, refs[deployment['managed_version']], HARNESS_SHA)
            finish_report(r2, report, args.out / ('qwen35-' + label + '-server.log'), deployment, args.out)
            result['runs'].append({'cross': report['cross'], 'source': r2.source(args.out / ('qwen35-' + label + '.json')),
                                   'diagnostic_complete': report['diagnostic_complete'], 'status': report['status'],
                                   'failed_cases': [list(r2.key(c)) for c in report['cases'] if c['status'] != 'ok']})
            r2.write(path, result)
            if report.get('deployment_changed_files'):
                raise RuntimeError('Frozen diagnostic host changed; refusing subsequent jobs')
        result['run_complete'] = len(result['runs']) == 4 and all(r['diagnostic_complete'] for r in result['runs'])
        result['all_cases_passed'] = result['run_complete'] and all(r['status'] == 'ok' for r in result['runs'])
    except Exception as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        r2.common.ENV.clear()
        r2.common.ENV.update(original_env)
        result['source_r2_changed_files'] = source_hosts_unchanged(manifest)
        result['diagnostic_host_changed_files'] = {}
        for label, d in result.get('deployments', {}).items():
            actual, expected = tree_hashes(Path(d['target'])), d['files_sha256']
            result['diagnostic_host_changed_files'][label] = sorted(k for k in actual.keys() | expected.keys()
                                                                    if actual.get(k) != expected.get(k))
        if result['source_r2_changed_files'] or any(result['diagnostic_host_changed_files'].values()):
            result['run_complete'] = False
            result['all_cases_passed'] = False
        result['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        r2.write(path, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    work = Path('/workspace/deepseek41-work')
    label = 'qwen35-managed-native-cross-phase-r1'
    parser.add_argument('--r2-manifest', type=Path, default=work / 'existing-regressions/final3651-native6b3-json-performance-r2/run.json')
    parser.add_argument('--harness-dir', type=Path, default=Path('/workspace/TensorSharp/benchmarks/engine_comparison'))
    parser.add_argument('--out', type=Path, default=work / 'existing-regressions' / label)
    parser.add_argument('--deployments', type=Path, default=work / ('regression-' + label))
    parser.add_argument('--stop-file', type=Path, default=work / 'stop-qwen35-cross-phase')
    args = parser.parse_args()
    result = execute(load_runner(args.harness_dir), args)
    return 0 if result.get('all_cases_passed') else 1


if __name__ == '__main__':
    sys.exit(main())
