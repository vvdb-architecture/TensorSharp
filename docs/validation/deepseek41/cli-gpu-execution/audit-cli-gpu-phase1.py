"""Bounded offline audit of phase-1 CLI and Engram scratch-file controls."""
from pathlib import Path
import csv,gzip,hashlib,json,re,statistics
W=Path('/tmp/deepseek41-reference');R=Path('/Users/zhongkaifu/work/TensorSharp');O=R/'docs/validation/deepseek41/cli-gpu-execution';P=O/'phase1'
def source(p):
 b=p.read_bytes();return {'path':str(p),'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()}
def read(p):return json.loads(p.read_text())
def copy(p,q):
 q.parent.mkdir(parents=True,exist_ok=True)
 if q.exists():assert q.read_bytes()==p.read_bytes(),q
 else:q.write_bytes(p.read_bytes())
def summary(rows,k):return {'values':[r[k]for r in rows],'median':statistics.median(r[k]for r in rows),'sum':sum(r[k]for r in rows)}
provenance=read(W/'cli-gpu-provenance.json');assert provenance['vm_working_directory']=='/root/TensorSharp/TensorSharp.Cli/bin'
assert provenance['native_after_sha256']=='1d9209883c7275e545cfb18c4fd53e7ab4eb51bcef6c8d3bc83c328d5c817835'
assert provenance['native_before_sha256']=='c68df1f3d0c70269be249c12a9eae9e7e6ae9144e232dda311d4e853c2d048d0'
hashes={p:s for s,p in (line.split(maxsplit=1)for line in (W/'deployed-sha256.txt').read_text().splitlines())}
assert hashes['/root/TensorSharp/TensorSharp.Cli/bin/libGgmlOps.so']==provenance['native_after_sha256']
for name,sha in provenance['source_sha256'].items():
 assert source(R/name)['sha256']==sha
 if '/root/TensorSharp/'+name in hashes:assert hashes['/root/TensorSharp/'+name]==sha
 copy(R/name,P/'sources'/Path(name).relative_to('TensorSharp.GGML.Native'))
build=(W/'native-decode-build.log').read_text();assert '[100%] Built target GgmlOps' in build
raw=(W/'user-after-perf2.log').read_text();clean=re.sub(r'\[dsv4\][^\n]*\n','',raw)
assert '16:18:24 info: TensorSharp.Cli[1701] tensorsharp-cli completed' in raw
assert re.findall(r'compute device (\d): backend=(CUDA\d), device=(CUDA\d) \(NVIDIA A40\)',raw)==[(str(i),'CUDA'+str(i),'CUDA'+str(i))for i in range(8)]
assert '[dsv4] auxiliary CPU worker pool: threads=32, persistent=yes' in raw
assert '[dsv4] routed-expert CPU offload: 0 of 40 layer(s); 40 layer(s) on GPUs' in raw
assert not re.search(r'\[dsv4\]\s+node\s+',raw),'Unexpected PERF3 instrumentation'
pat=r'\[dsv4\] ubatch nt=(\d+) p0=(\d+): build ([\d.]+)ms alloc ([\d.]+)ms inputs ([\d.]+)ms compute ([\d.]+)ms \(nodes (\d+), splits (\d+)(, reused)?\)'
matches=list(re.finditer(pat,raw));assert len(matches)==raw.count('[dsv4] ubatch')
rows=[]
for i,m in enumerate(matches):
 v=m.groups();rows.append({'index':i,'offset':m.start(),'nt':int(v[0]),'p0':int(v[1]),'build_ms':float(v[2]),'alloc_ms':float(v[3]),'inputs_ms':float(v[4]),'compute_ms':float(v[5]),'nodes':int(v[6]),'splits':int(v[7]),'reused':bool(v[8])})
turnpat=r'\[turn complete: tokens=(\d+) prefillMs=(\d+) decodeMs=(\d+) tps=([\d.]+) ttftMs=(\d+) reason=(\S+) kvPlan=(\S+)\]'
turns=list(re.finditer(turnpat,raw));assert len(turns)==5
bodies=[x.strip()for x in re.findall(r'Assistant:\s*(.*?)\n\[turn complete:',clean,re.S)];assert len(bodies)==5
beforeclean=(O/'before/user-before-perf-clean.txt').read_text();beforebody=[x.strip()for x in re.findall(r'Assistant:\s*(.*?)\n\[turn complete:',beforeclean,re.S)][1]
assert bodies[0]==bodies[1]==beforebody and bodies[0].endswith('[answer] 78')
assert json.loads(bodies[2])=={'sum':83+29,'difference':83-29}
assert json.loads(bodies[3])=={'product':83*29} and bodies[4]=='amber-047'
longtext=(W/'cli-gpu-long-prompt.txt').read_text();entries=re.findall(r'^Entry (\d+): the assigned code is (amber-\d+), and the shelf is (\d+)\.$',longtext,re.M)
assert len(entries)==80 and [int(e[0])for e in entries]==list(range(1,81)) and entries[46][1]==bodies[4]
expected=[('arithmetic78-first',43,0,41),('arithmetic78-repeat',43,0,41),('prompted-json-sum-difference',25,0,12),('prompted-json-product-followup',18,37,7),('attached-text-retrieval',1557,0,3)]
requests=[];last=-1;warmup=[]
for i,(m,spec)in enumerate(zip(turns,expected)):
 label,prefill,p0,generated=spec;batch=[r for r in rows if last<r['offset']<m.start()]
 if i==0:
  warmup=[batch.pop(0)];assert warmup[0]['nt']==1 and warmup[0]['p0']==0
 assert batch[0]['p0']==p0
 for a,b in zip(batch,batch[1:]):assert b['p0']==a['p0']+a['nt']
 pref=[r for r in batch if r['nt']>1];dec=[r for r in batch if r['nt']==1]
 assert sum(r['nt']for r in pref)==prefill and len(dec)==generated==int(m[1])
 assert batch==pref+dec
 assert abs(generated/(int(m[3])/1000)-float(m[4]))<.1 and m[6]=='eos'
 requests.append({'label':label,'passed':True,'validation_scope':'Observed output checked against parent-recorded expectations; CLI transcript does not echo every input line. JSON requests are prompting, not grammar mode.','output':bodies[i],'output_sha256':hashlib.sha256(bodies[i].encode()).hexdigest(),'prefill_tokens_forwarded':prefill,'prefix_tokens_retained':p0,'generated_tokens':generated,'decode_calls':len(dec),'prefill_microbatches':len(pref),'decode_statistics':{k:summary(dec,k)for k in ['build_ms','inputs_ms','compute_ms']},'cli_metrics':{'prefill_ms':int(m[2]),'decode_ms':int(m[3]),'reported_tokens_per_second':float(m[4]),'reported_ttft_ms':int(m[5]),'finish_reason':m[6],'kv_plan':m[7]},'microbatches':batch})
 last=m.end()
assert not [r for r in rows if r['offset']>last]
# Cold control proves local client-page state; it does not clear remote NFS caches.
fs=[json.loads(s)for s in (W/'engram-io-filesystem.jsonl').read_text().splitlines()];assert len(fs)==24
assert all(r['exact_output'] and r['bytes']==67108864 and r['dequantization']=='synthetic_int16_div127' for r in fs)
assert len({(r['tokens'],r['warm'],r['sample'],r['order'])for r in fs})==24
cold=[r for r in fs if not r['warm']];assert len(cold)==18
assert all(r['cold_pages_confirmed'] and r['initial_selected_resident_pages']==0 and r['initial_resident_pages']==0 for r in cold)
filesystem=[]
for nt in [1,3,256]:
 group=[r for r in fs if r['tokens']==nt];assert len(group)==8
 for warm in [False,True]:
  g=[r for r in group if r['warm']==warm]
  assert len(g)==(2 if warm else 6)
  assert all(r['threads']==(1 if (r['sample']+r['order'])%2==0 else 16)for r in g)
 if nt<4:assert all(r['head_dim']==256 and r['selected_rows']==24*nt for r in group)
 else:assert all(r['head_dim']==32 and r['selected_rows']==1024 for r in group)
 stats={str(t):summary([r for r in group if r['threads']==t and not r['warm']],'lookup_seconds')for t in [1,16]}
 filesystem.append({'tokens':nt,'cold_statistics_seconds':stats,'median_serial_over_parallel':stats['1']['median']/stats['16']['median'],'warmed_controls':[r for r in group if r['warm']]})
warm=[json.loads(s)for s in (W/'engram-io-warm-memory.jsonl').read_text().splitlines()];assert len(warm)==4
for r in warm:
 assert r['exact_output'] and len(r['samples_us'])==12
 assert abs(statistics.median(r['samples_us'])-r['median_us'])<.000002
# All original data copied byte for byte, or deterministically compressed.
files=['user-after-perf2.log','native-decode-build.log','deployed-sha256.txt','engram-io-test.log','engram-io-filesystem.jsonl','engram-io-warm-memory.jsonl','cli-gpu-provenance.json','cli-gpu-long-prompt.txt']
for name in files:copy(W/name,P/name)
(P/'user-after-perf2-clean.txt').write_text(clean)
gpu=W/'user-after-gpu.csv';compressed=P/'user-after-gpu.csv.gz';compressed.write_bytes(gzip.compress(gpu.read_bytes(),mtime=0));assert gzip.decompress(compressed.read_bytes())==gpu.read_bytes()
gpurows=list(csv.reader(gpu.read_text().splitlines()));assert all(len(r)==7 for r in gpurows)and{int(r[1])for r in gpurows}==set(range(8))
copy(Path(__file__),O/'audit-cli-gpu-phase1.py')
report={'scope':'Phase 1 only: deployed native 1d920988…; original PERF3 and updated PERF2 timing are diagnostic, not an isolated speed comparison. No inference or VM job was run by the auditor.','audit_complete':True,'cli_cases_passed':5,'cli_cases_total':5,'native_before_sha256':provenance['native_before_sha256'],'native_after_sha256':provenance['native_after_sha256'],'deployed_hashes':hashes,'source':source(Path(__file__)),'provenance_source':source(W/'cli-gpu-provenance.json'),'before_deterministic_body_exactly_matches_both_after_bodies':True,'warmup':warmup,'requests':requests,'filesystem_cases':24,'cold_cases':18,'all_cold_selected_pages_initially_nonresident':True,'filesystem_controls':filesystem,'warm_memory_controls':warm,'gpu_telemetry':{'raw_source':source(gpu),'compressed_source':source(compressed),'rows':len(gpurows),'scope':'Whole-capture samples; no PID column, no isolated request or exclusive performance qualification.'},'raw_sources':[source(W/name)for name in files],'operator_note':'Parent reports an overlong unsent PTY input was cleared with Ctrl-U before /text; five completed model requests are present. No failed model request is inferred from the blank CLI prompt.','limits':['After PERF2 has no per-node transitions; selected CUDA devices/offload diagnostics are verified, while the before PERF3 trace establishes original graph assignment.','CLI f32 label is not the actual native F16 key-cache allocation.','Filesystem control uses its own 64 MiB scratch file, fixed row selection and synthetic int16 dequantization; mincore verifies local client pages, not NFS server cache state.','Cached-memory and warmed-file overhead remain, as do all single-sample warmup costs. No whole-model universal speedup claim.','The subsequent uninstrumented run or later Engram changes are separate evidence.']}
(P/'audit.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'microbatches':len(rows),'passed_cli_cases':len(requests),'decode_medians':[(r['label'],r['decode_statistics']['inputs_ms']['median'],r['decode_statistics']['compute_ms']['median'])for r in requests],'filesystem_cases':24,'cold_cases':18,'phase1_native':provenance['native_after_sha256']},indent=2))
