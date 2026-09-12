"""Offline audit of the separate phase-1 uninstrumented CLI confirmation."""
from pathlib import Path
import csv,gzip,hashlib,json,re
W=Path('/tmp/deepseek41-reference');O=Path('/Users/zhongkaifu/work/TensorSharp/docs/validation/deepseek41/cli-gpu-execution');P=O/'phase1/untraced'
def source(p):
 b=p.read_bytes();return {'path':str(p),'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()}
def copy(p,q):
 q.parent.mkdir(parents=True,exist_ok=True)
 if q.exists():assert q.read_bytes()==p.read_bytes(),q
 else:q.write_bytes(p.read_bytes())
text=(W/'user-after-untraced.log').read_text();inputs=(W/'cli-gpu-untraced-input.txt').read_text().splitlines()
assert inputs==['What is 17 plus 25? Give a brief answer.','/reset','/temp 0','/seed 42','/max 128','What is 31 plus 47? Give a brief answer.','/reset','What is 31 plus 47? Give a brief answer.','/exit']
assert not re.search(r'\[dsv4\] (ubatch|forward) ',text)
assert '16:24:15 info: TensorSharp.Cli[1701] tensorsharp-cli completed' in text
assert re.findall(r'compute device (\d): backend=(CUDA\d), device=(CUDA\d) \(NVIDIA A40\)',text)==[(str(i),'CUDA'+str(i),'CUDA'+str(i))for i in range(8)]
assert 'routed-expert CPU offload: 0 of 40 layer(s); 40 layer(s) on GPUs' in text
bodies=[x.strip()for x in re.findall(r'Assistant:\s*(.*?)\n\[turn complete:',text,re.S)];assert len(bodies)==3 and bodies[0].endswith('[answer] 42')
phase=json.loads((O/'phase1/audit.json').read_text());expected=phase['requests'][0]['output'];assert bodies[1]==bodies[2]==expected
pattern=r'\[turn complete: tokens=(\d+) prefillMs=(\d+) decodeMs=(\d+) tps=([\d.]+) ttftMs=(\d+) reason=(\S+) kvPlan=(\S+)\]'
turns=re.findall(pattern,text);assert len(turns)==3 and [int(r[0])for r in turns]==[25,41,41]
records=[]
for i,(values,body)in enumerate(zip(turns,bodies)):
 n,prefill,decode,tps,ttft,reason,kv=values;assert reason=='eos' and kv=='Reset' and abs(int(n)/(int(decode)/1000)-float(tps))<.1
 records.append({'case':i,'prompt':inputs[0] if i==0 else inputs[5],'passed':True,'generated_tokens':int(n),'prefill_ms':int(prefill),'decode_ms':int(decode),'cli_reported_tps':float(tps),'cli_reported_ttft_ms':int(ttft),'finish_reason':reason,'kv_plan':kv,'sampling_scope':'Original defaults/random seed' if i==0 else 'temperature0, seed42, max128, reset','output':body,'output_sha256':hashlib.sha256(body.encode()).hexdigest()})
gpu=W/'user-after-untraced-gpu.csv';rows=list(csv.reader(gpu.read_text().splitlines()));assert rows and all(len(r)==7 for r in rows)and{int(r[1])for r in rows}==set(range(8))
last={int(r[1]):r for r in rows};assert all(float(r[4].strip().split()[0])==0 for r in last.values())
for name in ['user-after-untraced.log','cli-gpu-untraced-input.txt']:copy(W/name,P/name)
z=P/'user-after-untraced-gpu.csv.gz';z.write_bytes(gzip.compress(gpu.read_bytes(),mtime=0));assert gzip.decompress(z.read_bytes())==gpu.read_bytes()
copy(Path(__file__),O/'audit-cli-gpu-untraced.py')
report={'scope':'Three completed uninstrumented phase-1 CLI confirmation requests; observational throughput only, no paired cache-controlled speedup. No inference or VM command run by auditor.','audit_complete':True,'passed':3,'total':3,'native_sha256':phase['native_after_sha256'],'native_identity_scope':'Phase-1 deployed hash/provenance retained; parent additionally reported rehash after this run. No local auditor rehash of remote library.','source':source(Path(__file__)),'input_source':source(W/'cli-gpu-untraced-input.txt'),'log_source':source(W/'user-after-untraced.log'),'cases':records,'both_deterministic_outputs_exactly_match_before_and_perf2':True,'cli_load_elapsed_ms':float(re.search(r'architecture=deepseek41 .*?elapsedMs=([\d.]+)',text)[1]),'gpu_telemetry':{'raw_source':source(gpu),'compressed_source':source(z),'rows':len(rows),'last_per_device':last,'all_final_samples_memory_zero':True,'scope':'Capture includes load/idle, has no PID column and is not a request-level utilization qualification.'},'limitations':['Original uninstrumented baseline generated97 tokens with random sampling; this run generated25, so identical arithmetic answer does not make throughput directly comparable.','Controlled41-token requests preserve full output but no matching uninstrumented/cache-controlled old-binary pair was measured.','Startup filesystem cache and storage conditions were uncontrolled;225280.3ms load is an observation, not a paired startup regression.','This phase remains native1d920 with source24246c; subsequent optimizations must use a separate evidence stage.']}
(P/'audit.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'passed':3,'tps':[r['cli_reported_tps']for r in records],'all_deterministic_outputs_match':True,'final_gpu_memory_zero':True},indent=2))
