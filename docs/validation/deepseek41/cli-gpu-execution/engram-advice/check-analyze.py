import copy
import importlib.util
import json
from pathlib import Path
p = Path(__file__).parent
spec=importlib.util.spec_from_file_location('analysis',p/'analyze.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
rows=[]
for nt in (1,3):
 for s in range(3):
  for to in range(2):
   th=16 if (s+to)%2 else 1
   for ao in range(2):
    advice='random' if (s+ao)%2 else 'default'
    rows.append(dict(mode='scratch_advice',tokens=nt,threads=th,sample=s,thread_order=to,advice_order=ao,advice=advice,
     head_dim=256,row_bytes=512,selected_rows=nt*24,bytes=64*1024*1024,page_bytes=4096,total_pages=16384,selected_pages=nt*24,
     initial_resident_pages=0,initial_selected_resident_pages=0,final_resident_pages=nt*24,final_selected_resident_pages=nt*24,
     cold_pages_confirmed=True,minor_faults_delta=1,major_faults_delta=1,lookup_seconds=.01,
     dequantization='synthetic_int16_div127',hashes_fnv1a64=f'{nt:016x}',exact_output=True))
checks=[]
def passed(name,fn):fn();checks.append(name)
def reject(name,change):
 values=copy.deepcopy(rows);change(values)
 try:module.summarize(values)
 except (ValueError,KeyError):checks.append(name);return
 raise AssertionError(name)
passed('complete24',lambda:module.summarize(rows))
old_order=copy.deepcopy(rows)
for row in old_order: row['advice_order'] = (row['advice']=='random') ^ (row['threads']==16)
try: module.summarize(old_order)
except ValueError: checks.append('oldfixedadviceorderrejected')
else: raise AssertionError('Old fixed advice order was accepted')
reject('missingtrial',lambda x:x.pop())
reject('duplicate',lambda x:x.__setitem__(0,x[1]))
reject('changedrowfingerprint',lambda x:x[1].__setitem__('hashes_fnv1a64','1234567890abcdef'))
reject('nonfinite',lambda x:x[0].__setitem__('lookup_seconds',float('nan')))
reject('wronggeometry',lambda x:x[0].__setitem__('row_bytes',64))
reject('wrongorder',lambda x:x[0].__setitem__('advice_order',1-x[0]['advice_order']))
reject('falsecold',lambda x:x[0].__setitem__('initial_selected_resident_pages',1))
reject('badoutput',lambda x:x[0].__setitem__('exact_output',False))
reject('invalidresidency',lambda x:x[0].__setitem__('final_resident_pages',999999))
reject('negativefaultcount',lambda x:x[0].__setitem__('major_faults_delta',-1))
values=copy.deepcopy(rows);values[0].update(initial_resident_pages=1,initial_selected_resident_pages=1,cold_pages_confirmed=False)
out=module.summarize(values);assert out['groups'][0]['cold_pairs']==2 and out['groups'][0]['pairs'][0]['default_over_random'] is None;checks.append('uncoldtrialretainedunqualified')
(p/'analyzer-checks.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'scope':'Local summarizer guards with simulated records; no Linux scratch run.'},indent=2)+'\n')
print(f'Passed {len(checks)} summarizer guards')
