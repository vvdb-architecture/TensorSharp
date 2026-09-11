from pathlib import Path
import json,hashlib
SRC=Path('/tmp/deepseek41-reference/final-json-performance')
OUT=Path('/Users/zhongkaifu/work/TensorSharp/docs/validation/deepseek41/json-performance/incomplete-r1')
OUT.mkdir(parents=True,exist_ok=True)
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def source(p): return {'path':str(p),'sha256':sha(p),'bytes':p.stat().st_size}
def key(c): return c['scenario'],c['tag'],c['concurrency'],c.get('repeat',0)
def outputs(c): return [{'assistant_message':t['metrics']['assistant_message'],'finish_reason':t['metrics']['finish_reason'],'prompt_tokens':t['metrics']['prompt_tokens'],'completion_tokens':t['metrics']['completion_tokens']}for t in c['turns']]
r=json.loads((SRC/'run.json').read_text())
assert not r['run_complete'] and r['expected_timed_cases']==90 and sum(v['cases']for v in r['runs'])==30
reports={}
for run in r['runs']:
 name=run['model']+'-'+run['version'];p=SRC/(name+'.json');d=json.loads(p.read_text())
 assert sha(p)==run['report']['sha256'];reports[name]=d
 assert d['deployment_changed_files']==['logs/tensorsharp-server-20260911.jsonl']
 if name.startswith('qwen3-'):assert d['run_complete'] and len(d['cases'])==15
 else:assert not d['run_complete'] and not d['cases'] and d['error']=='RuntimeError: Frozen deployment changed before launch'
a={key(c):c for c in reports['qwen3-baseline']['cases']};b={key(c):c for c in reports['qwen3-final']['cases']}
assert a.keys()==b.keys() and len(a)==15
pairs=[]
for k in sorted(a):
 x,y=a[k],b[k];assert x['input_sha256']==y['input_sha256'] and [t['request']for t in x['turns']]==[t['request']for t in y['turns']]
 pairs.append({'key':list(k),'initial_request_sha256':x['input_sha256'],'full_requests_equal':True,'baseline_status':x['status'],'final_status':y['status'],'outputs_tokens_equal':outputs(x)==outputs(y),'baseline_output':outputs(x),'final_output':outputs(y)})
new=[p for p in pairs if p['baseline_status']=='ok' and p['final_status']!='ok']
assert [p['key'][1]for p in new]==['json-c4-r0-i0','json-c4-r0-i1']
for p in new:
 assert json.loads(p['baseline_output'][0]['assistant_message']['content'])=={'name':'Mars','moons':2,'habitable':False}
 assert json.loads(p['final_output'][0]['assistant_message']['content'])=={'Mars':{'moons':2,'habitable':False}}
 assert p['baseline_output'][0]['completion_tokens']==21 and p['final_output'][0]['completion_tokens']==25
summary={'scope':'Incomplete first90-case JSON A/B attempt. No timing comparison is credited; preserve harness failure and real semantic mismatches. Not replaced by the planned all90 r2 rerun.',
 'analysis_source':source(Path(__file__)),'run_source':source(SRC/'run.json'),'expected_timed_cases':90,'completed_timed_cases':30,'run_complete':False,
 'failure_cause':'The immutable deployment snapshot included inherited logs/tensorsharp-server-20260911.jsonl; normal server file logging appended to this file. All frozen-file verification was enforced, so Qwen3 jobs recorded deployment change and later four jobs refused launch.',
 'completed_jobs':2,'unstarted_jobs':4,'qwen3_baseline_passed':sum(c['status']=='ok'for c in a.values()),'qwen3_final_passed':sum(c['status']=='ok'for c in b.values()),
 'qwen3_case_pairs':pairs,'introduced_failed_cases':new,
 'semantic_interpretation':'The two new final failures are syntactically valid JSON and well-formed Unicode but have a nested Mars root key instead of the three requested top-level fields. The same nested error also occurs in nine baseline cases. This attempt does not identify a causal Unicode, native, concurrency or sampling change. Requests and validators will remain unchanged for r2.',
 'timing_qualification':'Withheld: deployment-integrity failures plus failed/unequal planned responses prevent whole-group ratios. Raw timings and sampled telemetry remain in the original child reports.',
 'rerun_policy':'New final3651-native6b3-json-performance-r2 label and fresh immutable deployments; all90 planned cases, same prompts/settings/order/warmups; per-job TENSORSHARP_LOG_DIR redirects mutable logs outside deployment. No binary/configuration hash exclusions.',
 'sources':[source(p)for p in sorted(SRC.iterdir())if p.is_file()]}
for p in SRC.glob('*.json'):
 q=OUT/p.name
 if q.exists():assert q.read_bytes()==p.read_bytes(),q
 else:q.write_bytes(p.read_bytes())
(OUT/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print('Retained30/90; Qwen3 baseline',summary['qwen3_baseline_passed'],'/15, final',summary['qwen3_final_passed'],'/15; two newly failed cases')
