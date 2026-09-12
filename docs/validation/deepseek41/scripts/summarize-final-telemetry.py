import argparse, csv, hashlib, json, pathlib, re, statistics
p=argparse.ArgumentParser();p.add_argument('profile');p.add_argument('--source',type=pathlib.Path,required=True);a=p.parse_args()
for f in sorted(a.source.glob(a.profile+'-*-gpu.csv')):
 with f.open() as stream:
  rows=[{k.strip():v.strip() for k,v in row.items() if k is not None} for row in csv.DictReader(stream)]
 if not rows: continue
 result={'profile':a.profile,'source_csv':'/workspace/deepseek41-work/'+f.name,'source_sha256':hashlib.sha256(f.read_bytes()).hexdigest(),'sample_rows':len(rows),'first_timestamp':rows[0].get('timestamp'),'last_timestamp':rows[-1].get('timestamp'),'gpus':{}}
 for gpu in sorted({row['index'] for row in rows},key=int):
  group=[row for row in rows if row['index']==gpu]; data={'samples':len(group)}
  for field in group[0]:
   if field in ('timestamp','index'): continue
   numbers=[]
   for row in group:
    match=re.match(r'[-+]?\d+(?:\.\d+)?',row.get(field,''))
    if match: numbers.append(float(match[0]))
   if numbers:data[field]={'min':min(numbers),'median':statistics.median(numbers),'max':max(numbers)}
  result['gpus'][gpu]=data
 output=f.with_name(f.stem.removesuffix('-gpu')+'-telemetry.json'); output.write_text(json.dumps(result,indent=2)+'\n');print(output.name,len(rows))
