import argparse,json,pathlib,subprocess,time,urllib.request,re,hashlib

def child_evidence(output, code, profile):
 issues=[]; report={}
 try:
  report=json.loads(output.read_text())
  if not isinstance(report,dict): raise ValueError('child report is not an object')
 except (OSError,ValueError) as error:
  issues.append('child report unavailable or invalid: '+str(error)); report={}
 plan=report.get('execution_plan',{}); cases=report.get('cases',[])
 expected=plan.get('expected_cases') if isinstance(plan,dict) else None
 if report.get('run_complete') is not True: issues.append('child run_complete is missing or not true')
 if report.get('profile') != profile: issues.append('child profile does not match this launch')
 if not isinstance(cases,list): issues.append('child cases is not an array'); cases=[]
 if type(expected) is not int or expected<1 or len(cases)!=expected:
  issues.append('child expected/actual case counts are missing or inconsistent')
 statuses=[case.get('status') if isinstance(case,dict) else None for case in cases]
 if any(status not in ('ok','fail') for status in statuses): issues.append('child case status is missing or unknown')
 failed=sum(status!='ok' for status in statuses)
 complete=not issues
 passed=complete and code==0 and failed==0
 return {'run_complete':complete,'expected_cases':expected,'actual_cases':len(cases),'failed_cases':failed,
         'all_passed':passed,'qualification_status':'complete_passed' if passed else 'complete_failed' if complete else 'incomplete',
         'issues':issues,'report_sha256':hashlib.sha256(output.read_bytes()).hexdigest() if output.is_file() else None}

def write_report(path,report):
 temporary=path.with_name(path.name+'.tmp'); temporary.write_text(json.dumps(report,indent=2)+'\n'); temporary.replace(path)

p=argparse.ArgumentParser();p.add_argument('profile');p.add_argument('--phase',choices=['quality','steady','decode8k','long','long-parallel','protocol','placement','tools','multilingual','media','thread-control','serial-tools'],default='quality');a=p.parse_args();w=pathlib.Path('/workspace/deepseek41-work')
base=[str(w/'venv/bin/python'),'/workspace/TensorSharp/benchmarks/engine_comparison/validate_inference.py','--url','http://127.0.0.1:5000','--engine','tensorsharp','--model','DeepSeek-V4.1-Flash-Q2_K-00001-of-00007','--weights-id','8e0c4de3cb6519bfc11ed69dc87184b457a57bb5-Q2_K','--profile',a.profile,'--timeout','2400']
phases={'quality': [('quality',['--scenarios','short,json,json_schema,multi_turn,tool_round_trip,agentic','--concurrency','1,4','--repeats','1','--structured-tool-results']),('unconstrained-tools',['--scenarios','tool_round_trip,agentic','--concurrency','1,4','--repeats','1'])], 'steady':[('steady',['--scenarios','short,decode','--concurrency','1,4','--repeats','3'])], 'long':[('long',['--scenarios','long_8k,long_32k','--concurrency','1','--repeats','3'])], 'long-parallel':[('long-parallel',['--scenarios','long_8k,long_32k','--concurrency','4','--repeats','1'])], 'protocol':[('thinking',['--scenarios','short,json_schema,tool_round_trip,agentic','--concurrency','1','--repeats','1','--structured-tool-results','--thinking','--max-tokens','2048']),('blocking',['--scenarios','short,json,tool_round_trip,agentic','--concurrency','1','--repeats','1','--structured-tool-results','--blocking'])], 'placement':[('quality',['--scenarios','short,json,json_schema,multi_turn,tool_round_trip,agentic','--concurrency','1,4','--repeats','1','--structured-tool-results']),('steady',['--scenarios','decode','--concurrency','1,4','--repeats','3']),('long',['--scenarios','long_8k,long_32k','--concurrency','1','--repeats','1'])]}
phases['thread-control']=[('thread-control',['--scenarios','decode','--concurrency','1','--repeats','3'])]
phases['serial-tools']=[('serial-tools',['--scenarios','tool_round_trip,agentic','--concurrency','1,4','--repeats','1','--structured-tool-results','--serial-tool-workflows'])]
phases['tools']=[('tool-policies',[])]
phases['multilingual']=[('multilingual',['--scenarios','short_zh,json_unicode','--concurrency','1,4','--repeats','1'])]
phases['media']=[('media',[])]
phases['decode8k']=[('decode8k-c1',['--scenarios','decode_8k','--concurrency','1','--repeats','3']),('decode8k-c4',['--scenarios','decode_8k','--concurrency','4','--repeats','1'])]
telemetry=(w/(a.profile+'-'+a.phase+'-gpu.csv')).open('w');monitor=subprocess.Popen(['nvidia-smi','--query-gpu=timestamp,index,utilization.gpu,utilization.memory,memory.used,clocks.current.sm,clocks.current.memory,temperature.gpu,power.draw','--format=csv','-l','2'],stdout=telemetry,stderr=subprocess.STDOUT)
r={'profile':a.profile,'phase':a.phase,'runner_sha256':hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
   'execution_plan':{'labels':[label for label,extra in phases[a.phase]],'expected_runs':len(phases[a.phase])},
   'run_complete':False,'all_passed':False,'runs':[]}
report_path=w/(a.profile+'-'+a.phase+'-runs.json')
write_report(report_path,r)
server_log=pathlib.Path(json.loads((w/(a.profile+'-launch.json')).read_text())['log'])
try:
 for label,extra in phases[a.phase]:
  output=w/(a.profile+'-'+label+'.json')
  selected_base=base
  if label in ('tool-policies','media'):
   script='validate_deepseek41_tools.py' if label=='tool-policies' else 'validate_deepseek41_media.py'
   selected_base=[str(w/'venv/bin/python'),'/workspace/TensorSharp/benchmarks/engine_comparison/'+script,'--url','http://127.0.0.1:5000','--model','DeepSeek-V4.1-Flash-Q2_K-00001-of-00007','--weights-id','8e0c4de3cb6519bfc11ed69dc87184b457a57bb5-Q2_K','--profile',a.profile,'--timeout','2400']
   if label=='tool-policies':
    launch=json.loads((w/(a.profile+'-launch.json')).read_text()); selected_base += ['--native-sha256',launch['sha256']['libGgmlOps.so'],'--server-build',launch['sha256']['TensorSharp.Runtime.dll']]
   else: selected_base=selected_base[:-2]; selected_base += ['--fixtures',str(w/'media-fixtures'),'--companion-sha256','e7b0debed15706dd2f065879fa62a54f49e5c0472a54fa33d5ff40957f167e0c']
  cmd=selected_base+extra+['--output',str(output)];log=w/(a.profile+'-'+label+'.log');start=time.time();server_offset=server_log.stat().st_size;print('Starting '+label,flush=True)
  with log.open('w') as out: code=subprocess.run(cmd,stdout=out,stderr=subprocess.STDOUT).returncode
  with server_log.open('rb') as src:src.seek(server_offset);server_bytes=src.read()
  forwards=[(int(n),float(t)) for n,t in re.findall(r'\[dsv4\] forward (\d+) tokens in ([\d.]+)s',server_bytes.decode(errors='replace'))]
  accounting={'server_log':str(server_log),'byte_start':server_offset,'byte_end':server_offset+len(server_bytes),'range_sha256':hashlib.sha256(server_bytes).hexdigest(),'prefill_calls':sum(n>1 for n,t in forwards),'prefill_tokens':sum(n for n,t in forwards if n>1),'prefill_forward_seconds':sum(t for n,t in forwards if n>1),'decode_calls':sum(n==1 for n,t in forwards),'decode_forward_seconds':sum(t for n,t in forwards if n==1),'preemption_lines':[line for line in server_bytes.decode(errors='replace').splitlines() if 'preempting' in line or 'preempted' in line]}
  r['runs'].append({'label':label,'command':cmd,'started_epoch':start,'wall_seconds':time.time()-start,'exit_code':code,'output':str(output),'log':str(log),'native_accounting':accounting,'child_validation':child_evidence(output,code,a.profile)})
  print(json.dumps(r['runs'][-1]),flush=True)
  write_report(report_path,r)
 r['run_complete']=len(r['runs'])==r['execution_plan']['expected_runs']
 r['children_exit_zero']=all(run['exit_code']==0 for run in r['runs'])
 r['all_passed']=r['run_complete'] and all(run['child_validation']['all_passed'] for run in r['runs'])
 write_report(report_path,r)
except BaseException as error:
 r['runner_error']=type(error).__name__+': '+str(error); write_report(report_path,r); raise
finally:
 monitor.terminate();monitor.wait();telemetry.close()
raise SystemExit(0 if r['run_complete'] and r['all_passed'] else 1)
