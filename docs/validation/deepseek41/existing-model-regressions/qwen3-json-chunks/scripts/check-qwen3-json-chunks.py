import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import types
from unittest.mock import patch
sys.path.insert(0, '/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison')
PATH=Path('/tmp/deepseek41-reference/run-qwen3-json-chunks.py')
spec=importlib.util.spec_from_file_location('chunks',PATH);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
RAW=Path('/tmp/deepseek41-reference/final-json-performance-r2')
checks=[]

def record(name): checks.append({'name':name,'passed':True})
def rejected(call):
    try:call()
    except (ValueError,RuntimeError):return
    raise AssertionError('Expected refusal')

for chunk,lengths in [(256,[90]),(76,[76,14]),(64,[64,26])]:
    text='\n'.join(f'[cb] SOLO step n=1 owner=x work=[r{r}:{phase}@{p}]' for r in range(2) for phase,p in ([('P',0)]+([]if chunk>=90 else [('P',chunk)])+[('D',90),('D',91)]))
    result=m.observe_chunks(text,chunk)
    assert result['complete'] and all(r['observed_prefill_lengths']==lengths for r in result['requests'])
    record('observed-positions-exact-split-'+str(chunk))
bad='\n'.join(f'[cb] SOLO step n=1 owner=x work=[r{r}:{phase}@{p}]'for r in range(2)for phase,p in [('P',0),('D',90),('P',64),('D',91)])
assert not m.observe_chunks(bad,64)['complete'];record('prefill-after-decode-counterexample-rejected')
assert not m.observe_chunks('',64)['complete'];record('empty-trace-is-incomplete')
assert not m.observe_chunks('[cb] SOLO step n=1 owner=x work=[r:P@0]\n[cb] SOLO step n=1 owner=x work=[r:D@90]',64)['complete'];record('wrong-shape-not-inferred-from-env')
rejected(lambda:m.observe_chunks('[cb] FUSED step n=2 owner=x work=[r:P@0,s:P@0]',64));record('non-solo-trace-rejected')

class Socket:
    def __enter__(self):return self
    def __exit__(self,*a):pass
    def connect_ex(self,*a):return 1

with tempfile.TemporaryDirectory() as tmp:
    work=Path(tmp);root=json.loads((RAW/'run.json').read_text());children={}
    for version in ('baseline','final'):
        target=work/'frozen'/version/'bin';target.mkdir(parents=True)
        for name in m.r2.BINARY_NAMES:(target/name).write_bytes((version+name).encode())
        (target/'appsettings.json').write_text('{"preserved":true}')
        (target/'logs').mkdir();(target/'logs/old.jsonl').write_text('inherited')
        files={str(p.relative_to(target)):m.common.file_sha(p)for p in target.rglob('*')if p.is_file()}
        root['deployments'][version].update(target=str(target),files_sha256=files,expected_core_binaries={k:files[k]for k in m.r2.BINARY_NAMES})
    for entry in root['runs']:
        name=entry['model']+'-'+entry['version'];child=json.loads((RAW/(name+'.json')).read_text());child['binary_sha256']=root['deployments'][entry['version']]['expected_core_binaries'];path=work/(name+'.json');m.write(path,child);entry['report']=m.source(path);children[(entry['model'],entry['version'])]=child
    prior=work/'r2-run.json';m.write(prior,root)
    unchanged={str(p):p.read_bytes()for p in(work/'frozen').rglob('*')if p.is_file()}
    weights=children[('qwen3','baseline')]['weights']
    with patch.object(m,'PRIOR_SHA',m.common.file_sha(prior)),patch.object(m.common,'identity',return_value=weights):
        _,_,request,_,harness=m.load_inputs(prior)
        assert m.digest(request)==m.REQUEST_SHA;record('pinned-r2-exact-request-and-frozen-files-accepted')
        for name in ('TensorSharp.Runtime.dll','appsettings.json','logs/old.jsonl'):
            p=Path(root['deployments']['final']['target'])/name;old=p.read_bytes();p.write_bytes(old+b'x');rejected(lambda:m.load_inputs(prior));p.write_bytes(old);record('tamper-rejected-'+name)
        p=work/'gemma4-final.json';old=p.read_bytes();p.write_bytes(old+b' ');rejected(lambda:m.load_inputs(prior));p.write_bytes(old);record('unmeasured-r2-child-tamper-rejected')
        p=prior;old=p.read_bytes();p.write_bytes(old+b' ');rejected(lambda:m.load_inputs(prior));p.write_bytes(old);record('r2-root-tamper-rejected')
        broken=copy.deepcopy(request);broken['messages'][0]['content']+='x'
        with patch.object(m.validation.engines,'run_openai_chat',side_effect=AssertionError('NoHTTP')):
            rejected(lambda:m.execute_case('url','model',broken,0))
        record('request-mutation-rejected-before-http')
        state={'processes':[],'calls':[],'mode':'normal'}
        class Process:
            def __init__(self,command,**kw):
                self.pid=100+len(state['processes']);self.closed=False;self.command=command;self.env=kw['env'];self.log=Path(kw['stdout'].name);self.count=0
                self.chunk=int(command[command.index('--prefill-chunk-size')+1]);self.version='baseline' if '/baseline/' in command[1] else 'final'
                assert self.env['TS_SCHED_SOLO_PREFILL_CHUNK']==self.env['TS_SCHED_PREFILL_CHUNK']==str(self.chunk)
                assert self.env['TS_SCHED_MAX_BATCHED_TOKENS']=='256' and self.env['TS_SCHED_MAX_RUNNING_SEQS']=='4' and self.env['TS_SCHED_PREFIX_CACHE']=='0' and self.env['TS_CB_DEBUG']=='1'
                assert Path(self.env['TENSORSHARP_LOG_DIR']).is_relative_to(work.resolve())
                state['processes'].append(self);state['active']=self
            def poll(self):return 0 if self.closed else None
        def stop(p):
            if p:
                p.closed=True
                if state['mode']=='cleanup_exception' and len(state['processes'])==1:
                    raise OSError('injected cleanup error after actual stop')
        original_source=m.source
        def checked_source(path):
            if state['mode']=='source_exception' and len(state['processes'])==1 and path.name.endswith('-server.log'):
                raise OSError('injected evidence read failure')
            return original_source(path)
        def engine(url,model,**args):
            p=state['active'];repeat=p.count;p.count+=1
            sent={k:v for k,v in args.items() if k!='timeout_s'};assert sent==request and m.digest(sent)==m.REQUEST_SHA
            state['calls'].append((p.version,p.chunk,repeat))
            with p.log.open('a') as stream:
                for phase,pos in [('P',0)]+([]if p.chunk>=90 else [('P',p.chunk)])+[('D',90),('D',91)]:
                    stream.write(f'[cb] SOLO step n=1 owner=x work=[request{repeat}:{phase}@{pos}]\n')
            metric=copy.deepcopy(children[('qwen3','final')]['cases'][3]['turns'][0]['metrics'])
            if state['mode']=='missing_usage':metric['usage_present']=False
            return metric
        modes=['normal','missing_usage','execution_exception','cleanup_exception','source_exception']
        for mode in modes:
            state.update(processes=[],calls=[],mode=mode)
            argv=[str(PATH),'--prior-run',str(prior),'--label',mode]
            call=m.execute_case
            def do_case(*args):
                if mode=='execution_exception' and len(state['processes'])==1:raise OSError('injected after readiness')
                return call(*args)
            with patch.object(m.common,'WORK',work),patch.object(m,'source',side_effect=checked_source),patch.object(m.subprocess,'Popen',Process),patch.object(m.common,'stop',side_effect=stop),patch.object(m.common,'snapshot',return_value={'test':True}),patch.object(m.r2,'gpu_clients',return_value=[]),patch.object(m.socket,'socket',Socket),patch.object(m.common.requests,'get',return_value=types.SimpleNamespace(status_code=200,json=lambda:{'data':[{'id':'Qwen3-0.6B-Q8_0'}]})),patch.object(m.validation.engines,'run_openai_chat',side_effect=engine),patch.object(m,'execute_case',side_effect=do_case),patch.object(m.r2,'freeze_version',side_effect=AssertionError('NoCopies')),patch.object(sys,'argv',argv),patch('builtins.print'):
                code=m.main()
            result=json.loads((work/'existing-regressions'/mode/'run.json').read_text())
            assert [(p.version,p.chunk)for p in state['processes']]==[(j['version'],j['chunk'])for j in m.plan()], [(r['job'], json.loads(Path(r['source']['path']).read_text()).get('error')) for r in result['runs']]
            assert all(p.closed for p in state['processes']) and len(result['runs'])==6
            assert {str(p):p.read_bytes()for p in(work/'frozen').rglob('*')if p.is_file()}==unchanged
            assert not result['all_cases_passed'] # retained nestedMars failure is never converted to success
            if mode=='execution_exception':assert code==1 and not result['run_complete'] and len(state['calls'])==10
            elif mode in ('cleanup_exception','source_exception'):
                assert code==1 and not result['run_complete'] and len(state['calls'])==12
                first=json.loads(Path(result['runs'][0]['source']['path']).read_text())
                assert first['finalization_errors'] and len(first['cases'])==2
                assert not(work/'existing-regressions'/mode/'active-server.json').exists()
            else:assert code==0 and result['run_complete'] and len(state['calls'])==12
            record(mode+'-six-fresh-processes-all-owned-stopped-failures-retained')
            with patch.object(m.common,'WORK',work),patch.object(sys,'argv',argv),patch.object(m.subprocess,'Popen',side_effect=AssertionError('NoLaunch')),patch('sys.stderr'):
                try:m.main()
                except SystemExit as e:assert e.code==2
                else:raise AssertionError('Overwrotepreviousrun')
        record('existing-output-rejected-before-launch')
        # A cleanup failure before exit keeps the owned marker; a later
        # foreign-client refusal cannot erase or overwrite that marker.
        state.update(processes=[],calls=[],mode='normal')
        out=work/'alive-cleanup';out.mkdir();args=types.SimpleNamespace(stop_file=work/'not-stopped')
        def failed_stop(p):
            if p:raise OSError('injected cleanup failure while still alive')
        with patch.object(m.subprocess,'Popen',Process),patch.object(m.common,'stop',side_effect=failed_stop),patch.object(m.common,'snapshot',return_value={}),patch.object(m.r2,'gpu_clients',return_value=[]),patch.object(m.socket,'socket',Socket),patch.object(m.common.requests,'get',return_value=types.SimpleNamespace(status_code=200,json=lambda:{'data':[{'id':'Qwen3-0.6B-Q8_0'}]})),patch.object(m.validation.engines,'run_openai_chat',side_effect=engine):
            first=m.run_job(args,m.plan()[0],root,request,weights,harness,out)
        assert not first['run_complete'] and not first['owned_process_exit_observed'] and first['finalization_errors']
        marker=(out/'active-server.json').read_bytes()
        with patch.object(m.subprocess,'Popen',side_effect=AssertionError('No new process')),patch.object(m.common,'stop',side_effect=failed_stop),patch.object(m.common,'snapshot',return_value={}),patch.object(m.r2,'gpu_clients',return_value=[{'pid':state['processes'][0].pid}]):
            second=m.run_job(args,m.plan()[1],root,request,weights,harness,out)
        assert not second['run_complete'] and (out/'active-server.json').read_bytes()==marker
        record('failed-live-cleanup-retains-marker-through-later-refusal')
        # Foreign-client and STOP guards refuse process creation.
        args=types.SimpleNamespace(stop_file=work/'STOP')
        for mode in ('foreign','stop'):
            out=work/(mode+'-guard');out.mkdir()
            if mode=='stop':args.stop_file.touch()
            with patch.object(m.r2,'gpu_clients',return_value=[{'pid':99}] if mode=='foreign' else []),patch.object(m.subprocess,'Popen',side_effect=AssertionError('NoLaunch')),patch.object(m.common,'snapshot',return_value={}),patch.object(m.common,'stop'):
                result=m.run_job(args,m.plan()[0],root,request,weights,harness,out)
            assert not result['run_complete'] and not result['cases'] and 'error' in result
            record(mode+'-guard-before-owned-process')

out=Path('/tmp/deepseek41-reference/qwen3-json-chunks-checks.json')
m.write(out,{'scope':'Local simulated HTTP/process controls, actual temporary frozen files, retained exactrequest. No model/VM execution or numeric inference.', 'runner_source':m.source(PATH),'test_source':m.source(Path(__file__)),'checks':checks})
print(len(checks),'checks passed')
