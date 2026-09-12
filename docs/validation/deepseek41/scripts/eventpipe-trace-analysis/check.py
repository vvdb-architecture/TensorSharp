#!/usr/bin/env python3
"""Real retained traces plus isolated negative fixtures; no VM or inference."""
import copy, hashlib, json, subprocess, tempfile
from pathlib import Path
HERE=Path(__file__).resolve().parent
RAW=HERE.parent/'qwen35-eventpipe-final-native-r1'
DOTNET='/Users/zhongkaifu/.dotnet/dotnet'
DLL=HERE/'bin/Release/net10.0/trace-analysis.dll'
def source(p):return dict(path=str(p),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest())
def write(p,o):p.write_text(json.dumps(o,indent=2)+'\n')
checks=[]
def check(name,ok):
 if not ok:raise AssertionError(name)
 checks.append(name)
selftest=subprocess.run([DOTNET,str(DLL),'--self-test'],capture_output=True,text=True,check=True)
write(HERE/'self-test.json',json.loads(selftest.stdout))
for name in json.loads(selftest.stdout)['checks']:checks.append('reducer: '+name)
with tempfile.TemporaryDirectory(prefix='trace-analysis-guards-') as t:
 tmp=Path(t)
 original_m=json.loads((RAW/'qwen35-baseline-managed_final-native-eventpipe.json').read_text())
 original_r=json.loads((RAW/'qwen35-baseline-managed_final-native.json').read_text())
 original_trace=RAW/'qwen35-baseline-managed_final-native.nettrace'
 def invoke(name, mutate=None, expected=1, trace=None, unbound=False):
  d=tmp/name;d.mkdir();m=copy.deepcopy(original_m);r=copy.deepcopy(original_r);p=trace or original_trace
  if mutate:mutate(m,r)
  mp=d/'capture.json';rp=d/'request.json';write(mp,m)
  if not unbound:r['eventpipe_evidence']=source(mp)
  write(rp,r)
  run=subprocess.run([DOTNET,str(DLL),'--trace',str(p),'--metadata',str(mp),'--report',str(rp),'--out',str(d/'analysis')],capture_output=True,text=True)
  (d/'process.log').write_text(run.stdout+run.stderr)
  summary=json.loads((d/'analysis/summary.json').read_text()) if (d/'analysis/summary.json').exists() else None
  check(name,run.returncode!=0 if expected else run.returncode==0)
  return summary,run
 invoke('unbound capture rejected',lambda m,r:r.update(eventpipe_evidence=dict(sha256='0'*64,bytes=1)),unbound=True)
 invoke('incomplete request coverage rejected',lambda m,r:r.update(run_complete=False))
 invoke('capture build mismatch rejected',lambda m,r:r.update(version='wrong'))
 invoke('report capture interval mismatch rejected',lambda m,r:r.update(timed_monotonic_interval=[1,2]))
 invoke('request interval outside collector rejected',lambda m,r:m['stop_requested'].update(monotonic=1))
 invoke('unordered anchors rejected',lambda m,r:m['collection_ready'].update(monotonic=1))
 invoke('invalid UTC unix anchor rejected',lambda m,r:m['collection_ready'].update(unix_seconds=1))
 invoke('negative uncertainty rejected',lambda m,r:m['collection_ready'].update(anchor_uncertainty_seconds=-1))
 invoke('raw trace hash mismatch rejected',lambda m,r:m['raw_nettrace'].update(sha256='0'*64))
 invoke('missing case rejected',lambda m,r:r['cases'].pop())
 invoke('duplicate case rejected',lambda m,r:r['cases'].__setitem__(1,copy.deepcopy(r['cases'][0])))
 invoke('out of window request rejected',lambda m,r:r['cases'][0]['turns'][0]['metrics'].update(t_start_abs=1))
 summary,_=invoke('wrong target PID cannot pass integrity',lambda m,r:m.update(host_pid=2147483000))
 check('wrong PID preserves parse result and explicit failure',summary and summary['analysis_complete'] and not summary['window_integrity_passed'] and summary['session']['target_events']==0)
 summary,_=invoke('collector failure remains failed offline',lambda m,r:m.update(collection_complete=False))
 check('collector failure preserves valid raw event results',summary and summary['session']['target_sample_count']>0 and not summary['window_integrity_passed'])
 truncated=tmp/'truncated.nettrace';truncated.write_bytes(original_trace.read_bytes()[:original_trace.stat().st_size//2])
 summary,run=invoke('truncated raw trace cannot pass integrity',lambda m,r:m.update(raw_nettrace=source(truncated)),trace=truncated)
 check('truncated failure retains explicit analyzer result',summary and not summary['window_integrity_passed'])
for version in ['baseline','final']:
 out=HERE.parent/f'qwen35-eventpipe-analysis-{version}-r3'
 cmd=[DOTNET,str(DLL),'--trace',str(RAW/f'qwen35-{version}-managed_final-native.nettrace'),'--metadata',str(RAW/f'qwen35-{version}-managed_final-native-eventpipe.json'),'--report',str(RAW/f'qwen35-{version}-managed_final-native.json'),'--out',str(out)]
 run=subprocess.run(cmd,capture_output=True,text=True);(HERE/f'{version}-parse.log').write_text(run.stdout+run.stderr)
 check(version+' real trace parse succeeds',run.returncode==0)
 s=json.loads((out/'summary.json').read_text())
 check(version+' zero loss, inversion, missing and depth limit',s['session']['events_lost']==0 and s['session']['first_time_inversion']=='Invalid' and s['session']['missing_stack_samples']==s['session']['depth_limit_samples']==0)
 check(version+' all15 cases and engine samples retained',len(s['request_windows'])==15 and s['session']['engine_worker_samples']>0)
 check(version+' profiler and GC reasons kept separate',any(p['reason']=='SuspendOther' for p in s['runtime_suspension_by_reason']) and all(p['Reason']!='SuspendOther' for p in s['gc_reason_suspensions']))
 repeat=subprocess.run(cmd,capture_output=True,text=True)
 check(version+' existing analysis refuses overwrite',repeat.returncode!=0)
write(HERE/'checks.json',dict(passed=len(checks),checks=checks,source=source(HERE/'Program.cs'),project=source(HERE/'trace-analysis.csproj'),guard=source(Path(__file__)),binary=source(DLL),scope='Local real retained trace decoding and isolated negative controls. No new collection, model inference, or VM jobs.'))
print(json.dumps(dict(passed=len(checks),output=str(HERE/'checks.json'))))
