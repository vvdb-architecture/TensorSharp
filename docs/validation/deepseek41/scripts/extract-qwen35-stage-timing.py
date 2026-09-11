#!/usr/bin/env python3
"""Read-only correlation of retained Qwen3.5 alternating HTTP and server logs."""
import hashlib
import json
from pathlib import Path
import re
import statistics
import subprocess

ROOT = Path('/Users/zhongkaifu/work/TensorSharp')
RAW = Path('/tmp/deepseek41-reference/final-qwen35-json-alternating')
OUT = Path('/tmp/deepseek41-reference/qwen35-stage-timing')


def source(path):
    data = path.read_bytes()
    return {'path': str(path), 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


results = []
for pair in (1, 2):
    for version in ('baseline', 'final'):
        report_path = RAW / ('pair' + str(pair)) / ('qwen35-' + version + '.json')
        log_path = report_path.parent / ('qwen35-' + version + '-file-logs/tensorsharp-server-20260911.jsonl')
        stdout_path = report_path.parent / ('qwen35-' + version + '-server.log')
        report = json.loads(report_path.read_text())
        starts, completed = {}, {}
        for line_no, text in enumerate(log_path.read_text().splitlines(), 1):
            row = json.loads(text)
            if row.get('category') != 'TensorSharp.Server.ModelService':
                continue
            request_id = row.get('scope', {}).get('RequestId')
            props = row.get('props', {})
            event = row.get('event', {}).get('name')
            if event == 'ChatStarted':
                tag = re.search(r'\[validation ([^]]+)\]', props['LastUserContent']).group(1)
                assert request_id not in starts
                starts[request_id] = {'tag': tag, 'line': line_no, 'ts': row['ts']}
            if event == 'ChatCompleted':
                assert request_id not in completed
                completed[request_id] = {'props': props, 'line': line_no, 'ts': row['ts']}
        cases = []
        for case in report['cases']:
            matches = [(rid, start) for rid, start in starts.items() if start['tag'] == case['tag']]
            assert len(matches) == 1
            rid, start = matches[0]
            completion = completed[rid]
            props = completion['props']
            metrics = case['turns'][0]['metrics']
            assert props['PromptTokens'] == metrics['prompt_tokens'] == 92
            assert props['Tokens'] == metrics['completion_tokens'] == 16
            assert props['KvReusedTokens'] == 0
            assert props['AssistantContent'] == metrics['assistant_message']['content']
            cases.append({'tag': case['tag'], 'concurrency': case['concurrency'], 'request_id': rid,
                          'input_sha256': case['input_sha256'], 'status': case['status'],
                          'start_line': start['line'], 'complete_line': completion['line'],
                          'start_ts': start['ts'], 'complete_ts': completion['ts'],
                          'pipeline_ttft_ms': props['TimeToFirstTokenMs'], 'pipeline_elapsed_ms': props['ElapsedMs'],
                          'client_ttft_ms': metrics['ttft_ms'], 'client_elapsed_ms': metrics['total_wall_ms'],
                          'prompt_tokens': props['PromptTokens'], 'completion_tokens': props['Tokens'],
                          'kv_reused_tokens': props['KvReusedTokens'],
                          'assistant_content_sha256': hashlib.sha256(props['AssistantContent'].encode()).hexdigest()})
        assert len(cases) == 15 and len({c['tag'] for c in cases}) == 15
        medians = {}
        for c in (1, 4):
            rows = [r for r in cases if r['concurrency'] == c]
            medians[str(c)] = {k: statistics.median(r[k] for r in rows) for k in
                              ('pipeline_ttft_ms', 'pipeline_elapsed_ms', 'client_ttft_ms', 'client_elapsed_ms')}
        results.append({'pair': pair, 'version': version, 'report_source': source(report_path),
                        'file_log_source': source(log_path), 'stdout_source': source(stdout_path),
                        'binary_sha256': report['binary_sha256'], 'cases': cases, 'medians': medians,
                        'native_phase_line_count': stdout_path.read_text().count('[phase]')})

paths = {
    'TensorSharp.Chat/ChatGenerationPipeline.cs': ['var promptSw', 'var evalSw', 'var totalSw',
        'promptSw.Stop()', '_kvCacheRenderer.RenderToTokens', 'engine.SubmitRequest',
        'timeToFirstTokenMs =', 'await foreach (var nextToken'],
    'TensorSharp.Server/OpenAI/OpenAIChatAdapter.cs': ['samplingConfig = WithStructuredOutputConstraint', 'samplingConfig = WithDeepSeek41ToolGrammar'],
    'TensorSharp.GGML.Native/ggml_ops_qwen35_verify.cpp': ['PhaseTimer phase_timer', 'const bool fv_persist =', 'phase_timer.mark', 'const int n_logits ='],
    'TensorSharp.GGML.Native/ggml_ops_moe.cpp': ['host_moe_explicit_thread_count', 'g_moe_cpu_threads_override.load'],
    'TensorSharp.GGML.Native/ggml_ops_deepseek4.cpp': ['host_moe_explicit_thread_count'],
}
sources = []
for relative, needles in paths.items():
    path = ROOT / relative
    if not path.exists():
        candidates = list(ROOT.rglob(path.name))
        assert len(candidates) == 1
        path = candidates[0]
        relative = str(path.relative_to(ROOT))
    current = path.read_text()
    old = subprocess.check_output(['git', 'show', 'HEAD:' + relative], cwd=ROOT).decode()
    sources.append({'path': relative, 'current': source(path),
                    'head_sha256': hashlib.sha256(old.encode()).hexdigest(),
                    'byte_identical_to_head': current == old,
                    'current_lines': [{'line': i, 'text': text} for i, text in enumerate(current.splitlines(), 1)
                                      if any(n in text for n in needles)],
                    'head_lines': [{'line': i, 'text': text} for i, text in enumerate(old.splitlines(), 1)
                                   if any(n in text for n in needles)]})
result = {'scope': 'Read-only retained-log correlation; no inference or native/GC profiling was run.',
          'matched_cases': 60, 'all_zero_kv_reuse': True, 'all_prompt_tokens': 92, 'all_completion_tokens': 16,
          'finding': 'The repeated solo latency gap is also present in the server pipeline timer after SubmitRequest, while awaiting the first sampled token. Prompt rendering/tokenization and adapter grammar construction precede that timer. Native graph work, scheduling, active grammar masking/sampling, and token delivery remain inside the unresolved interval; these logs cannot isolate them.',
          'limits': 'No retained phase, allocation, or GC trace. The integer pipeline timer and client timer have different boundaries. Concurrent-case native work is shared and must not be assigned by naive log order. Four diagnostic fresh-host crossings are prepared separately.',
          'runs': results, 'source_evidence': sources,
          'head_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
          'extractor_source': source(Path(__file__))}
OUT.mkdir(exist_ok=True)
(OUT / 'stage-timing.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({'matched_cases': 60, 'solo_medians': [{k: r[k] for k in ('pair', 'version')} | r['medians']['1'] for r in results]}, indent=2))
