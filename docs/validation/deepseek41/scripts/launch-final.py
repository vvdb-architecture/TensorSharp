import argparse,hashlib,json,os,pathlib,shutil,subprocess,time
p=argparse.ArgumentParser();p.add_argument('profile');p.add_argument('--cpu-moe',type=int,default=0);p.add_argument('--tp',type=int,default=0);p.add_argument('--sparse',type=int,default=1);p.add_argument('--gpus',type=int,default=8);p.add_argument('--ubatch',type=int,default=256);p.add_argument('--server-dir',default='server-v41-next');p.add_argument('--native',default='/workspace/TensorSharp/TensorSharp.GGML.Native/build/libGgmlOps.so');p.add_argument('--mmproj');p.add_argument('--compact',type=int,choices=[0,1],default=0);p.add_argument('--max-running',type=int,default=4);p.add_argument('--prefill-chunk',type=int,default=256);p.add_argument('--solo-prefill-chunk',type=int,default=8192);p.add_argument('--max-batched',type=int,default=4096);p.add_argument('--num-blocks',type=int);p.add_argument('--cpu-threads',type=int)
a=p.parse_args();work=pathlib.Path('/workspace/deepseek41-work');server=work/a.server_dir;report=work/(a.profile+'-launch.json');log=work/(a.profile+'-server.log')
source=pathlib.Path(a.native)
shutil.copy2(source,server/'libGgmlOps.so.next');os.replace(server/'libGgmlOps.so.next',server/'libGgmlOps.so')
env=dict(CUDA_VISIBLE_DEVICES=','.join(map(str,range(a.gpus))),MAX_CONTEXT='65536',TS_DSV4_UBATCH=str(a.ubatch),TS_DSV4_NGPU=str(a.gpus),KV_CACHE_DTYPE='f16',TS_DSV41_TP=str(a.tp),TS_DSV41_SPARSE_FA=str(a.sparse),TS_DSV41_COMPACT_RAW_GATHER=str(a.compact),TS_DSV41_ENGRAM_WARM='1',TS_DSV41_ENGRAM_THREADS='16',TS_DSV4_PERF='1',TS_SCHED_MAX_RUNNING_SEQS=str(a.max_running),TS_SCHED_PREFILL_CHUNK=str(a.prefill_chunk),TS_SCHED_SOLO_PREFILL_CHUNK=str(a.solo_prefill_chunk),TS_SCHED_MAX_BATCHED_TOKENS=str(a.max_batched))
if a.cpu_threads is not None:
 if a.cpu_threads < 1: p.error('cpu-threads must be positive')
 env['TS_CPU_MOE_THREADS']=str(a.cpu_threads)
if a.num_blocks is not None: env['TS_SCHED_NUM_BLOCKS']=str(a.num_blocks)
cmd=[str(work/'dotnet/dotnet'),str(server/'TensorSharp.Server.Host.dll'),'--model','/workspace/models/DeepSeek-V4.1-Flash-Q2_K/DeepSeek-V4.1-Flash-Q2_K-00001-of-00007.gguf','--backend','ggml_cuda','--tp',str(a.gpus),'--host','127.0.0.1','--port','5000','--max-tokens','2048','--n-cpu-moe',str(a.cpu_moe),'--cpu-moe-threads',str(a.cpu_threads if a.cpu_threads is not None else 48)]
if a.mmproj: cmd += ['--mmproj',a.mmproj]
r={'profile':a.profile,'started_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'command':cmd,'environment':env,'sha256':{f.name:hashlib.file_digest(f.open('rb'),'sha256').hexdigest() for f in server.iterdir() if f.suffix=='.dll' or f.name=='libGgmlOps.so'},'log':str(log),'gpu_at_start':subprocess.check_output(['nvidia-smi','--query-gpu=index,name,memory.used,memory.total,clocks.current.sm,temperature.gpu','--format=csv,noheader'],text=True)}
with log.open('w') as out:
 process=subprocess.Popen(cmd,stdout=out,stderr=subprocess.STDOUT,cwd='/workspace/TensorSharp',env={**os.environ,**env},start_new_session=True)
r['pid']=process.pid;report.write_text(json.dumps(r,indent=2)+'\n');print(json.dumps({'pid':process.pid,'log':str(log),'report':str(report),'native_sha256':r['sha256']['libGgmlOps.so']}))
