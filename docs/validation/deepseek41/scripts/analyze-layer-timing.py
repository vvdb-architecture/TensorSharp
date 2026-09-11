#!/usr/bin/env python3
"""Read-only comparison of retained timing evidence; does not run inference."""
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics

WORK = Path('/tmp/deepseek41-reference')
RAW = WORK / 'final-raw'
DEST = Path('/Users/zhongkaifu/work/TensorSharp/docs/validation/deepseek41/final-placements')
PROFILES = {
    'baseline': 'layer8-context65536-ubatch1024-cpumoe0-sparse1-compact1-slots4-b26c',
    'final': 'layer8-context65536-ubatch1024-cpumoe0-cputhreads32-sparse1-compact1-chunk1024-6b3-final',
}

def source(path):
    return {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'bytes': path.stat().st_size}

def summary(values):
    assert values and all(math.isfinite(x) for x in values)
    return {'min': min(values), 'median': statistics.median(values), 'max': max(values)}

def inspect(profile):
    report_path = RAW / (profile + '-long-parallel.json')
    phase_path = RAW / (profile + '-long-parallel-runs.json')
    csv_path = RAW / (profile + '-long-parallel-gpu.csv')
    report = json.loads(report_path.read_text())
    phase = json.loads(phase_path.read_text())
    accounting = phase['runs'][0]['native_accounting']
    cases = []
    for case in report['cases']:
        if case['scenario'] != 'long_32k':
            continue
        m = case['turns'][0]['metrics']
        assert m['decode_timing_source'] == 'stream_window' and m['completion_tokens'] == 34
        span = m['t_last_abs'] - m['t_first_abs']
        assert math.isclose(m['decode_tps'], (m['completion_tokens'] - 1) / span, rel_tol=1e-12)
        cases.append({'tag': case['tag'], 'status': case['status'], 'prompt_tokens': m['prompt_tokens'],
                      'completion_tokens': m['completion_tokens'], 'ttft_ms': m['ttft_ms'],
                      'request_wall_ms': m['total_wall_ms'], 'decode_tps': m['decode_tps'],
                      'decode_timing_source': m['decode_timing_source'], 'decode_window_seconds': span,
                      **{k: m[k] for k in ('t_start_abs', 't_first_abs', 't_last_abs', 't_end_abs')}})
    assert len(cases) == 4 and all(c['status'] == 'ok' for c in cases)
    with csv_path.open() as stream:
        rows = [{k.strip(): v.strip() for k, v in row.items()} for row in csv.DictReader(stream)]
    field_names = {'sm_mhz': 'clocks.current.sm [MHz]', 'memory_mhz': 'clocks.current.memory [MHz]',
                   'temperature_c': 'temperature.gpu', 'power_w': 'power.draw [W]'}
    per_device = {}
    for device in range(8):
        selected = [r for r in rows if int(r['index']) == device]
        per_device[str(device)] = {'samples': len(selected), **{
            key: summary([float(r[field].split()[0]) for r in selected]) for key, field in field_names.items()}}
    return {'profile': profile, 'report_source': source(report_path), 'phase_source': source(phase_path),
            'csv_source': source(csv_path), 'cases_32k': cases,
            'first_token_spread_seconds': max(c['t_first_abs'] for c in cases) - min(c['t_first_abs'] for c in cases),
            'last_token_spread_seconds': max(c['t_last_abs'] for c in cases) - min(c['t_last_abs'] for c in cases),
            'native_both_waves_and_warmup': accounting,
            'mean_native_decode_call_ms': accounting['decode_forward_seconds'] * 1000 / accounting['decode_calls'],
            'telemetry': {'sample_count': len(rows), 'first_sample': rows[0]['timestamp'],
                          'last_sample': rows[-1]['timestamp'], 'per_device': per_device},
            'full_server_log_available_locally': (RAW / (profile + '-server.log')).exists()}

result = {'analysis_source': source(Path(__file__)), 'profiles': {key: inspect(p) for key, p in PROFILES.items()},
          'interpretation': [
              'The 11.56% lower final median short-response SSE decode rate is retained; this metric is not sustained 512-token throughput.',
              'Both use 33 streamed intervals for 34 generated tokens. The old early request overlaps about 59.56 seconds of remaining prefill, while final first tokens arrive within about 0.263 seconds. This shows different scheduling overlap; it does not establish a kernel regression or remove the measured per-request decline.',
              'Across both long waves and warmup, native decode averages are effectively equal at the recorded millisecond log precision. Nine extra old calls match the nine extra old 8k fenced-response tokens. The old full server log is absent locally, so old 32k-only native time cannot be independently reconstructed.',
              'GPU clock medians match, but both contain lower SM clock samples. Periodic samples cannot establish all in-flight clocks or rule out short contention. No clock-causality claim is made.',
          ],
          'future_json_warmup_observation': {
              'runner_source': source(WORK / 'run-final-json-performance.py'),
              'scope': 'Runner design only; no execution results from this separate run are audited here. Each new immutable host performs an unmeasured 512-token decode warmup followed by a JSON c1 request and JSON c4 wave, retained separately from the 90 timed JSON requests.',
              'comparison_requirements': 'Compare the first JSON c1 warmup only when exact initial/full requests, outputs, finish reasons and prompt/completion counts match. Report raw TTFT and whole-request wall, not just decode throughput.',
              'limitations': 'One first-use observation per model/version; telemetry starts after warmups. Therefore it can flag a first-use difference, but cannot qualify its timing, isolate grammar compilation, prove a systematic cold-use regression, or substitute for the repeated measured groups.'}}
output = DEST / 'layer8-timing-diagnostics.json'
output.write_text(json.dumps(result, indent=2) + '\n')
print(output)
