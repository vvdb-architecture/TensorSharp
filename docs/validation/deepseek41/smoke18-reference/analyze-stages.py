#!/usr/bin/env python3
"""Read-only retained F32 stage/logit comparison; no weights or native execution."""
import argparse
import hashlib
import json
from pathlib import Path
import numpy as np


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compare(native, reference):
    a, b = native.astype(np.float64).reshape(-1), reference.astype(np.float64).reshape(-1)
    if a.shape != b.shape or not np.isfinite(a).all() or not np.isfinite(b).all():
        raise ValueError('Mismatched or nonfinite stage')
    # F32 source values are safely squared in F64. Explicit reductions also
    # avoid platform BLAS floating-point-status warnings in a read-only audit.
    na, nb = np.sqrt(np.sum(a * a)), np.sqrt(np.sum(b * b))
    if na <= 0 or nb <= 0:
        raise ValueError('Zero-norm stage')
    d = a - b
    return dict(max_absolute_error=float(np.max(np.abs(d))), relative_l2=float(np.sqrt(np.sum(d * d)) / nb),
                cosine=float(np.clip(np.sum((a / na) * (b / nb)), -1, 1)),
                strict_allclose_atol_rtol_2e_5=bool(np.allclose(a, b, atol=2e-5, rtol=2e-5)),
                equal_fraction=float(np.mean(a == b)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', type=Path, required=True)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    n, r = args.native, args.reference
    manifest = json.loads((n / 'manifest.json').read_text())
    for name, expected in manifest['artifacts'].items():
        path = n / name
        if not path.is_file() or path.stat().st_size != expected['bytes'] or sha(path) != expected['sha256']:
            raise ValueError('Native artifact hash mismatch: ' + name)
    rows = []

    def stage(layer, name, native_name, reference_name, alias_note=None):
        npth, rpth = n / 'trace-after' / ('p000000_' + native_name + '.f32'), r / ('p000000_' + reference_name + '.npy')
        original = np.load(rpth, mmap_mode='r')
        native = np.fromfile(npth, dtype='<f4')
        if native.size != original.size:
            raise ValueError('Stage shape mismatch: ' + native_name)
        rows.append(dict(layer=layer, stage=name, shape=list(original.shape),
                         native_file=npth.name, native_sha256=sha(npth), reference_file=rpth.name,
                         reference_sha256=sha(rpth), native_name_alias=alias_note,
                         metrics=compare(native, original)))

    stage(-1, 'embedding', 'v41.00.engram_output', 'embedding')
    for layer in range(40):
        for ns, rs in [('attn_input', 'attn_input'), ('query', 'q'), ('raw_kv', 'raw_k'),
                       ('attn_output', 'attn_out'), ('ffn_output', 'ffn_out')]:
            stage(layer, ns, f'v41.{layer:02d}.{ns}', f'blk{layer:02d}_{rs}')
        native_hidden = f'v41.{layer:02d}.output'
        alias = None
        if not (n / 'trace-after' / ('p000000_' + native_hidden + '.f32')).exists():
            if layer == 39 or (r / f'p000000_blk{layer+1:02d}_engram_out.npy').exists():
                raise ValueError('Missing hidden output cannot be explained by a no-Engram next-layer alias')
            native_hidden = f'v41.{layer+1:02d}.engram_output'
            alias = 'Next layer has no Engram; trace_v41 renames the same incoming hidden tensor. No mathematical transform occurs between these trace sites.'
        stage(layer, 'hidden', native_hidden, f'blk{layer:02d}_hidden', alias)
        for ns, rs in [('latent', 'compress_latent'), ('engram_output', 'engram_out')]:
            if (r / f'p000000_blk{layer:02d}_{rs}.npy').exists():
                stage(layer, ns, f'v41.{layer:02d}.{ns}', f'blk{layer:02d}_{rs}')
    primary = np.load(n / 'smoke.first-logits.npy')
    traced = np.load(n / 'smoke.traced-first-logits.npy')
    old = np.fromfile(n / 'retained-old-native-first-logits.f32', dtype='<f4')
    reference_logits = np.load(r / 'logits.npy', mmap_mode='r')[-1]
    report = dict(scope=__doc__, native_source_manifest_sha256=sha(n / 'manifest.json'),
                  comparison_source_sha256=sha(Path(__file__)), stages=rows,
                  stages_passing_strict=sum(x['metrics']['strict_allclose_atol_rtol_2e_5'] for x in rows),
                  stages_total=len(rows), final_logits=compare(primary, reference_logits),
                  primary_vs_traced_bitwise_equal=bool(np.array_equal(primary.view(np.uint32), traced.view(np.uint32))),
                  primary_vs_old_bitwise_equal=bool(np.array_equal(primary.view(np.uint32), old.view(np.uint32))),
                  logit_payload_sha256=hashlib.sha256(primary.tobytes()).hexdigest(),
                  old_logit_file_sha256=sha(n / 'retained-old-native-first-logits.f32'),
                  old_native_binary_provenance='Not bound by this retained raw logit artifact; no pre/post-precision attribution.',
                  caveats=['F32-input GGUF oracle differs from CUDA quantized-activation matmul; this report does not attribute the entire discrepancy to one mechanism.',
                           'The original CPU oracle launcher/source hash is not retained. Its cache policy, exact tokens and unsplit prefill were established separately from the retained log and shapes.',
                           'Trace equals primary logits for this run only; this does not establish general trace/production fusion equivalence.',
                           'Strict2e-5 failures remain failures. No tolerance was widened.'])
    args.report.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
    print(json.dumps({k:v for k,v in report.items() if k != 'stages'}, indent=2))
    print('per-layer relative L2: attention_input, query, raw_KV, attention_output, FFN_output, hidden')
    for layer in range(40):
        print(layer, [round(row['metrics']['relative_l2'], 8) for row in rows if row['layer'] == layer and row['stage'] in ['attn_input','query','raw_kv','attn_output','ffn_output','hidden']])


if __name__ == '__main__':
    main()
