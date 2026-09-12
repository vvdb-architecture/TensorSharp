#!/usr/bin/env python3
import collections, hashlib, json, re
from pathlib import Path
from datetime import datetime
ROOT=Path('/tmp/deepseek41-reference')
def dt(x):return datetime.fromisoformat(re.sub(r"\.(\d+)", lambda m: "." + m[1][:6].ljust(6,"0"), x))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def load_analysis(p, report_path):
 summary=json.loads((p/'summary.json').read_text())
 if summary.get('analysis_complete') is not True or summary.get('window_integrity_passed') is not True:
  raise ValueError('Refusing incomplete or failed trace analysis')
 def pinned(path, item):
  if path.stat().st_size!=item['bytes'] or sha(path)!=item['sha256']:raise ValueError('Analysis input/export changed: '+str(path))
 pinned(report_path,summary['inputs']['requests'])
 exports={Path(x['path']).name:x for x in summary['outputs']}
 for name in ['samples.jsonl','stacks.json','runtime-events.jsonl','suspensions.json']:
  if name not in exports:raise ValueError('Missing bound export: '+name)
  pinned(p/name,exports[name])
 return summary,json.loads((p/'stacks.json').read_text()),json.loads(report_path.read_text())
def category(frames):
 s='\n'.join(frames)
 for match,name in [('ResetKVCache','reset_kv'),('ResetHolderForReuse','reset_holder'),('ConvertHolderRingToScratch','ring_to_scratch'),('TSGgml_Qwen35ModelVerify','native_verify_boundary'),('TSGgml_Qwen35ArenaDecode','native_batched_decode_boundary'),('TSGgml_Qwen35ModelDecode','native_decode_boundary'),('GrammarConstraint','grammar'),('GrammarMatcher','grammar'),('TokenSampler','sampler'),('TaskAwaiter','worker_wait')]:
  if match in s:return name
 return 'other_managed'
def main():
 result={'scope':'Temporal samples at the managed engine thread; category priority is exact listed source matching. Samples are not on-CPU time. Native boundary residence includes waits and cannot resolve native frames.','source_sha256':sha(Path(__file__)),'runs':{}}
 for v in ['baseline','final']:
  p=ROOT/f'qwen35-eventpipe-analysis-{v}-r3';summary,stacks,report=load_analysis(p,ROOT/'qwen35-eventpipe-final-native-r1'/f'qwen35-{v}-managed_final-native.json');cases={c['tag']:c for c in report['cases']}
  engine=[json.loads(x) for x in (p/'samples.jsonl').open() if x.strip()];engine=[s for s in engine if stacks[s['stack_id']]['engine_worker_loop']]
  run={'summary_sha256':sha(p/'summary.json'),'full_window_counts':dict(collections.Counter(category(stacks[s['stack_id']]['frames_leaf_to_root']) for s in engine)),'solo':[],'full_window_engine_stack_counts':dict(collections.Counter(s['stack_id'] for s in engine))}
  for req in summary['request_windows']:
   if req['Concurrency']!=1:continue
   tag=req['Tag'];samples=[s for s in engine if any(r['tag']==tag and r['phase']=='before_first_token' for r in s['requests'])];m=cases[tag]['turns'][0]['metrics'];counts=collections.Counter(category(stacks[s['stack_id']]['frames_leaf_to_root']) for s in samples);by_stack=collections.Counter(s['stack_id'] for s in samples);start,first=dt(req['Start']),dt(req['First']);gc=[]
   for ev in summary['gc_reason_suspensions']:
    a,b=dt(ev['Start']),dt(ev['End']); overlap=max(0,(min(first,b)-max(start,a)).total_seconds()*1000)
    if overlap:gc.append(dict(ev,overlap_first_token_ms=overlap))
   other=[]
   for line in (p/'runtime-events.jsonl').open():
    ev=json.loads(line)
    if start<=dt(ev['utc'])<=first and ('Contention' in ev['name'] or 'Thread' in ev['name']):other.append(ev)
   groups=[]
   for s in samples:
    cat=category(stacks[s['stack_id']]['frames_leaf_to_root']); at=dt(s['utc']);off=(at-start).total_seconds()*1000
    if groups and groups[-1]['category']==cat:groups[-1]['last_offset_ms']=off;groups[-1]['samples']+=1
    else:groups.append({'category':cat,'first_offset_ms':off,'last_offset_ms':off,'samples':1})
   run['solo'].append({'tag':tag,'ttft_ms':m['ttft_ms'],'start_utc':req['Start'],'first_utc':req['First'],'engine_samples':len(samples),'categories':dict(counts),'stacks':[dict(stacks[k],samples=n) for k,n in by_stack.most_common()],'sample_runs':groups,'gc_reason_spans':gc,'thread_contention_events':other})
  result['runs'][v]=run
 out=ROOT/'qwen35-eventpipe-engine-comparison.json';out.write_text(json.dumps(result,indent=2)+'\n')
 print(out)
 for v,r in result['runs'].items():
  print(v,r['full_window_counts'])
  for c in r['solo']:
   print(c['tag'],round(c['ttft_ms'],3),c['categories'],'GC',len(c['gc_reason_spans']))
   print('groups',c['sample_runs'])

if __name__=='__main__':main()
