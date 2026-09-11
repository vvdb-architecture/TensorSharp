#!/usr/bin/env python3
"""Two immutable-host EventPipe diagnostics; exact R2 warmups and30 JSON cases."""
import argparse
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import time

HERE = Path(__file__).resolve().parent
CROSS_RUNNER_SHA = '32219050b755a6b28a08de3c0255d929d53813e5378b022b00d435c4484104b2'
CROSS_MANIFEST_SHA = '2dff39c993f0d53868b9ddcc41fa5ca2681a10a9dfa3ea8a4c4e5c8c2a241994'
TOOL_EVIDENCE_SHA = '4b48f777a1b4714a4b1e08386ddc5a157c300cb71299766f1582e7a3cc3d604e'
JOBS = ('baseline-managed_final-native', 'final-managed_final-native')
PROFILES = 'dotnet-sampled-thread-time,dotnet-common'
# dotnet-common's documented0x100003801D plus contention0x4000; explicit
# configuration overrides that provider's profile settings, so retain all bits.
PROVIDERS = 'Microsoft-Windows-DotNETRuntime:0x100003C01D:4'
SCOPE = ('Diagnostic EventPipe sampled thread stacks plus GC/contention/threading events, '
         'with native phase timing. Thread samples include waiting and are not exact on-CPU time. '
         'The two builds share final native; no qualified performance claim or production edit.')


def load_cross():
    import hashlib
    path = HERE / 'run-qwen35-cross-phase.py'
    if hashlib.sha256(path.read_bytes()).hexdigest() != CROSS_RUNNER_SHA:
        raise ValueError('Reviewed cross runner changed')
    spec = importlib.util.spec_from_file_location('reviewed_cross_eventpipe', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def stamp():
    before = time.monotonic()
    unix = time.time()
    after = time.monotonic()
    return {'utc': datetime.fromtimestamp(unix, timezone.utc).isoformat(timespec='microseconds'),
            'unix_seconds': unix, 'monotonic': (before + after) / 2,
            'anchor_uncertainty_seconds': (after - before) / 2}


class TraceMonitor:
    """Wrap only R2's post-warmup telemetry lifecycle, leaving requests unchanged."""
    def __init__(self, base_factory, r2, args, path, trace_reports):
        self.base = base_factory(path)
        self.r2, self.args, self.trace_reports = r2, args, trace_reports
        self.collector = None
        self.stream = None
        self.finalized = False
        self.report = {'diagnostic_only': True, 'collection_complete': False, 'errors': [],
                       'trace_integrity': 'Pending offline raw-nettrace parse, event-loss and time-range checks'}

    @property
    def path(self): return self.base.path
    @property
    def samples(self): return self.base.samples
    @property
    def errors(self): return self.base.errors + self.report['errors']

    def save(self):
        self.r2.write(self.report_path, self.report)

    def start(self):
        active = json.loads((self.args.out / 'active-server.json').read_text())
        self.label = active['version']
        if self.label not in JOBS:
            raise ValueError('Unexpected trace target')
        self.report_path = self.args.out / ('qwen35-' + self.label + '-eventpipe.json')
        self.trace_path = self.args.out / ('qwen35-' + self.label + '.nettrace')
        self.log_path = self.args.out / ('qwen35-' + self.label + '-collector.log')
        if self.trace_path.exists() or self.log_path.exists():
            raise FileExistsError('Refusing an existing trace or collector log')
        command = [str(self.args.trace_tool), 'collect', '--process-id', str(active['pid']),
                   '--profile', PROFILES, '--providers', PROVIDERS, '--format', 'NetTrace', '--output', str(self.trace_path),
                   '--buffersize', '128']
        self.report.update(host_pid=active['pid'], version=self.label, command=command,
                           profiles=PROFILES, provider_semantics={'0x100003801D': 'Unchanged dotnet-common GC/loader/JIT/threading/compilation events', '0x4000': 'Additional contention'},
                           collector_environment={'DOTNET_ROOT': str(self.r2.common.WORK / 'dotnet')},
                           attach_requested=stamp(), host_sampling_rate_ms=1,
                           attach_scope='After the512-token and five JSON warmups, before all15 measured requests')
        self.trace_reports[self.label] = self.report
        self.save()
        self.base.start()
        env = dict(os.environ)
        env['DOTNET_ROOT'] = str(self.r2.common.WORK / 'dotnet')
        self.stream = self.log_path.open('w')
        self.collector = subprocess.Popen(command, env=env, stdout=self.stream, stderr=subprocess.STDOUT,
                                          start_new_session=True)
        self.report['collector_pid'] = self.collector.pid
        self.save()
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            if self.args.stop_file.exists():
                raise InterruptedError('Resource window closed during trace attach')
            if self.collector.poll() is not None:
                raise RuntimeError('Collector exited before collection readiness')
            text = self.log_path.read_text(errors='replace')
            # This pinned CLI suppresses its interactive "Recording trace"
            # banner when stdout is redirected. It prints Output File only
            # after StartEventPipeSession returns; nonempty data additionally
            # proves CopyToAsync is receiving the session stream.
            if ('Output File' in text and str(self.trace_path) in text
                    and self.trace_path.exists() and self.trace_path.stat().st_size > 0):
                self.report['collection_ready'] = stamp()
                self.save()
                return
            time.sleep(.1)
        raise TimeoutError('No recording-ready trace within45seconds; no measured requests started')

    def stop(self):
        if self.finalized:
            return
        self.finalized = True
        try:
            self.report['stop_requested'] = stamp()
            if self.collector:
                early = self.collector.poll()
                if early is not None:
                    self.report['errors'].append('Collector exited before requested stop: ' + str(early))
                else:
                    self.collector.send_signal(signal.SIGINT)
                    try:
                        self.collector.wait(timeout=45)
                    except subprocess.TimeoutExpired:
                        self.report['errors'].append('Collector failed to finalize within45seconds')
                        self.collector.kill()
                        self.collector.wait(timeout=5)
                self.report['collector_exit_code'] = self.collector.returncode
            if self.stream:
                self.stream.close()
            self.report['finalized'] = stamp()
            if hasattr(self, 'log_path') and self.log_path.exists():
                self.report['collector_log'] = self.r2.source(self.log_path)
                text = self.log_path.read_text(errors='replace')
                self.report['completed_message_present'] = 'Trace completed.' in text
                for line in text.splitlines():
                    if any(term in line.lower() for term in ('[error]', 'potentially broken', 'lost events', 'trace collection canceled')):
                        self.report['errors'].append(line)
            if hasattr(self, 'trace_path') and self.trace_path.exists():
                self.report['raw_nettrace'] = self.r2.source(self.trace_path)
            if (self.report.get('collection_ready') and not self.report['errors']
                    and self.report.get('collector_exit_code') == 0 and self.report.get('completed_message_present')
                    and self.report.get('raw_nettrace', {}).get('bytes', 0) > 0):
                self.report['collection_complete'] = True
        except Exception as error:
            self.report['errors'].append(type(error).__name__ + ': ' + str(error))
            if self.collector and self.collector.poll() is None:
                self.collector.kill()
                self.collector.wait(timeout=5)
        finally:
            # R2 calls common.stop(host) only AFTER this method returns. Thus CLR
            # rundown completes while the owned host still exists.
            try:
                self.base.stop()
            except Exception as error:
                self.report['errors'].append('Telemetry stop: ' + type(error).__name__ + ': ' + str(error))
                self.report['collection_complete'] = False
            finally:
                if hasattr(self, 'report_path'):
                    self.save()


def validate_tool(cross, args):
    cross.pin(args.tool_evidence, TOOL_EVIDENCE_SHA)
    evidence = json.loads(args.tool_evidence.read_text())
    for item in evidence['tool_files'] + evidence['help_sources']:
        cross.pin(Path(item['path']), item['sha256'])
    if str(args.trace_tool) != evidence['tool_executable'] or evidence['version'] != '10.0.745401':
        raise ValueError('Trace tool identity differs from reviewed CLI')
    return evidence


def execute(cross, r2, args):
    if args.out.exists():
        raise FileExistsError('Refusing existing diagnostic output')
    cross.pin(args.cross_manifest, CROSS_MANIFEST_SHA)
    previous = json.loads(args.cross_manifest.read_text())
    if not previous['run_complete'] or not previous['all_cases_passed']:
        raise ValueError('Prior four-job cross must be complete')
    original, refs, weights = cross.validate_inputs(r2, args)
    tool = validate_tool(cross, args)
    protected = [args.cross_manifest.parent.resolve(), args.r2_manifest.parent.resolve()]
    protected += [Path(d['target']).resolve() for d in previous['deployments'].values()]
    protected += [Path(d['target']).resolve() for d in original['deployments'].values()]
    if any(args.out.resolve() == p or p in args.out.resolve().parents or args.out.resolve() in p.parents for p in protected):
        raise ValueError('Output overlaps retained host/evidence')
    deployments = {label: previous['deployments'][label] for label in JOBS}
    for label, d in deployments.items():
        if d['native_version'] != 'final' or cross.tree_hashes(Path(d['target'])) != d['files_sha256']:
            raise ValueError('Selected immutable cross host changed: ' + label)
    args.out.mkdir(parents=True)
    result = {'diagnostic_only': True, 'performance_qualified': False, 'scope': SCOPE, 'run_complete': False,
              'runner_source': r2.source(Path(__file__)), 'cross_manifest': r2.source(args.cross_manifest),
              'cross_runner': r2.source(HERE / 'run-qwen35-cross-phase.py'),
              'r2_runner': r2.source(HERE / 'run-final-json-performance.py'),
              'shared_helper': r2.source(HERE / 'run-final-existing-models.py'),
              'r2_manifest': r2.source(args.r2_manifest), 'tool_evidence': r2.source(args.tool_evidence),
              'trace_tool': tool, 'profiles': PROFILES, 'providers': PROVIDERS, 'expected_cases': 30,
              'excluded_decode512_warmups': 2, 'excluded_json_warmups': 10,
              'harness_sha256': cross.HARNESS_SHA, 'weights': weights, 'deployments': deployments,
              'planned_jobs': list(JOBS), 'runs': []}
    path = args.out / 'run.json'
    r2.write(path, result)
    original_env, original_monitor = dict(r2.common.ENV), r2.Telemetry
    traces = {}
    try:
        r2.common.ENV.update(TS_GGML_PHASE_TIMING='1', DOTNET_EventPipeThreadSamplingRate='1')
        args.exclusive_window = False
        r2.Telemetry = lambda p: TraceMonitor(original_monitor, r2, args, p, traces)
        for label, deployment in deployments.items():
            if args.stop_file.exists():
                raise InterruptedError('Resource window closed')
            report = r2.run_version(args, 'qwen35', label, Path(deployment['target']), deployment,
                                    args.out, weights, refs[deployment['managed_version']], cross.HARNESS_SHA)
            cross.finish_report(r2, report, args.out / ('qwen35-' + label + '-server.log'), deployment, args.out)
            trace = traces.get(label, {})
            report['eventpipe_scope'] = SCOPE
            report['eventpipe_complete'] = trace.get('collection_complete') is True
            trace_path = args.out / ('qwen35-' + label + '-eventpipe.json')
            if trace_path.is_file():
                trace['actual_case_monotonic_interval'] = report['timed_monotonic_interval']
                trace['expected_measured_cases'] = 15
                r2.write(trace_path, trace)
            report['eventpipe_evidence'] = r2.source(trace_path) if trace_path.is_file() else None
            report['diagnostic_complete'] = report['diagnostic_complete'] and report['eventpipe_complete']
            report_path = args.out / ('qwen35-' + label + '.json')
            r2.write(report_path, report)
            result['runs'].append({'version': label, 'source': r2.source(report_path),
                                   'diagnostic_complete': report['diagnostic_complete'], 'status': report['status'],
                                   'failed_cases': [list(r2.key(c)) for c in report['cases'] if c['status'] != 'ok']})
            r2.write(path, result)
            if report.get('deployment_changed_files'):
                raise RuntimeError('Immutable host changed; refusing second job')
        result['run_complete'] = len(result['runs']) == 2 and all(r['diagnostic_complete'] for r in result['runs'])
        result['all_cases_passed'] = result['run_complete'] and all(r['status'] == 'ok' for r in result['runs'])
    except Exception as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        r2.Telemetry = original_monitor
        r2.common.ENV.clear(); r2.common.ENV.update(original_env)
        result['original_r2_changed_files'] = cross.source_hosts_unchanged(original)
        result['cross_host_changed_files'] = cross.source_hosts_unchanged(previous)
        result['tool_changed_files'] = [item['path'] for item in tool['tool_files'] + tool['help_sources']
                                         if not Path(item['path']).is_file() or cross.sha(Path(item['path'])) != item['sha256']]
        if result['original_r2_changed_files'] or result['cross_host_changed_files'] or result['tool_changed_files']:
            result['run_complete'] = result['all_cases_passed'] = False
        result['finished'] = stamp()
        r2.write(path, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    work = Path('/workspace/deepseek41-work')
    parser.add_argument('--r2-manifest', type=Path, default=work / 'existing-regressions/final3651-native6b3-json-performance-r2/run.json')
    parser.add_argument('--cross-manifest', type=Path, default=work / 'existing-regressions/qwen35-managed-native-cross-phase-r1/run.json')
    parser.add_argument('--harness-dir', type=Path, default=Path('/workspace/TensorSharp/benchmarks/engine_comparison'))
    parser.add_argument('--out', type=Path, default=work / 'existing-regressions/qwen35-eventpipe-final-native-r1')
    parser.add_argument('--trace-tool', type=Path, default=work / 'profiling-tools/dotnet-trace')
    parser.add_argument('--tool-evidence', type=Path, default=work / 'qwen35-eventpipe-tool-evidence.json')
    parser.add_argument('--stop-file', type=Path, default=work / 'stop-qwen35-eventpipe')
    args = parser.parse_args()
    cross = load_cross()
    result = execute(cross, cross.load_runner(args.harness_dir), args)
    return 0 if result.get('all_cases_passed') else 1


if __name__ == '__main__':
    raise SystemExit(main())
