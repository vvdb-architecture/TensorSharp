#!/usr/bin/env python3
"""Read-only audit of four diagnostic managed/native crossings and loaded paths."""
import argparse,hashlib,importlib.util,json,re,statistics,sys
from pathlib import Path
W=Path('/tmp/deepseek41-reference');ROOT=Path('/Users/zhongkaifu/work/TensorSharp')
RUN_SHA='32219050b755a6b28a08de3c0255d929d53813e5378b022b00d435c4484104b2'
OBSERVER_SHA='ee30d16c13064f244723682c7ae5730fe022034fa2f0543efb26db4b1a69c9ac'
sys.path.insert(0,str(ROOT/'benchmarks/engine_comparison'))
def load(name,path):
 s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
A=load('r2_independent_audit',W/'audit-json-performance-r2.py');C=load('cross_runner',W/'run-qwen35-cross-phase.py')
def source(p):return {'path':str(p),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size}
def read(p):return json.loads(p.read_text())

def main():
 ap=argparse.ArgumentParser(description=__doc__)
 ap.add_argument('--raw',type=Path,default=W/'qwen35-managed-native-cross-phase-r1')
 ap.add_argument('--observer',type=Path,default=W/'qwen35-cross-loaded-libraries.json')
 ap.add_argument('--out',type=Path,default=ROOT/'docs/validation/deepseek41/json-performance/qwen35-cross-phase')
 args=ap.parse_args();raw=args.raw;r=read(raw/'run.json');prior=read(W/'final-json-performance-r2/run.json')
 assert r['run_complete']and r['all_cases_passed']and r['expected_cases']==60
 assert r['diagnostic_only']and r['performance_qualified']is False
 assert r['planned_jobs']==[{'managed':m,'native':n}for m,n in C.JOBS]and len(r['runs'])==4
 assert r['runner_source']['sha256']==source(W/'run-qwen35-cross-phase.py')['sha256']==RUN_SHA
 assert r['r2_manifest']['sha256']==source(W/'final-json-performance-r2/run.json')['sha256']==C.R2_MANIFEST_SHA
 assert r['r2_runner']['sha256']==source(W/'run-final-json-performance.py')['sha256']==C.R2_RUNNER_SHA
 assert r['shared_helper']['sha256']==source(W/'run-final-existing-models.py')['sha256']==C.HELPER_SHA
 for name,h in r['harness_sha256'].items():assert source(ROOT/'benchmarks/engine_comparison'/name)['sha256']==h
 assert r['source_r2_changed_files']=={}and all(v==[]for v in r['diagnostic_host_changed_files'].values())
 assert r['weights']['sha256']==C.MODEL_SHA
 reference={A.key(c):c for c in read(W/'final-json-performance-r2/qwen35-final.json')['cases']}
 children={};rows=[];all_keys=[]
 for entry,(managed,native) in zip(r['runs'],C.JOBS):
  label=managed+'-managed_'+native+'-native';assert entry['cross']=={'managed':managed,'native':native}
  path=raw/Path(entry['source']['path']).name;d=read(path);assert source(path)['sha256']==entry['source']['sha256']
  assert entry['diagnostic_complete']and entry['status']=='ok'and entry['failed_cases']==[]
  assert A.m.complete(d)and d['diagnostic_complete']and d['status']=='ok'and d['diagnostic_only']and d['performance_qualified']is False
  assert d['telemetry_qualification']['qualified']is False and d['native_phase_capture_ok']and d['warmup_coverage_ok']
  assert d['environment']=={**A.m.common.ENV,'TS_GGML_PHASE_TIMING':'1'}
  assert d['sampling']==A.m.validation.SAMPLING and d['thinking']is False and d['stream']is True
  assert d['deployment_changed_files']==[]and not d.get('error')and len(d['cases'])==15 and len(d['warmups'])==5
  deployment=r['deployments'][label]
  expected=dict(prior['deployments'][managed]['files_sha256']);expected['libGgmlOps.so']=prior['deployments'][native]['files_sha256']['libGgmlOps.so']
  assert deployment['files_sha256']==expected
  for name,h in d['binary_sha256'].items():assert h==expected[name]
  assert d['binary_sha256']==deployment['expected_core_binaries']
  assert d['cross']=={'managed':managed,'native':native}and d['weights_id']==C.MODEL_SHA
  assert d['decode512_warmup']['status']=='ok'and d['decode512_warmup']['turns'][0]['metrics']['completion_tokens']==512
  for c in d['cases']+d['warmups']:
   A.check_case(c);assert c['status']=='ok'and c['turns'][0]['metrics']['completion_tokens']==16
  for c in d['cases']:
   k=A.key(c);ref=reference[k]
   assert A.m.request_identity(c)==A.m.request_identity(ref)and A.m.output_identity(c)==A.m.output_identity(ref)
   all_keys.append((label,*k))
  for wave in d['waves']:
   cases=[c for c in d['cases']if c['concurrency']==wave['concurrency']and c['repeat']==wave['repeat']]
   assert len(cases)==wave['concurrency']and wave['all_passed']and wave['generated_tokens']==16*len(cases)
  log=raw/Path(d['server_log']['path']).name;assert source(log)['sha256']==d['server_log']['sha256']
  phases_path=raw/Path(d['native_phase_evidence']['path']).name;phases=read(phases_path)
  assert source(phases_path)['sha256']==d['native_phase_evidence']['sha256']
  recomputed=C.phase_evidence(A.m,log)
  assert phases['source']['sha256']==source(log)['sha256']and phases['phase_lines']==recomputed['phase_lines']
  assert phases['qwen35_verify_lines']==recomputed['qwen35_verify_lines']>0
  telemetry=raw/Path(d['telemetry_source']['path']).name;assert source(telemetry)['sha256']==d['telemetry_source']['sha256']
  assert d['runtime_file_logs']
  for relative,info in d['runtime_file_logs'].items():assert source(raw/relative)['sha256']==info['sha256']
  groups={}
  for degree in (1,4):
   cases=[c for c in d['cases']if c['concurrency']==degree];metrics=[c['turns'][0]['metrics']for c in cases]
   groups[str(degree)]={'all_cases_passed':True,'cases':len(cases),'diagnostic_only':True,
    'ttft_ms':[v['ttft_ms']for v in metrics],'request_wall_ms':[v['total_wall_ms']for v in metrics],
    'json_decode_tps':[v['decode_tps']for v in metrics],
    'median_ttft_ms':statistics.median(v['ttft_ms']for v in metrics),
    'median_request_wall_ms':statistics.median(v['total_wall_ms']for v in metrics),
    'median_json_decode_tps':statistics.median(v['decode_tps']for v in metrics),
    'waves':[w for w in d['waves']if w['concurrency']==degree]}
  children[label]=d;rows.append({'label':label,'cross':entry['cross'],'source':source(path),'binary_sha256':d['binary_sha256'],
                              'phase_source':source(phases_path),'phase_verify_lines':phases['qwen35_verify_lines'],'groups':groups})
 assert len(all_keys)==len(set(all_keys))==60
 obs=read(args.observer);assert obs['observer_sha256']==source(W/'observe-qwen35-cross-libraries.py')['sha256']==OBSERVER_SHA
 assert obs['observed_hosts']==len(obs['observations'])==4 and obs['runner_finished_utc']==r['finished_utc']
 assert len({v['pid']for v in obs['observations']})==4
 observed=[]
 for item in obs['observations']:
  label=item['version'];assert label in children and item['command']==children[label]['launch']
  expected_path=str(Path(r['deployments'][label]['target'])/'libGgmlOps.so')
  paths={line.split(maxsplit=5)[5]for line in item['mappings']}
  assert paths=={expected_path}
  observed.append({'label':label,'pid':item['pid'],'mapped_path':expected_path,'native_sha256_from_verified_deployment':r['deployments'][label]['files_sha256']['libGgmlOps.so']})
 assert {v['label']for v in observed}==set(children)
 result={'scope':'Independent local audit of all four managed/native diagnostic crossings. All 60 requests/outputs/token counts match completed R2. Instrumented timings are diagnostic only, never qualified regression/fix evidence.',
         'integrity_valid':True,'run_complete':True,'all_60_cases_passed':True,'performance_qualified':False,
         'analysis_source':source(Path(__file__)),'reused_case_auditor_source':source(W/'audit-json-performance-r2.py'),
         'run_source':source(raw/'run.json'),'rows':rows,'loaded_library_observer_source':source(args.observer),
         'loaded_libraries':observed,'loaded_library_scope':'All four owned command lines and observed mapping paths match intended frozen hosts. Observer sampled mappings once per host and did not hash live libraries; content hashes come from the producer-verified unchanged deployment manifests.',
         'phase_scope':'Every retained phase row was reparsed against its exact stdout line and hash. Phase lines lack request IDs; this audit does not assign concurrent native work to individual requests. Separate c1 correlation is required.',
         'sources':[source(p)for p in sorted(raw.rglob('*'))if p.is_file()]}
 args.out.mkdir(parents=True,exist_ok=True)
 for p in raw.rglob('*'):
  if not p.is_file()or (p.suffix!='.json'and not p.name.endswith('-telemetry.jsonl')):continue
  q=args.out/p.relative_to(raw);q.parent.mkdir(parents=True,exist_ok=True)
  if q.exists():assert q.read_bytes()==p.read_bytes()
  else:q.write_bytes(p.read_bytes())
 q=args.out/'loaded-libraries.json'
 if q.exists():assert q.read_bytes()==args.observer.read_bytes()
 else:q.write_bytes(args.observer.read_bytes())
 (args.out/'audit.json').write_text(json.dumps(result,indent=2)+'\n')
 print(json.dumps({'cases':60,'mapped_libraries':4,'performance_qualified':False,'medians':{row['label']:{c:g['median_ttft_ms']for c,g in row['groups'].items()}for row in rows}},indent=2))
if __name__=='__main__':main()
