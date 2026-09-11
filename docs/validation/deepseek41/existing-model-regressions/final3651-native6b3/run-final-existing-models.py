#!/usr/bin/env python3
"""Final TensorSharp small-model correctness regression: exact75 + separate15 Unicode.

Never builds/downloads, changes the source host, or qualifies performance.
Uses a new isolated deployment/output directory and stops only owned servers.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import time

ROOT=Path('/workspace/TensorSharp');WORK=Path('/workspace/deepseek41-work')
sys.path.insert(0,str(ROOT/'benchmarks/engine_comparison'))
import requests
import validate_inference as validation

NATIVE_SHA='6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014'
MODELS={
 'qwen3':Path('/workspace/models/Qwen3-0.6B-GGUF/Qwen3-0.6B-Q8_0.gguf'),
 'qwen35':Path('/workspace/models/Qwen3.5-0.8B-GGUF/Qwen3.5-0.8B-Q8_0.gguf'),
 'gemma4':Path('/workspace/models/gemma-4-E2B-it-GGUF/gemma-4-E2B-it-Q4_K_M.gguf'),
}
SUITES={'quality':['short','decode','json','multi_turn','tool_round_trip'],'unicode':['json_unicode']}
ENV={'CUDA_VISIBLE_DEVICES':'7','MAX_CONTEXT':'8192','KV_CACHE_DTYPE':'f16',
 'TS_SCHED_MAX_BATCHED_TOKENS':'256','TS_SCHED_MAX_RUNNING_SEQS':'4',
 'TS_SCHED_PREFILL_CHUNK':'256','TS_SCHED_SOLO_PREFILL_CHUNK':'256',
 'TS_SCHED_PREFIX_CACHE':'0','TS_SCHED_STOP_REPETITION':'0','OMP_NUM_THREADS':'4','TS_GGML_CPU_THREADS':'4'}
PROFILE='gpu7-context8192-slots4-batch256-f16'

def file_sha(path):
 h=hashlib.sha256()
 with path.open('rb')as stream:
  for block in iter(lambda:stream.read(1<<20),b''):h.update(block)
 return h.hexdigest()
def source(path):return {'path':str(path),'sha256':file_sha(path),'bytes':path.stat().st_size}
def write(path,value):
 path.parent.mkdir(parents=True,exist_ok=True);temporary=path.with_name(path.name+'.tmp');temporary.write_text(json.dumps(value,indent=2,ensure_ascii=False)+'\n');temporary.replace(path)
def plan(scenarios):
 return {'scenarios':scenarios,'concurrency':[1,4],'repeats':1,'expected_cases':len(scenarios)*5}
def keys(scenarios):
 return {(name,f'{name}-c{degree}-r0-i{i}',degree,0)for name in scenarios for degree in (1,4)for i in range(degree)}
def case_key(case):return case.get('scenario'),case.get('tag'),case.get('concurrency'),case.get('repeat',0)
def coverage(report,scenarios):
 actual=[case_key(c)for c in report.get('cases',[])];return len(actual)==len(set(actual)) and set(actual)==keys(scenarios)
def capture(command):
 try:return subprocess.run(command,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=15).stdout
 except Exception as error:return type(error).__name__+': '+str(error)
def snapshot():
 return {'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),
  'gpu':capture(['nvidia-smi','--query-gpu=index,name,memory.used,utilization.gpu,clocks.sm,clocks.mem,power.draw,power.limit,temperature.gpu','--format=csv']),
  'processes':capture(['ps','-eo','pid,comm,pcpu,nlwp,psr']),
  'scope':'Process names only; no unrelated command-line arguments or credentials.'}

def freeze(source_host,native,deployment,expected_runtime=None):
 if deployment.exists():raise FileExistsError('Refusing to overwrite deployment: '+str(deployment))
 if file_sha(native)!=NATIVE_SHA:raise ValueError('Native source does not match explicit6b3 checkpoint')
 if not(source_host/'TensorSharp.Server.Host.dll').is_file():raise FileNotFoundError(source_host/'TensorSharp.Server.Host.dll')
 target=deployment/'TensorSharp.Server.Host/bin';target.parent.mkdir(parents=True)
 shutil.copytree(source_host,target,symlinks=False)
 shutil.copyfile(native,target/'libGgmlOps.so')
 files={str(p.relative_to(target)):file_sha(p)for p in sorted(target.rglob('*'))if p.is_file()}
 if expected_runtime and files.get('TensorSharp.Runtime.dll')!=expected_runtime:raise ValueError('Frozen Runtime does not match requested hash')
 manifest={'source_host':str(source_host),'native_source':source(native),'target':str(target),'files_sha256':files,
  'scope':'New deployment snapshot. Source host never modified. Recorded files must remain identical before/after every owned server.'}
 write(deployment/'deployment.json',manifest)
 return target,manifest

def unchanged(target,manifest):
 changes=[]
 for name,expected in manifest['files_sha256'].items():
  path=target/name
  if not path.is_file()or file_sha(path)!=expected:changes.append(name)
 return changes

def compare(reference,current):
 if not coverage(reference,SUITES['quality'])or not coverage(current,SUITES['quality']):
  return {'comparable':False,'issues':['Reference/current must contain the exact25 unique planned cases'],'introduced_failed_cases':[]}
 a={case_key(c):c for c in reference['cases']};b={case_key(c):c for c in current['cases']}
 issues=[field+' differs'for field in ('weights_id','profile','thinking','sampling','stream')if reference.get(field)!=current.get(field)]
 issues += ['request hash differs: '+str(k)for k in a if a[k]['input_sha256']!=b[k]['input_sha256']]
 def generated(case):
  return [{'content':t['metrics'].get('assistant_message',{}).get('content'),
           'reasoning_content':t['metrics'].get('assistant_message',{}).get('reasoning_content'),
           'finish_reason':t['metrics'].get('finish_reason'),
           'tool_functions':[x.get('function')for x in t['metrics'].get('assistant_message',{}).get('tool_calls',[])]}for t in case.get('turns',[])]
 introduced=[{'key':list(k),'reference':a[k],'current':b[k]}for k in a if a[k]['status']=='ok'and b[k]['status']!='ok']
 return {'comparable':not issues,'issues':issues,'cases':len(a),'baseline_pass':sum(c['status']=='ok'for c in a.values()),'current_pass':sum(c['status']=='ok'for c in b.values()),
  'matching_request_hashes':sum(a[k]['input_sha256']==b[k]['input_sha256']for k in a),'matching_generated_outputs':sum(generated(a[k])==generated(b[k])for k in a),
  'introduced_failed_cases':introduced,'changed_output_keys':[list(k)for k in a if generated(a[k])!=generated(b[k])],
  'scope':'Case-level correctness comparison. Known failures remain failures; no performance claim. Inspect any new failure for the newer assistant-content-only validator before attributing numerical regression.'}

def identity(name,weights):
 stat=weights.stat();value={'path':str(weights),'bytes':stat.st_size,'mtime_ns':stat.st_mtime_ns};cache=WORK/'existing-regressions'/f'{name}-weights.json'
 prior=json.loads(cache.read_text())if cache.is_file()else{}
 if all(prior.get(k)==v for k,v in value.items())and isinstance(prior.get('sha256'),str)and len(prior['sha256'])==64:
  value['sha256']=prior['sha256'];value['hash_origin']={'cached_source':source(cache),'validation':'Matching path, byte size and nanosecond mtime; earlier content SHA reused.'}
 else:value['sha256']=file_sha(weights);value['hash_origin']={'validation':'Content SHA computed for this run.'}
 return value

def launch_command(target,weights):
 return [str(WORK/'dotnet/dotnet'),str(target/'TensorSharp.Server.Host.dll'),'--model',str(weights),'--backend','ggml_cuda','--host','127.0.0.1','--port','5011','--max-tokens','1024','--kv-cache-dtype','f16','--no-spec','--no-prefix-cache','--no-webui','--prefill-chunk-size','256']
def stop(process):
 if process and process.poll()is None:
  os.killpg(process.pid,signal.SIGTERM)
  try:process.wait(timeout=15)
  except subprocess.TimeoutExpired:os.killpg(process.pid,signal.SIGKILL);process.wait(timeout=10)

def execute_suite(url,model_id,name,suite,report,path,stop_file):
 scenarios=SUITES[suite]
 report['warmup']=validation.run_case(url,model_id,'tensorsharp','short','warmup',timeout=90)
 write(path,report)
 for scenario in scenarios:
  for degree in (1,4):
   if stop_file.exists():raise InterruptedError('Resource window closed')
   started=time.monotonic()
   with ThreadPoolExecutor(max_workers=degree)as pool:
    jobs=[pool.submit(validation.run_case,url,model_id,'tensorsharp',scenario,f'{scenario}-c{degree}-r0-i{i}',timeout=90)for i in range(degree)]
    cases=[job.result()for job in jobs]
   for case in cases:case.update(concurrency=degree,repeat=0)
   report['cases'].extend(cases);tokens=sum(t['metrics'].get('completion_tokens',0)for c in cases for t in c['turns'])
   report['waves'].append({'scenario':scenario,'concurrency':degree,'repeat':0,'wall_ms':(time.monotonic()-started)*1000,'generated_tokens':tokens,'all_passed':all(c['status']=='ok'for c in cases)})
   report['summary']=validation.summarize(report['cases']);write(path,report)
   print(name,suite,scenario,degree,[c['status']for c in cases],flush=True)
 report['run_complete']=coverage(report,scenarios)
 report['status']='ok'if report['run_complete']and report['warmup']['status']=='ok'and all(c['status']=='ok'for c in report['cases'])else'fail'
 write(path,report)

def run_model(args,name,target,deployment,out,reference_reports,harness):
 weights=MODELS[name];weight_id=identity(name,weights);command=launch_command(target,weights)
 env={k:v for k,v in os.environ.items()if not k.startswith(('TS_','TENSORSHARP_','GGML_','KV_CACHE_','MAX_CONTEXT'))};env.update(ENV)
 reports={}
 for suite in SUITES:
  reports[suite]={'format_version':2,'engine':'tensorsharp','model':name,'weights_id':weight_id['sha256'],'weights':weight_id,'profile':PROFILE,'thinking':False,'stream':True,'sampling':validation.SAMPLING,
   'launch':command,'environment':ENV,'timings_qualified_for_comparison':False,'measurement_note':'Correctness rerun only. Unicode is separate new coverage, not included in historical75 comparison.',
   'structured_tool_results':False,'serial_tool_workflows':False,'max_tokens_override':None,'run_complete':False,'execution_plan':plan(SUITES[suite]),'status':'not_started','cases':[],'waves':[],'runner_source':source(Path(__file__)),'harness_sha256':harness,
   'binary_sha256':{k:deployment['files_sha256'][k]for k in ('TensorSharp.Server.Host.dll','TensorSharp.Runtime.dll','TensorSharp.Models.dll','TensorSharp.Chat.dll','TensorSharp.Server.dll','libGgmlOps.so')}}
  write(out/suite/(name+'-tensorsharp.json'),reports[suite])
 process=None;log=out/(name+'-server.log');before=snapshot();error=None
 try:
  if args.stop_file.exists():raise InterruptedError('Resource window closed')
  changed=unchanged(target,deployment)
  if changed:raise RuntimeError('Frozen deployment changed: '+str(changed))
  with socket.socket()as probe:
   if probe.connect_ex(('127.0.0.1',5011))==0:raise RuntimeError('Port5011 already occupied; refusing another process')
  with log.open('w')as stream:process=subprocess.Popen(command,env=env,cwd=target,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
  write(out/'active-server.json',{'pid':process.pid,'model':name,'command':command})
  deadline=time.monotonic()+180;model_id=None;url='http://127.0.0.1:5011'
  while time.monotonic()<deadline:
   if args.stop_file.exists():raise InterruptedError('Resource window closed')
   if process.poll()is not None:raise RuntimeError('Owned server exited '+str(process.returncode))
   try:
    response=requests.get(url+'/v1/models',timeout=2);rows=response.json().get('data',[])if response.status_code==200 else[]
    if rows:model_id=rows[0]['id'];break
   except(requests.RequestException,ValueError):pass
   time.sleep(.5)
  if model_id is None:raise TimeoutError('Server startup exceeded180seconds')
  for suite,report in reports.items():
   report['served_model_id']=model_id;report['status']='running'
   execute_suite(url,model_id,name,suite,report,out/suite/(name+'-tensorsharp.json'),args.stop_file)
 except Exception as failure:error=type(failure).__name__+': '+str(failure);print(name,error,flush=True)
 finally:
  stop(process);(out/'active-server.json').unlink(missing_ok=True);changes=unchanged(target,deployment);after=snapshot()
  for suite,report in reports.items():
   report['before']=before;report['after']=after;report['deployment_changed_files']=changes
   if error:report['model_run_error']=error
   if not report['run_complete']or changes:report['status']='fail'
   if log.is_file():report['server_log']=source(log)
   if suite=='quality'and report['run_complete']:
    report['comparisons']={label:{'reference_source':metadata['source'],**compare(metadata['report'],report)}for label,metadata in reference_reports[name].items()}
   write(out/suite/(name+'-tensorsharp.json'),report)
 return reports

def main():
 ap=argparse.ArgumentParser(description=__doc__);ap.add_argument('--source-host-dir',type=Path,required=True);ap.add_argument('--native',type=Path,default=WORK/'native-v41-shared-pins-6b3b5ab3.so');ap.add_argument('--expected-runtime-sha256');ap.add_argument('--label',default='final3651-native6b3');ap.add_argument('--models',default=','.join(MODELS));ap.add_argument('--stop-file',type=Path);ap.add_argument('--before-dir',type=Path,default=WORK/'existing-regressions/quality-baseline');ap.add_argument('--prior-after-dir',type=Path,default=WORK/'existing-regressions/quality');args=ap.parse_args()
 names=args.models.split(',')
 if not args.label or Path(args.label).name!=args.label or len(set(names))!=len(names)or not set(names)<=MODELS.keys():ap.error('Invalid label/models')
 out=WORK/'existing-regressions'/args.label;deployment_root=WORK/('regression-'+args.label)
 if out.exists()or deployment_root.exists():ap.error('Deployment/output already exists; choose a new label to preserve earlier evidence')
 args.stop_file=args.stop_file or out/'STOP'
 references={}
 for name in names:
  if not MODELS[name].is_file():raise FileNotFoundError(MODELS[name])
  references[name]={}
  for label,directory in [('head-baseline',args.before_dir),('earlier-after',args.prior_after_dir)]:
   path=directory/(name+'-tensorsharp.json');report=json.loads(path.read_text())
   if not coverage(report,SUITES['quality']):raise ValueError('Reference lacks exact25unique cases: '+str(path))
   references[name][label]={'source':source(path),'report':report}
 out.mkdir(parents=True);target,deployment=freeze(args.source_host_dir,args.native,deployment_root,args.expected_runtime_sha256)
 harness={Path(path).name:file_sha(Path(path))for path in (validation.__file__,validation.engines.__file__,validation.scenarios.__file__)}
 overall={'label':args.label,'runner_source':source(Path(__file__)),'harness_sha256':harness,'deployment_manifest':source(deployment_root/'deployment.json'),'planned_models':names,'planned_suites':SUITES,'expected_quality_cases':25*len(names),'expected_unicode_cases':5*len(names),'run_complete':False,'all_cases_passed':False,'timings_qualified_for_comparison':False,'runs':[]}
 write(out/'run.json',overall)
 for name in names:
  reports=run_model(args,name,target,deployment,out,references,harness)
  overall['runs'].append({'model':name,'suites':{suite:{'source':source(out/suite/(name+'-tensorsharp.json')),'complete':r['run_complete'],'status':r['status'],'passed':sum(c['status']=='ok'for c in r['cases']),'cases':len(r['cases'])}for suite,r in reports.items()},'comparisons':reports['quality'].get('comparisons',{})});write(out/'run.json',overall)
 overall['run_complete']=len(overall['runs'])==len(names)and all(x['complete']for r in overall['runs']for x in r['suites'].values())
 overall['all_cases_passed']=overall['run_complete']and all(x['status']=='ok'for r in overall['runs']for x in r['suites'].values())
 overall['introduced_failed_cases']={r['model']:{label:len(c['introduced_failed_cases'])if c['comparable']else None for label,c in r['comparisons'].items()}for r in overall['runs']}
 overall['finished_utc']=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime());write(out/'run.json',overall)
 print(json.dumps({'report':str(out/'run.json'),'run_complete':overall['run_complete'],'all_cases_passed':overall['all_cases_passed'],'introduced_failed_cases':overall['introduced_failed_cases']},indent=2))
 return 0 if overall['all_cases_passed']else 1
if __name__=='__main__':raise SystemExit(main())
