#!/usr/bin/env python3
"""Local full60-case audit; retain alternating pairs and all raw distributions."""
import hashlib,importlib.util,json,math,statistics,sys
from pathlib import Path
W=Path('/tmp/deepseek41-reference');ROOT=Path('/Users/zhongkaifu/work/TensorSharp')
RAW=W/'final-qwen35-json-alternating';OUT=ROOT/'docs/validation/deepseek41/json-performance/qwen35-alternating'
sys.path.insert(0,str(ROOT/'benchmarks/engine_comparison'))
def load(name,p):
 s=importlib.util.spec_from_file_location(name,p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
audit=load('r2_auditor',W/'audit-json-performance-r2.py');control=load('qwen35_control',W/'run-qwen35-json-control.py')
def source(p):return {'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size}
def read(p):return json.loads(p.read_text())
def close(a,b):return math.isclose(a,b,rel_tol=1e-12,abs_tol=1e-10)

def main():
 r=read(RAW/'run.json');prior=read(W/'final-json-performance-r2/run.json')
 assert r['run_complete']and r['all_cases_passed']and r['all_groups_comparable']and r['expected_timed_cases']==60
 assert r['planned_pairs']==control.PLAN and len(r['runs'])==4
 assert r['runner_source']['sha256']==source(W/'run-qwen35-json-control.py')['sha256']=='d3cc10180fd03b14b7dc7a4d940203804e1b0d64230e15a9b2a5aceaf9dbf4a5'
 assert r['reused_runner_source']['sha256']==source(W/'run-final-json-performance.py')['sha256']==control.R2_RUNNER_SHA
 assert r['preceding_r2_source']['sha256']==source(W/'final-json-performance-r2/run.json')['sha256']==control.R2_RUN_SHA
 assert r['reused_frozen_deployments']==prior['deployments']
 assert r['harness_sha256']==prior['harness_sha256']
 for name,sha in r['harness_sha256'].items():assert source(ROOT/'benchmarks/engine_comparison'/name)['sha256']==sha
 assert [(v['pair'],v['version'])for v in r['runs']]==[(p['pair'],v)for p in control.PLAN for v in p['versions']]
 assert sum(v['cases']for v in r['runs'])==60
 reports={};tele={};warm={};case_identities=[]
 for entry in r['runs']:
  pair,version=entry['pair'],entry['version'];p=RAW/pair/('qwen35-'+version+'.json');d=read(p)
  assert source(p)['sha256']==entry['report']['sha256'] and entry['run_complete']and entry['status']=='ok'and entry['cases']==15
  assert control.r2.complete(d)and d['status']=='ok'and len(d['warmups'])==5
  assert d['deployment_changed_files']==[]and not d.get('error')
  assert d['binary_sha256']==prior['deployments'][version]['expected_core_binaries']
  assert d['environment']==control.r2.common.ENV and d['profile']==control.r2.common.PROFILE
  assert d['sampling']==control.r2.validation.SAMPLING and d['thinking']is False and d['stream']is True
  assert d['weights_id']==r['weights']['sha256']
  assert source(RAW/pair/('qwen35-'+version+'-server.log'))['sha256']==d['server_log']['sha256']
  assert d['decode512_warmup']['status']=='ok'and d['decode512_warmup']['turns'][0]['metrics']['completion_tokens']==512
  old=read(W/'final-json-performance-r2'/('qwen35-'+version+'.json'));oldcases={control.r2.key(c):c for c in old['cases']}
  for c in d['cases']+d['warmups']:
   audit.check_case(c);assert c['status']=='ok'and c['turns'][0]['metrics']['completion_tokens']==16
  for c in d['cases']:
   k=control.r2.key(c);orig=oldcases[k]
   assert control.r2.request_identity(c)==control.r2.request_identity(orig)
   assert control.r2.output_identity(c)==control.r2.output_identity(orig)
   case_identities.append([pair,version,*k])
  for w in d['waves']:
   members=[c for c in d['cases']if c['concurrency']==w['concurrency']and c['repeat']==w['repeat']]
   assert len(members)==w['concurrency']and w['all_passed']
   assert w['generated_tokens']==16*len(members)
  audit.RAW=RAW/pair;tele[pair+'-'+version]=audit.telemetry('qwen35-'+version,d)
  reports[(pair,version)]=d
 assert len(case_identities)==len(set(map(tuple,case_identities)))==60
 comparisons={};samples={}
 for pair in ['pair1','pair2']:
  a,b=reports[(pair,'baseline')],reports[(pair,'final')]
  computed=control.r2.compare(a,b);assert computed==r['comparisons'][pair]and computed['all_groups_comparable']
  distributions=control.distributions(a,b,computed);assert distributions==r['paired_case_distributions'][pair]
  comparisons[pair]=computed;samples[pair]=distributions;warm[pair]=audit.first_warm(a,b)
  for degree,g in computed['concurrency_groups'].items():
   assert g['comparable']and all(not v for v in g['metric_issues'].values())
   for version,d in [('baseline',a),('final',b)]:
    c=[c['turns'][0]['metrics']for c in d['cases']if c['concurrency']==int(degree)];s=g['summaries'][version]
    for field,dest in [('ttft_ms','ttft_ms_median'),('decode_tps','json_decode_tps_median'),('total_wall_ms','request_wall_ms_median')]:
     assert close(s[dest],statistics.median(v[field]for v in c))
    assert all(v['decode_timing_source']=='stream_window'and v['t_last_abs']-v['t_first_abs']>=.05 for v in c)
    waves=[w for w in d['waves']if w['concurrency']==int(degree)]
    assert close(s['whole_wave_wall_ms_median'],statistics.median(w['wall_ms']for w in waves))
   old,new=g['summaries']['baseline'],g['summaries']['final'];ratio=g['speedup_final_over_baseline']
   for field,metric in [('ttft_ms_median','ttft'),('request_wall_ms_median','request_wall'),('whole_wave_wall_ms_median','whole_wave_wall')]:assert close(ratio[metric],old[field]/new[field])
   assert close(ratio['json_decode'],new['json_decode_tps_median']/old['json_decode_tps_median'])
 result={'scope':'Independent complete60-case audit of both alternating pairs. Exact requests/outputs/token counts match completed R2; all paired distributions and slower measurements retained. This is a whole-build comparison, not isolated grammar causality.',
         'run_complete':True,'integrity_valid':True,'all_60_cases_passed':True,'all_four_groups_comparable':True,
         'analysis_source':source(Path(__file__)),'reused_auditor_source':source(W/'audit-json-performance-r2.py'),'raw_run_source':source(RAW/'run.json'),
         'case_identities':case_identities,'comparisons':comparisons,'paired_case_distributions':samples,
         'telemetry':tele,'first_json_warmup_diagnostics':warm,
         'latency_finding':'Final c1 median TTFT is slower in both pair orders: +15.431351ms and +15.162893ms. Median c1 request wall is slower by21.619781ms and21.648075ms. These are retained as unresolved latency regressions requiring investigation, not dismissed as variance. C4 direction differs between complete pairs.',
         'deployment_scope':'Producer verified every existing frozen file before/after each job; this local audit checks reported hashes and unchanged settings without rehashing VM deployment or weights.',
         'sources':[source(p)for p in sorted(RAW.rglob('*'))if p.is_file()]}
 OUT.mkdir(parents=True,exist_ok=True)
 for p in RAW.rglob('*'):
  if not p.is_file()or (p.suffix!='.json'and not p.name.endswith('-telemetry.jsonl')):continue
  q=OUT/p.relative_to(RAW);q.parent.mkdir(parents=True,exist_ok=True)
  if q.exists():assert q.read_bytes()==p.read_bytes()
  else:q.write_bytes(p.read_bytes())
 (OUT/'audit.json').write_text(json.dumps(result,indent=2)+'\n')
 print(json.dumps({'audit':str(OUT/'audit.json'),'complete':60,'comparisons':{p:{c:g['speedup_final_over_baseline']for c,g in d['concurrency_groups'].items()}for p,d in comparisons.items()}},indent=2))
if __name__=='__main__':main()
