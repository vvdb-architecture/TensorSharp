#!/usr/bin/env python3
"""Summarize exactly 24 owned-scratch rows; never qualify a full-model speedup."""
import argparse
import hashlib
import json
import math
import re
import statistics
from pathlib import Path


def summarize(rows):
    expected = {(nt, th, s, advice) for nt in (1, 3) for th in (1, 16)
                for s in range(3) for advice in ('default', 'random')}
    if len(rows) != len(expected):
        raise ValueError('Expected exactly 24 scratch trials')
    indexed = {}
    fingerprints = {}
    for row in rows:
        key = (row['tokens'], row['threads'], row['sample'], row['advice'])
        if key not in expected or key in indexed:
            raise ValueError('Unexpected or duplicate trial')
        indexed[key] = row
        nt, th, sample, advice = key
        if row['mode'] != 'scratch_advice' or row['exact_output'] is not True:
            raise ValueError('Wrong mode or failed output parity')
        if (row['head_dim'], row['row_bytes'], row['selected_rows'], row['bytes']) != (256, 512, nt * 24, 64 * 1024 * 1024):
            raise ValueError('Unexpected geometry')
        if row['dequantization'] != 'synthetic_int16_div127':
            raise ValueError('Unexpected arithmetic')
        page = row['page_bytes']
        if type(page) is not int or page < 1 or row['bytes'] % page or row['total_pages'] != row['bytes'] // page:
            raise ValueError('Invalid page geometry')
        selected = row['selected_pages']
        if type(selected) is not int or not 0 < selected <= min(row['selected_rows'] * ((512 + page - 1) // page + 1), row['total_pages']):
            raise ValueError('Invalid selected-page count')
        for prefix in ('initial', 'final'):
            total = row[prefix + '_resident_pages']
            selected_resident = row[prefix + '_selected_resident_pages']
            if type(total) is not int or type(selected_resident) is not int or not 0 <= selected_resident <= min(selected, total) <= row['total_pages'] or total > row['total_pages']:
                raise ValueError('Invalid residency counters')
        if row['cold_pages_confirmed'] is not (row['initial_selected_resident_pages'] == 0):
            raise ValueError('False cold-page qualification')
        for name in ('minor_faults_delta', 'major_faults_delta'):
            if type(row[name]) is not int or row[name] < 0:
                raise ValueError('Invalid process fault delta')
        seconds = row['lookup_seconds']
        if type(seconds) not in (int, float) or not math.isfinite(seconds) or seconds <= 0:
            raise ValueError('Nonpositive or nonfinite time')
        if row['thread_order'] not in (0, 1) or row['advice_order'] not in (0, 1):
            raise ValueError('Invalid order')
        if (sample + row['thread_order']) % 2 != (th == 16):
            raise ValueError('Wrong alternating thread order')
        if (sample + row['advice_order']) % 2 != (advice == 'random'):
            raise ValueError('Wrong alternating advice order')
        fp = row['hashes_fnv1a64']
        if not isinstance(fp, str) or not re.fullmatch('[0-9a-f]{16}', fp):
            raise ValueError('Malformed selected-row fingerprint')
        identity = (fp, selected, page)
        if fingerprints.setdefault(nt, identity) != identity:
            raise ValueError('Selected rows or page geometry changed between paired trials')
    if set(indexed) != expected:
        raise ValueError('Incomplete trial coverage')
    for nt in (1, 3):
        for th in (1, 16):
            if {indexed[(nt, th, sample, 'default')]['advice_order'] for sample in range(3)} != {0, 1}:
                raise ValueError('Each thread count must cover both advice orders')
    groups = []
    for nt in (1, 3):
        for th in (1, 16):
            pairs = []
            for sample in range(3):
                before = indexed[(nt, th, sample, 'default')]
                after = indexed[(nt, th, sample, 'random')]
                cold = before['cold_pages_confirmed'] and after['cold_pages_confirmed']
                pairs.append({'sample': sample, 'local_selected_pages_cold': cold,
                    'default_ms': before['lookup_seconds'] * 1000,
                    'random_ms': after['lookup_seconds'] * 1000,
                    'default_over_random': before['lookup_seconds'] / after['lookup_seconds'] if cold else None})
            ratios = [pair['default_over_random'] for pair in pairs if pair['local_selected_pages_cold']]
            groups.append({'tokens': nt, 'threads': th, 'pairs': pairs,
                'cold_pairs': len(ratios), 'cold_pair_ratio_median': statistics.median(ratios) if ratios else None})
    return {'complete': True, 'trials': 24, 'exact_outputs': 24, 'groups': groups,
        'scope': 'Owned scratch-file timings with local selected-page residency checks. Process fault counters and post-read residency are observations, not physical/network bytes. Filesystem server caches, cross-process activity and full-model performance are not qualified by this summary.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('jsonl', type=Path)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    raw = args.jsonl.read_bytes()
    rows = [json.loads(line) for line in raw.decode().splitlines() if line.strip()]
    report = summarize(rows)
    report['input'] = {'path': str(args.jsonl.resolve()), 'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()}
    report['analyzer_sha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.report.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
    print(json.dumps(report, allow_nan=False))


if __name__ == '__main__':
    main()
