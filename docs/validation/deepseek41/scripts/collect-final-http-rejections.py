import argparse,hashlib,json,pathlib,csv,statistics
p=argparse.ArgumentParser();p.add_argument('profile');p.add_argument('--source',type=pathlib.Path,required=True);p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--qualified-labels',default='');a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
launch=a.source/(a.profile+'-launch.json');provenance=json.loads(launch.read_text()) if launch.exists() else {}
for f in sorted(a.source.glob(a.profile+'-*.json')):
 data=json.loads(f.read_text())
 if 'cases' not in data or any(c.get('status') not in ('ok','fail') or 'turns' not in c for c in data['cases']):
  (a.output/f.name).write_text(json.dumps(data,indent=2,ensure_ascii=False)+'\n');continue
 result={k:v for k,v in data.items() if k not in ('cases','warmup')};result['source_report']='/workspace/deepseek41-work/'+f.name;result['source_report_sha256']=hashlib.sha256(f.read_bytes()).hexdigest();result['native_sha256']=provenance.get('sha256',{}).get('libGgmlOps.so');result['cases']=[]
 for case in data['cases']:
  row={k:v for k,v in case.items() if k!='turns'};row['turns']=[]
  for turn in case['turns']:
   
   if 'request' not in turn or 'metrics' not in turn:
    row['turns'].append(turn);continue
   req=turn['request'];metrics=turn['metrics']
   row['turns'].append({'request_sha256':hashlib.sha256(json.dumps(req,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest(),'message_roles':[m['role'] for m in req.get('messages',[])],'max_tokens':req.get('max_tokens'),'response_format':req.get('response_format'),'extra_body':req.get('extra_body'),'tool_choice':req.get('tool_choice'),'parallel_tool_calls':req.get('parallel_tool_calls'),'metrics':metrics})
  result['cases'].append(row)
 out=a.output/f.name;label=f.stem[len(a.profile)+1:];previous=json.loads(out.read_text()) if out.exists() else {};result['performance_qualified']=label in a.qualified_labels.split(',') or previous.get('performance_qualified',False);out.write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n');print(json.dumps({'file':f.name,'passed':sum(c['status']=='ok' for c in data['cases']),'total':len(data['cases']),'failed':[{'tag':c['tag'],'detail':c.get('detail')} for c in data['cases'] if c['status']!='ok'],'summary':{k:{kk:vv for kk,vv in v.items() if not kk.endswith('_samples')} for k,v in data.get('summary',{}).items()}}))
if provenance:(a.output/launch.name).write_text(json.dumps(provenance,indent=2)+'\n')
