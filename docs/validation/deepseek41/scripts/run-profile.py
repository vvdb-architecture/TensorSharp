import argparse, hashlib, json, pathlib, subprocess, time
p=argparse.ArgumentParser();p.add_argument('profile');p.add_argument('--phases',required=True);a=p.parse_args()
w=pathlib.Path('/workspace/deepseek41-work');script=w/'run-final-suite.py';phases=a.phases.split(',')
r={'profile':a.profile,'planned_phases':phases,'run_complete':False,'runner_sha256':hashlib.sha256(script.read_bytes()).hexdigest(),'started_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'runs':[]}
report=w/(a.profile+'-profile-runs.json')
def save(): report.write_text(json.dumps(r,indent=2)+'\n')
save()
for phase in phases:
 cmd=['python3',str(script),a.profile,'--phase',phase];start=time.time();print('Starting phase '+phase,flush=True)
 code=subprocess.run(cmd).returncode
 r['runs'].append({'phase':phase,'command':cmd,'exit_code':code,'wall_seconds':time.time()-start});save()
r['run_complete']=len(r['runs'])==len(phases);r['all_passed']=all(x['exit_code']==0 for x in r['runs']);r['finished_utc']=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime());save();print(json.dumps(r),flush=True)
raise SystemExit(0 if r['all_passed'] else 1)
