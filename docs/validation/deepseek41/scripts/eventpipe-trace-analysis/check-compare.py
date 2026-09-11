import copy, importlib.util, json, tempfile
from pathlib import Path
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('compare',HERE/'compare.py');c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
checks=[]
with tempfile.TemporaryDirectory(prefix='trace-comparison-guards-') as t:
 p=Path(t);r=p/'report.json';r.write_text('{}');export_names=['samples.jsonl','stacks.json','runtime-events.jsonl','suspensions.json']
 def source(f):return dict(path=str(f),bytes=f.stat().st_size,sha256=c.sha(f))
 for n in export_names:(p/n).write_text('[]' if n.endswith('.json') else '')
 pristine=dict(analysis_complete=True,window_integrity_passed=True,inputs=dict(requests=source(r)),outputs=[source(p/n) for n in export_names])
 def run(name,change,fail=True):
  s=copy.deepcopy(pristine);change(s);(p/'summary.json').write_text(json.dumps(s))
  try:c.load_analysis(p,r);ok=not fail
  except (ValueError,FileNotFoundError):ok=fail
  if not ok:raise AssertionError(name)
  checks.append(name)
 run('valid integrity and bound exports accepted',lambda s:None,False)
 run('incomplete analysis rejected',lambda s:s.update(analysis_complete=False))
 run('failed window integrity rejected',lambda s:s.update(window_integrity_passed=False))
 run('HTTP report mismatch rejected',lambda s:s['inputs']['requests'].update(sha256='0'*64))
 for n in export_names:
  run('substituted '+n+' rejected',lambda s,n=n:next(x for x in s['outputs'] if Path(x['path']).name==n).update(sha256='0'*64))
 run('missing export rejected',lambda s:s['outputs'].pop())
for name in ['baseline','final']:
 c.load_analysis(HERE.parent/f'qwen35-eventpipe-analysis-{name}-r3',HERE.parent/'qwen35-eventpipe-final-native-r1'/f'qwen35-{name}-managed_final-native.json');checks.append(name+' real bound exports and HTTP report accepted')
output=dict(passed=len(checks),checks=checks,comparison_sha256=c.sha(HERE/'compare.py'),guard_sha256=c.sha(Path(__file__)))
(HERE/'comparison-checks.json').write_text(json.dumps(output,indent=2)+'\n');print(json.dumps(output))
