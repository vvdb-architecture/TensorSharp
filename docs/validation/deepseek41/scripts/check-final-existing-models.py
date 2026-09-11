import copy,hashlib,importlib.util,json,pathlib,sys,tempfile,types
from unittest.mock import patch
sys.path.insert(0,'/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison')
source=pathlib.Path('/tmp/deepseek41-reference/run-final-existing-models.py');spec=importlib.util.spec_from_file_location('runner',source);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);checks=[]
class Probe:
 def __enter__(self):return self
 def __exit__(self,*args):pass
 def connect_ex(self,*args):return 1
class Process:
 next_pid=100
 def __init__(self,*args,**kwargs):self.pid=Process.next_pid;Process.next_pid+=1;self.stopped=False
 def poll(self):return 0 if self.stopped else None
 def wait(self,**kwargs):self.stopped=True;return 0

def engine(fail_tag=None):
 def run(url,model,**request):
  prompt=request['messages'][0]['content'];tag=prompt.split(']')[0].split('[validation ')[-1];name=tag.split('-c')[0];details=[]
  if name in ('warmup','short'):content='42'
  elif name=='decode':content='hash collision '*40
  elif name=='json':content='{"name":"Mars","moons":2,"habitable":false}'
  elif name=='json_unicode':content='{"city":"北京","message":"你好，世界","emoji":"🚀"}'
  elif name=='multi_turn':content='OK'if len(request['messages'])==1 else'{"code":"juniper-5938"}'
  elif name=='tool_round_trip':
   if len(request['messages'])==1:content=None;details=[{'id':'call_weather','type':'function','function':{'name':'get_weather','arguments':'{"city":"Paris","units":"celsius"}'}}]
   else:content='{"city":"Paris","temperature_c":19}'
  else:raise AssertionError(name)
  assert 'parallel_tool_calls'not in request['extra_body']
  if fail_tag==tag:content='wrong answer'
  message={'role':'assistant','content':content}
  if details:message['tool_calls']=details
  return {'assistant_message':message,'tool_call_details':details,'finish_reason':'tool_calls'if details else'length'if name=='decode'else'stop','usage_present':True,'prompt_tokens':10,'completion_tokens':512 if name=='decode'else 5,'ttft_ms':1,'prefill_tps':100,'decode_tps':100,'total_wall_ms':3,'decode_timing_source':'stream_window','output_text':content or''}
 return run

with tempfile.TemporaryDirectory()as directory:
 work=pathlib.Path(directory);host=work/'source-host';host.mkdir();native=work/'native.so';native.write_bytes(b'new-native')
 for name in ['TensorSharp.Server.Host.dll','TensorSharp.Runtime.dll','TensorSharp.Models.dll','TensorSharp.Chat.dll','TensorSharp.Server.dll','libGgmlOps.so']:(host/name).write_bytes(name.encode())
 original={p.name:p.read_bytes()for p in host.iterdir()};weights={name:work/(name+'.gguf')for name in m.MODELS}
 for path in weights.values():path.write_bytes(b'fixture weights')
 before=work/'before';before.mkdir();prior=work/'prior';prior.mkdir()
 # Build complete reference responses through the unchanged real portable harness.
 with patch.object(m.validation.engines,'run_openai_chat',side_effect=engine()):
  for name,path in weights.items():
   report={'model':name,'weights_id':m.file_sha(path),'profile':m.PROFILE,'thinking':False,'stream':True,'sampling':m.validation.SAMPLING,'cases':[],'waves':[],'run_complete':False}
   with patch('builtins.print'):m.execute_suite('http://unused',name,name,'quality',report,before/(name+'-tensorsharp.json'),work/'NO_STOP')
   (prior/(name+'-tensorsharp.json')).write_bytes((before/(name+'-tensorsharp.json')).read_bytes())
 for mode in ('all-success','introduced-failure','preexisting-stop'):
  label='test-'+mode;stop_file=work/'STOP_TEST'
  if mode=='preexisting-stop':stop_file.touch()
  processes=[]
  def launch(*args,**kwargs):
   process=Process();processes.append(process);return process
  def kill(pid,*args):
   for process in processes:
    if process.pid==pid:process.stopped=True
  argv=[str(source),'--source-host-dir',str(host),'--native',str(native),'--label',label,'--before-dir',str(before),'--prior-after-dir',str(prior),'--stop-file',str(stop_file)]
  with patch.object(m,'WORK',work),patch.object(m,'MODELS',weights),patch.object(m,'NATIVE_SHA',m.file_sha(native)),patch.object(m,'snapshot',return_value={'simulated':True}),patch.object(m.socket,'socket',Probe),patch.object(m.subprocess,'Popen',side_effect=launch),patch.object(m.os,'killpg',side_effect=kill),patch.object(m.requests,'get',return_value=types.SimpleNamespace(status_code=200,json=lambda:{'data':[{'id':'fixture'}]})),patch.object(m.validation.engines,'run_openai_chat',side_effect=engine('json-c4-r0-i0'if mode=='introduced-failure'else None)),patch.object(sys,'argv',argv),patch('builtins.print'):
   code=m.main()
  result=json.load(open(work/'existing-regressions'/label/'run.json'))
  assert all(p.stopped for p in processes)
  assert {p.name:p.read_bytes()for p in host.iterdir()}==original
  if mode=='preexisting-stop':assert code==1 and not result['run_complete']and not processes
  else:
   assert result['run_complete'] and len(result['runs'])==3
   assert sum(x['suites']['quality']['cases']for x in result['runs'])==75
   assert sum(x['suites']['unicode']['cases']for x in result['runs'])==15
   assert code==int(mode=='introduced-failure')
   expected=int(mode=='introduced-failure')
   assert all(count==expected for refs in result['introduced_failed_cases'].values()for count in refs.values())
   assert all(c['matching_request_hashes']==25 for x in result['runs']for c in x['comparisons'].values())
  checks.append({'name':mode,'passed':True,'run_complete':result['run_complete'],'all_cases_passed':result['all_cases_passed'],'owned_servers':len(processes),'introduced_failed_cases':result['introduced_failed_cases']})
 reference=json.load(open(before/'qwen3-tensorsharp.json'));duplicate=copy.deepcopy(reference);duplicate['cases'][1]=copy.deepcopy(duplicate['cases'][0]);assert not m.compare(reference,duplicate)['comparable'];checks.append({'name':'duplicate-case-rejected','passed':True})
 missing=copy.deepcopy(reference);missing['cases'].pop();assert not m.compare(reference,missing)['comparable'];checks.append({'name':'partial-case-rejected','passed':True})
 target=work/'regression-test-all-success/TensorSharp.Server.Host/bin';deployment=json.load(open(work/'regression-test-all-success/deployment.json'));(target/'TensorSharp.Runtime.dll').write_bytes(b'modified');assert m.unchanged(target,deployment)==['TensorSharp.Runtime.dll'];checks.append({'name':'frozen-deployment-mutation-detected','passed':True})
 try:m.freeze(host,native,work/'regression-test-all-success')
 except FileExistsError:checks.append({'name':'existing-deployment-never-overwritten','passed':True})
 else:raise AssertionError('overwrote deployment')
pathlib.Path('/tmp/deepseek41-reference/final-existing-models-checks.json').write_text(json.dumps({'scope':'Local simulated HTTP/process orchestration using actual portable case validation; no servers, VM jobs, real model inference or downloads.','checks':checks,'runner_sha256':m.file_sha(source),'test_sha256':m.file_sha(pathlib.Path(__file__))},indent=2)+'\n');print(len(checks),'checks passed')
