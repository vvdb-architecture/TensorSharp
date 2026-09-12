import json,os,pathlib,signal,subprocess,time
w=pathlib.Path('/workspace/deepseek41-work');profile='tp8-context65536-ubatch1024-cpumoe0-cputhreads1-sparse1-compact1-chunk1024-b26c-tags'
phase=w/(profile+'-placement-runs.json');quality=w/(profile+'-quality.json')
while True:
 try:
  r=json.loads(phase.read_text());q=json.loads(quality.read_text())
  if q.get('run_complete') is True and any(x['label']=='quality' for x in r['runs']):break
 except (OSError,ValueError):pass
 time.sleep(1)
rows=[]
for line in subprocess.check_output(['ps','-eo','pid=,ppid=,args='],text=True).splitlines():
 parts=line.strip().split(None,2)
 if len(parts)==3: rows.append((int(parts[0]),int(parts[1]),parts[2]))
parents=[pid for pid,ppid,cmd in rows if profile in cmd and any('/'+name in cmd for name in ('run-profile.py','run-final-suite.py')) and pid!=os.getpid()]
selected=set(parents)
for _ in range(8):
 for pid,ppid,cmd in rows:
  if ppid in selected:selected.add(pid)
commands=[{'pid':pid,'ppid':ppid,'command':cmd} for pid,ppid,cmd in rows if pid in selected]
for pid in parents+[pid for pid in selected if pid not in parents]:
 try:os.kill(pid,signal.SIGTERM)
 except ProcessLookupError:pass
reason='Stopped inferior one-native-CPU-thread tuning experiment after the complete 30-case quality suite. Any partially started sustained-decode report and all unrun planned phases remain incomplete; they are not passing or qualified performance evidence. A separate planned three-repeat single-request control follows.'
a={'profile':profile,'terminated_epoch':time.time(),'reason':reason,'complete_quality_report':str(quality),'quality_cases':len(q['cases']),'quality_passed':sum(x['status']=='ok' for x in q['cases']),'terminated_processes':commands}
(w/(profile+'-thread-experiment-abort.json')).write_text(json.dumps(a,indent=2)+'\n')
for path in (w/(profile+'-profile-runs.json'),phase):
 v=json.loads(path.read_text());v['intentional_termination']=a;v['run_complete']=False;v['all_passed']=False;path.write_text(json.dumps(v,indent=2)+'\n')
print(json.dumps(a),flush=True)
