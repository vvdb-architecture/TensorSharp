"""Local evidence curation only; no compilation, inference or network activity."""
from pathlib import Path
import hashlib,json,re,statistics,subprocess
R=Path('/Users/zhongkaifu/work/TensorSharp')
O=R/'docs/validation/deepseek41/cli-gpu-execution'
B=Path('/tmp/dsv41-backend-diagnostics')
F=Path('/tmp/deepseek41-reference/engram-decode-fanout')
def read(p):return json.loads(p.read_text())
def source(p):
 data=p.read_bytes();return {'path':str(p),'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()}
def copy(p,q):
 q.parent.mkdir(parents=True,exist_ok=True)
 if q.exists():assert q.read_bytes()==p.read_bytes(),q
 else:q.write_bytes(p.read_bytes())
 return source(q)
# Check the producer's local backend manifest without substituting current files.
for name,expected in read(B/'manifest.json')['files'].items():
 actual=source(B/name)
 assert all(actual[k]==expected[k] for k in ['bytes','sha256'])
 copy(B/name,O/'local-backend'/name)
copy(B/'manifest.json',O/'local-backend/manifest.json')
b=read(B/'summary.json');checks=b['checks']
for name,accepted in [('old-cuda-fallback',True),('new-cuda-fallback',False),('new-metal-explicit',False),('new-cpu-explicit',True)]:
 d=read(B/(name+'.json'));assert d==checks[name] and d['passed'] and d['accepted']==accepted
oracle=[]
for name in ['cpu-stable-oracle','cpu-oracle-before','cpu-oracle','cpu-f32-oracle']:
 d=read(B/(name+'.json'));c=d['checks'];assert len(c)==41 and len({x['name'] for x in c})==41
 oracle.append({'name':name,'passed':sum(x['passed'] for x in c),'total':len(c),'failed_checks':[x for x in c if not x['passed']],'source':source(B/(name+'.json'))})
assert [x['passed'] for x in oracle]==[41,32,32,35]
assert read(B/'cpu-oracle-before.json')==read(B/'cpu-oracle.json')
# Exact current source pins for the subsequent fanout stage.
f=read(F/'report.json');assert f['timing_qualified_for_vm'] is False
for path,sha in f['source_sha256'].items():
 p=R/path;assert source(p)['sha256']==sha
 copy(p,O/'sources'/Path(path).relative_to('TensorSharp.GGML.Native'))
assert f['source_sha256']['TensorSharp.GGML.Native/ggml_ops_deepseek4.cpp']=='24246c67729630b9e6c95c8f660068d06f0b53f8b523e7c935c1e5c0e5d6609d'
for i,c in enumerate(f['commands']):
 assert c['exit_code']==0 and c['log']==f'{i:02}.log'
 copy(F/c['log'],O/'local-engram-fanout'/c['log'])
copy(F/'report.json',O/'local-engram-fanout/report.json')
blocked=[]
for name in ['01.log','05.log']:
 text=(F/name).read_text()
 assert 'Engram 1/3/257-token serial/parallel/warm, invalid rows, lifecycle, concurrent submission and exception tests passed' in text
 rows=re.findall(r'Simulated blocked reads: rows=(\d+) threads=(\d+) peak=(\d+) elapsed_ms=([\d.]+)',text)
 assert len(rows)==4
 for n,t,peak,elapsed in rows:
  n,t,peak=int(n),int(t),int(peak);assert n in [24,72] and t in [1,16] and 1<=peak<=t
  assert (peak==1 if t==1 else peak>1)
  blocked.append({'log':name,'rows':n,'threads':t,'peak':peak,'elapsed_ms':float(elapsed),'scope':'Artificial blocked reads; overlap/bound is the assertion, not a performance threshold.'})
warmed=[json.loads(line) for line in (F/'03.log').read_text().splitlines()]
assert {(r['tokens'],r['threads']) for r in warmed}=={(1,1),(1,16),(3,1),(3,16)}
for r in warmed:
 assert r['exact_output'] and r['head_dim']==256 and r['selected_rows']==r['tokens']*24
 assert len(r['samples_us'])==12 and r['repetitions_per_sample']==200
 assert abs(statistics.median(r['samples_us'])-r['median_us'])<0.000002
copy(Path('/tmp/deepseek41-reference/cli-gpu-audit-plan.md'),O/'audit-plan.md')
copy(Path(__file__),O/'curate-cli-gpu-local.py')
patch=subprocess.check_output(['git','diff','--','TensorSharp.GGML.Native/ggml_ops_deepseek4.cpp','TensorSharp.GGML.Native/tests/dsv41_engram_io_test.cpp','TensorSharp.GGML.Native/tests/dsv41_engram_io_bench.cpp'],cwd=R)
(O/'sources/fanout-and-diagnostics.diff').write_bytes(patch)
summary={'scope':'Independent local artifact audit; before/after full-checkpoint VM CLI evidence is pending. No model, benchmark, build or remote command was run by this curator.',
 'local_artifact_audit_complete':True,'vm_before_after_audited':False,
 'user_deployment':{'path':'/root/TensorSharp/TensorSharp.Cli/bin','reported_original_native_sha256':'c68df1f3d0c70269be249c12a9eae9e7e6ae9144e232dda311d4e853c2d048d0','identity_scope':'Supplied by parent; raw VM identity report pending.'},
 'backend_diagnostics_source':b['source'],'backend_checks':{k:checks[k] for k in ['old-cuda-fallback','new-cuda-fallback','new-metal-explicit','new-cpu-explicit']},
 'oracle_results':oracle,'cuda_index_before_after_reports_exactly_equal':True,
 'fanout_source_sha256':f['source_sha256'],'fanout_commands_all_exit_zero':True,
 'simulated_blocked_rows':blocked,'warm_memory_control':warmed,
 'limitations':['Backend tests precede the Engram fanout source change; their full-model oracle results do not validate the later combined source.','Cached-memory dispatch is slower with 16 workers here; artificial blocking demonstrates overlap only. No universal performance or VM speedup claim.','Historical model validation native 6b3 and the user original c68d native remain distinct from this local source/test stage.','Original local setup and strict numerical failures are retained.'],
 'curator_source':source(Path(__file__))}
(O/'local-audit.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps({'local_artifact_audit_complete':True,'backend_controls':4,'oracle_counts':[x['passed'] for x in oracle],'fanout_command_exits':len(f['commands']),'warm_control_rows':len(warmed),'vm_before_after_audited':False},indent=2))
