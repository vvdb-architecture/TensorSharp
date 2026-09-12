"""Offline audit of the exact original CLI and PERF3 transcript, with lossless log curation."""
from pathlib import Path
import collections,csv,gzip,hashlib,io,json,re,statistics
W=Path('/tmp/deepseek41-reference');O=Path('/Users/zhongkaifu/work/TensorSharp/docs/validation/deepseek41/cli-gpu-execution')
def source(p):
 b=p.read_bytes();return {'path':str(p),'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()}
def med(rows,key):return {'min':min(r[key] for r in rows),'median':statistics.median(r[key] for r in rows),'max':max(r[key]for r in rows),'sum':sum(r[key]for r in rows)}
def copy(p,q):
 q.parent.mkdir(parents=True,exist_ok=True)
 if q.exists():assert q.read_bytes()==p.read_bytes()
 else:q.write_bytes(p.read_bytes())
raw=W/'user-before-perf.log';text=raw.read_text();clean=re.sub(r'\[dsv4\][^\n]*\n','',text)
assert clean==(W/'user-before-perf-clean.txt').read_text()
pat=r'\[dsv4\] ubatch nt=(\d+) p0=(\d+): build ([\d.]+)ms alloc ([\d.]+)ms inputs ([\d.]+)ms compute ([\d.]+)ms \(nodes (\d+), splits (\d+)(, reused)?\)'
ms=list(re.finditer(pat,text));assert len(ms)==text.count('[dsv4] ubatch')==139
rows=[];backend_patterns={}
for i,m in enumerate(ms):
 values=m.groups();r={'index':i,'nt':int(values[0]),'p0':int(values[1]),'build_ms':float(values[2]),'alloc_ms':float(values[3]),'inputs_ms':float(values[4]),'compute_ms':float(values[5]),'nodes':int(values[6]),'splits':int(values[7]),'reused':bool(values[8])}
 block=text[m.end():ms[i+1].start() if i+1<len(ms) else len(text)]
 ns=re.findall(r'\[dsv4\]\s+node\s+(\d+)\s+(.*?)\s+op=(\S+)\s+->\s+(\S+)',block)
 assert ns and int(ns[0][0])==0
 assert all(int(ns[j][0])<int(ns[j+1][0])for j in range(len(ns)-1))
 assert int(ns[-1][0])<r['nodes']
 counts=collections.Counter()
 for j,n in enumerate(ns):counts[n[3]]+=(int(ns[j+1][0])if j+1<len(ns)else r['nodes'])-int(n[0])
 assert sum(counts.values())==r['nodes']
 r['backend_node_spans']=dict(counts);r['backend_transitions']=len(ns)
 r['cpu_assigned_nodes']=sum(v for k,v in counts.items()if k=='CPU')
 r['other_assigned_nodes']=sum(v for k,v in counts.items()if not re.fullmatch(r'(CUDA|TSDSV4-)[0-7]',k))
 pattern=hashlib.sha256(json.dumps(ns,separators=(',',':')).encode()).hexdigest()
 r['transition_pattern_sha256']=pattern
 backend_patterns.setdefault(pattern,{'first_microbatch':i,'microbatches':[],'transitions':len(ns),'backend_node_spans':dict(counts)})['microbatches'].append(i)
 forwards=re.findall(r'\[dsv4\] forward (\d+) tokens in ([\d.]+)s \(([\d.]+) tok/s\)',block)
 assert len(forwards)==1 and int(forwards[0][0])==r['nt']
 r['forward_seconds']=float(forwards[0][1]);rows.append(r)
assert [(i,r['nt'],r['p0'])for i,r in enumerate(rows)if r['nt']>1]==[(1,43,0),(97,43,0)]
assert rows[0]['nt']==1 and rows[0]['p0']==0
requests=[]
for label,start,end,generated,prompt in [('random-arithmetic42',1,97,95,'What is 17 plus 25? Give a brief answer.'),('deterministic-arithmetic78',97,139,41,'What is 31 plus 47? Give a brief answer.')]:
 batch=rows[start:end];assert batch[0]['nt']==43 and len(batch[1:])==generated
 for a,b in zip(batch,batch[1:]):assert b['p0']==a['p0']+a['nt'] and b['nt']==1
 requests.append({'label':label,'prompt':prompt,'prefill':batch[0],'generated_tokens':generated,'decode_calls':len(batch[1:]),'decode_microbatch_indices':[r['index']for r in batch[1:]],'decode_statistics':{k:med(batch[1:],k)for k in ['build_ms','alloc_ms','inputs_ms','compute_ms','forward_seconds']},'all_backend_names':sorted({k for r in batch for k in r['backend_node_spans']}),'cpu_assigned_nodes':sum(r['cpu_assigned_nodes']for r in batch),'other_assigned_nodes':sum(r['other_assigned_nodes']for r in batch)})
assert requests[0]['decode_statistics']['inputs_ms']['median']==76.56 and requests[0]['decode_statistics']['compute_ms']['median']==33.32
assert requests[1]['decode_statistics']['inputs_ms']['median']==39.64 and requests[1]['decode_statistics']['compute_ms']['median']==33.08
turnpat=r'(\d\d:\d\d:\d\d) info: TensorSharp.Cli\[1501\] interactive.turn complete tokens=(\d+) promptTokens=(\d+) kvPlan=(\S+) prefillMs=(\d+) decodeMs=(\d+) tps=([\d.]+) ttftMs=(\d+) reason=(\S+)'
def turns(s):return [dict(zip(['utc_time','tokens','prompt_tokens','kv_plan','prefill_ms','decode_ms','cli_tps','cli_ttft_ms','finish_reason'],m))for m in re.findall(turnpat,s)]
perfturns=turns(clean);origturns=turns((W/'user-before-cli.log').read_text())
assert len(perfturns)==2 and [int(r['tokens'])for r in perfturns]==[95,41]
assert len(origturns)==1 and origturns[0]['tokens']=='97' and '[answer] 42' in (W/'user-before-cli.log').read_text()
assert '[answer] 42' in clean and '[answer] 78' in clean
assert 'Seed set to 42' in clean and 'temperature updated.' in clean and 'Max tokens per reply set to 128' in clean
# Telemetry has no PID column or precise request-start anchor. Report full-capture
# and CLI-inferred intervals separately; do not invent a qualified kernel timer.
gpu=W/'user-before-gpu.csv';samples=[]
for line in gpu.read_text().splitlines():
 cells=next(csv.reader([line]));assert len(cells)==7
 def number(k):return float(cells[k].strip().split()[0])
 samples.append({'timestamp':cells[0].strip(),'device':int(cells[1]),'gpu_util_percent':number(2),'memory_util_percent':number(3),'memory_mib':number(4),'power_w':number(5),'sm_mhz':number(6)})
assert set(s['device']for s in samples)==set(range(8))
def gpu_summary(group):
 return {str(d):{'samples':len(g:=[s for s in group if s['device']==d]),'nonzero_gpu_samples':sum(s['gpu_util_percent']>0 for s in g),'metrics':{k:med(g,k)for k in ['gpu_util_percent','memory_util_percent','memory_mib','power_w','sm_mhz']}}for d in range(8)}
telemetry={'source':source(gpu),'first_timestamp':samples[0]['timestamp'],'last_timestamp':samples[-1]['timestamp'],'rows':len(samples),'whole_capture':gpu_summary(samples),'limitations':'No compute PID column; capture spans idle/load and multiple requests. Whole-capture medians are not request medians or proof of exclusive execution. Request timing cannot be aligned exactly from these CLI second-resolution completion logs.'}
(O/'before').mkdir(parents=True,exist_ok=True)
archives=[]
for name in ['user-before-perf.log','user-before-gpu.csv']:
 p=W/name;q=O/'before'/(name+'.gz');compressed=gzip.compress(p.read_bytes(),mtime=0)
 if q.exists():assert q.read_bytes()==compressed
 else:q.write_bytes(compressed)
 assert gzip.decompress(q.read_bytes())==p.read_bytes()
 archives.append({'raw':source(p),'compressed':source(q),'decompression_matches_raw':True})
for name in ['user-before-cli.log','user-before-perf-clean.txt']:copy(W/name,O/'before'/name)
copy(Path(__file__),O/'audit-cli-gpu-before.py')
with (O/'before/microbatches.csv').open('w')as f:
 writer=csv.DictWriter(f,fieldnames=[k for k in rows[0]if k not in ['backend_node_spans']]);writer.writeheader()
 for r in rows:writer.writerow({k:v for k,v in r.items()if k!='backend_node_spans'})
report={'scope':'Original user deployment CLI and PERF3 diagnostic only; no before/after performance conclusion. Zero CPU graph spans does not exclude host Engram/input work.',
 'audit_complete':True,'raw_source':source(raw),'analysis_source':source(Path(__file__)),'clean_derivation':r're.sub(r"\[dsv4\][^\n]*\n", "", raw_text); token prefixes before diagnostics preserved',
 'original_uninstrumented_turns':origturns,'perf3_turns':perfturns,'warmup':rows[0],'requests':requests,'microbatches':rows,'backend_patterns':backend_patterns,
 'telemetry':telemetry,'archives':archives,'limitations':['CLI timings include stdout and PERF3 transition-dump overhead; the compute stopwatch ends before that dump. PERF2 and PERF3 total times are not an isolated speed comparison.','inputs includes lookup, waits, mask preparation and uploads; compute includes synchronized graph execution, not exclusive GPU kernel time.','Transition spans show backend assignment; a printed op describes only the first node of each span.','CLI logs report kvCacheDtype=f32, but the native executor allocates raw/compressed/index key caches as F16 in the source.','Exact binary/build/launch identity artifacts are pending; parent-reported original native c68d is not substituted for historical 6b3 benchmark identities.']}
(O/'before/audit.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'microbatches':len(rows),'requests':[(r['label'],r['decode_calls'],r['decode_statistics']['inputs_ms']['median'],r['decode_statistics']['compute_ms']['median'])for r in requests],'cpu_nodes':sum(r['cpu_assigned_nodes']for r in rows),'other_nodes':sum(r['other_assigned_nodes']for r in rows),'telemetry_rows':len(samples),'compressed_bytes':sum(x['compressed']['bytes']for x in archives)},indent=2))
