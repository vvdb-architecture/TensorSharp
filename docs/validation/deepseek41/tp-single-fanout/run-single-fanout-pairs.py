import datetime,hashlib,json,os,pathlib,statistics,subprocess,time
base=pathlib.Path('/workspace/deepseek41-work')
exe=pathlib.Path('/workspace/TensorSharp/TensorSharp.GGML.Native/build/GgmlOpsDsv41TpTest')
lib=exe.parent/'libGgmlOps.so'
def sha(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  for b in iter(lambda:f.read(1<<20),b''): h.update(b)
 return h.hexdigest()
def command(argv):
 p=subprocess.run(argv,text=True,capture_output=True)
 return {'command':argv,'returncode':p.returncode,'stdout':p.stdout,'stderr':p.stderr}
def snapshot():
 return {'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'gpu':command(['nvidia-smi','--query-gpu=index,name,uuid,clocks.current.sm,clocks.current.memory,temperature.gpu,power.draw,power.limit,utilization.gpu,memory.used','--format=csv']),
 'compute_processes':command(['nvidia-smi','--query-compute-apps=pid,process_name,used_memory','--format=csv']),
 'cpu_processes':command(['ps','-eo','pid,pcpu,pmem,stat,comm','--sort=-pcpu'])}
report={'scope':'Exclusive isolated synthetic routed-MoE paired fanout timing; parent confirmed model ready/idle and other validation jobs complete. CPU enclosing backend uses one thread. No full-model performance claim.','native_sha256':sha(lib),'test_executable_sha256':sha(exe),'script_sha256':sha(pathlib.Path(__file__)),'pairs_per_shape':30,'warmups_per_mode':10,'starting_state':snapshot(),'runs':[]}
if report['native_sha256']!='8e66aaf4e724d4da9442f14ff01a901db038b89a9e1daa0df9d701ceeb5dc242':raise RuntimeError('unexpected candidate binary')
for ranks in (2,4,8):
 argv=[str(exe),'--cuda',str(ranks),'--fanout-pairs','30']
 env=dict(os.environ,CUDA_VISIBLE_DEVICES='0,1,2,3,4,5,6,7')
 log=base/f'native-single-fanout-pairs-cuda{ranks}.log'
 start=time.monotonic(); utc=datetime.datetime.now(datetime.timezone.utc).isoformat()
 with log.open('w') as stream:p=subprocess.run(argv,env=env,stdout=stream,stderr=subprocess.STDOUT)
 lines=[json.loads(line[len('FANOUT_BENCH '):]) for line in log.read_text().splitlines() if line.startswith('FANOUT_BENCH ')]
 for row in lines:
  old=row['two_fanout_ms'];new=row['single_fanout_ms']
  row['summary']={'two_fanout_median_ms':statistics.median(old),'single_fanout_median_ms':statistics.median(new),'median_ratio_old_over_new':statistics.median(old)/statistics.median(new),'paired_ratio_median':statistics.median(a/b for a,b in zip(old,new)),'single_fanout_faster_pairs':sum(b<a for a,b in zip(old,new)),'max_two_fanout_ms':max(old),'max_single_fanout_ms':max(new)}
 report['runs'].append({'command':argv,'environment':{'CUDA_VISIBLE_DEVICES':env['CUDA_VISIBLE_DEVICES']},'start_utc':utc,'wall_seconds':time.monotonic()-start,'exit_code':p.returncode,'log':log.name,'log_sha256':sha(log),'results':lines})
 print(json.dumps({'ranks':ranks,'exit_code':p.returncode,'results':[{'tokens':r['tokens'],**r['summary']} for r in lines]}),flush=True)
report['ending_state']=snapshot()
report['all_passed']=all(r['exit_code']==0 and len(r['results'])==2 and all(x['bitwise_equal'] and len(x['two_fanout_ms'])==30 and len(x['single_fanout_ms'])==30 for x in r['results']) for r in report['runs'])
(base/'native-single-fanout-paired.json').write_text(json.dumps(report,indent=2)+'\n')
raise SystemExit(0 if report['all_passed'] else 1)
