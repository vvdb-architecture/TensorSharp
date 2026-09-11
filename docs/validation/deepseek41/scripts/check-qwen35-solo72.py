#!/usr/bin/env python3
"""Local identity, coverage, environment and lifecycle guards; no real endpoints."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import runpy
import tempfile
import threading
import types
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
prior = runpy.run_path(str(HERE / 'check-qwen35-cross-phase.py'))
cross,r2 = prior['m'],prior['r2']
spec=importlib.util.spec_from_file_location('solo_guarded',HERE/'run-qwen35-solo72.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
checks=[];state={'hosts':[],'maps':[],'requests':[],'fail':False,'measured_threads':[]}


def check(label,condition):
    assert condition,label
    checks.append(label)


def refused(label,action):
    try:action()
    except(ValueError,FileExistsError):checks.append(label)
    else:raise AssertionError(label)


class Host:
    def __init__(self,command,**kwargs):
        self.pid=50000+len(state['hosts']);self.command=command;self.env=kwargs['env'];self.stopped=False
        self.index=len(state['hosts']);state['hosts'].append(self)
        logdir=Path(self.env['TENSORSHARP_LOG_DIR']);logdir.mkdir(parents=True)
        (logdir/'runtime.jsonl').write_text('{"simulated":true}\n')
    def poll(self):return 0 if self.stopped else None
    def wait(self,**kwargs):self.stopped=True;return 0


def observe(cross,pid,target,expected):
    assert not any(i==len(state['hosts'])-1 for i,tag,request in state['requests'])
    state['maps'].append(pid)
    return {'pid':pid,'library_sha256':expected,'selected_library_paths':[str(target/'libGgmlOps.so')]}


def engine(url,model,**request):
    tag=request['messages'][0]['content'].split(']')[0].split('[validation ')[-1]
    index=len(state['hosts'])-1
    assert state['hosts'][-1].pid in state['maps']
    state['requests'].append((index,tag,copy.deepcopy(request)))
    if tag.startswith('json-c'):
        state['measured_threads'].append(threading.current_thread().name)
    decode=tag=='warmup-decode'
    text='hash collision '*40 if decode else '{"name":"Mars","moons":2,"habitable":false}'
    if state['fail'] and index%4==1 and tag=='json-c1-r1-i0':text='{"name":"Earth","moons":2,"habitable":false}'
    return {'assistant_message':{'role':'assistant','content':text},'finish_reason':'length'if decode else'stop',
        'usage_present':True,'prompt_tokens':92,'completion_tokens':512 if decode else 16,'ttft_ms':10,'decode_tps':100,
        'total_wall_ms':300,'decode_timing_source':'stream_window','t_first_abs':1,'t_last_abs':1.23,'prefill_tps':9200,'output_text':text}


with tempfile.TemporaryDirectory()as directory:
    work=Path(directory);old=work/'r2';old.mkdir()
    original=json.loads((HERE/'final-json-performance-r2/run.json').read_text())
    for version in ('baseline','final'):
        host=work/('source-'+version);host.mkdir()
        for name in r2.BINARY_NAMES:(host/name).write_bytes((version+name).encode())
        (host/'settings.json').write_text('{}')
        d=original['deployments'][version]
        d.update(target=str(host),files_sha256=cross.tree_hashes(host),expected_core_binaries={name:cross.sha(host/name)for name in r2.BINARY_NAMES})
        report=json.loads((HERE/'final-json-performance-r2'/('qwen35-'+version+'.json')).read_text())
        report['binary_sha256']=d['expected_core_binaries']
        r2.write(old/('qwen35-'+version+'.json'),report)
        entry=next(r for r in original['runs']if r['model']=='qwen35'and r['version']==version)
        entry['report']=r2.source(old/('qwen35-'+version+'.json'))
    r2.write(old/'run.json',original)
    (work/'dotnet').mkdir();(work/'dotnet/dotnet').write_text('fake dotnet')
    previous={'run_complete':True,'all_cases_passed':True,'deployments':cross.freeze_cross(r2,original,work/'cross-hosts'),
        'dotnet_executable':r2.source(work/'dotnet/dotnet')}
    crossroot=work/'cross-results';crossroot.mkdir();r2.write(crossroot/'run.json',previous)
    args=types.SimpleNamespace(out=work/'solo-results',r2_manifest=old/'run.json',cross_manifest=crossroot/'run.json',stop_file=work/'stop',exclusive_window=True)
    initial={str(p):cross.sha(p)for p in work.rglob('*')if p.is_file()}
    qualified={'qualified':True,'gpu7':{'clocks.sm':{'median':1740},'clocks.mem':{'median':7251}}}
    noise={'TS_GGML_PHASE_TIMING':'1','DOTNET_EventPipeThreadSamplingRate':'1','COMPlus_PerfMapEnabled':'1','CORECLR_ENABLE_PROFILING':'1','GGML_FA_DEBUG':'1','DOTNET_ROOT':'old','TENSORSHARP_LOG_DIR':'old'}
    with patch.object(cross,'R2_MANIFEST_SHA',cross.sha(old/'run.json')),patch.object(m,'CROSS_MANIFEST_SHA',cross.sha(crossroot/'run.json')),\
        patch.object(r2.common,'identity',return_value=report['weights']),patch.object(r2.common,'WORK',work),\
        patch.object(m.subprocess,'Popen',Host),patch.object(m.socket,'socket',prior['Socket']),patch.object(m,'verify_native_mapping',side_effect=observe),\
        patch.object(r2.validation.engines,'run_openai_chat',side_effect=engine),patch.object(r2,'Telemetry',prior['Monitor']),\
        patch.object(r2,'gpu_clients',return_value=[]),patch.object(r2.common,'snapshot',return_value={'simulated':True}),\
        patch.object(r2.common.requests,'get',return_value=types.SimpleNamespace(status_code=200,json=lambda:{'data':[{'id':'qwen35'}]})),\
        patch.object(r2.common,'stop',side_effect=lambda p:p.wait()if p else None),patch.object(r2,'qualify_telemetry',return_value=qualified),\
        patch.dict(os.environ,noise):
        result=m.execute(cross,r2,args)
        check('all72 pass with both whole18 comparisons',result['run_complete']and result['all_cases_passed']and result['all_pairs_comparable'])
        check('four fresh B/F/F/B processes',len(state['hosts'])==4 and [r['version']for r in result['runs']]==list(m.VERSIONS))
        check('72 measured plus24 exact warmups',len(state['requests'])==96 and sum(t.startswith('json-c')for _,t,_ in state['requests'])==72)
        check('one maps read per process before every warmup',len(state['maps'])==4 and len(set(state['maps']))==4)
        check('all inherited profiler/runtime settings removed',all(not any(k.startswith(('DOTNET_','COMPlus_','CORECLR_'))for k in p.env)and 'TS_GGML_PHASE_TIMING'not in p.env and'GGML_FA_DEBUG'not in p.env for p in state['hosts']))
        check('ordinary R2 profile preserved',all(all(p.env.get(k)==v for k,v in r2.common.ENV.items())for p in state['hosts']))
        check('all72 preserve R2 single-worker dispatch',len(state['measured_threads'])==72 and all(n.startswith('ThreadPoolExecutor')for n in state['measured_threads']))
        check('all processes cleaned up',all(p.stopped for p in state['hosts']))
        check('every previous file byte-identical',all(cross.sha(Path(p))==h for p,h in initial.items()))
        reports=[json.loads(Path(r['source']['path']).read_text())for r in result['runs']]
        for index,r in enumerate(reports):
            rows=[(tag,req)for i,tag,req in state['requests']if i==index]
            check('exact eighteen requests and unique logical IDs job'+str(index),m.complete(r)and [t for t,_ in rows[6:]]==['json-c1-r'+str(i%3)+'-i0'for i in range(18)])
            check('six identical payloads per original tag job'+str(index),all(len({json.dumps(req,sort_keys=True)for tag,req in rows[6:]if tag=='json-c1-r'+str(rep)+'-i0'})==1 for rep in range(3)))
            check('declared18/3/15 arrays retain all values job'+str(index),[r['descriptive_groups'][k]['cases']for k in ('all18','first3','later15')]==[18,3,15])
        check('paired order0/1 and3/2',[(p['baseline_job'],p['final_job'])for p in result['comparisons']]==[(0,1),(3,2)])
        check('every solo wave wall retained',all(len(r['waves'])==18 and all(c['wave_wall_ms']>0 for c in r['cases'])for r in reports))
        changed=copy.deepcopy(reports[0]);changed['cases'].pop()
        check('missing eighteenth case rejected',not m.complete(changed)and not m.compare(r2,changed,reports[1])['whole18_comparable'])
        changed=copy.deepcopy(reports[0]);changed['cases'][4]['logical_id']=changed['cases'][3]['logical_id']
        check('duplicate logical iteration rejected',not m.complete(changed))
        changed=copy.deepcopy(reports[0]);changed['waves'][4]['generated_tokens']+=1
        check('wave token reconciliation required',not m.complete(changed))
        changed=copy.deepcopy(reports[0]);changed['cases'][4]['original_request_matches']=False
        check('changed original request identity rejected',not m.complete(changed))
        changed=copy.deepcopy(reports[0]);changed['telemetry_qualification']={'qualified':False,'issues':['Fewer than two telemetry samples']}
        check('insufficient telemetry refuses comparison without padding',not m.compare(r2,changed,reports[1])['whole18_comparable'])
        refused('existing output refused',lambda:m.execute(cross,r2,args))
        bad=copy.copy(args);bad.out=work/'missing-window';bad.exclusive_window=False
        refused('missing explicit exclusive window refused before launch',lambda:m.execute(cross,r2,bad))
        check('missing-window refusal writes nothing',not bad.out.exists())
        dll=work/'dotnet/dotnet';priorbytes=dll.read_bytes();dll.write_bytes(b'changed')
        bad=copy.copy(args);bad.out=work/'bad-tool'
        refused('changed dotnet executable refused',lambda:m.execute(cross,r2,bad));dll.write_bytes(priorbytes)
        selected=Path(previous['deployments']['baseline-managed_final-native']['target'])/'settings.json';priorbytes=selected.read_bytes();selected.write_bytes(b'changed')
        refused('changed frozen host setting refused',lambda:m.execute(cross,r2,bad));selected.write_bytes(priorbytes)
        state['fail']=True;bad=copy.copy(args);bad.out=work/'semantic-fail'
        failed=m.execute(cross,r2,bad)
        check('all72 preserved despite six repeated semantic failures',failed['run_complete']and not failed['all_cases_passed']and sum(len(r['failed_logical_ids'])for r in failed['runs'])==6)
        check('whole18 failed pair refused without cherry-picking',not failed['all_pairs_comparable']and len(failed['comparisons'][0]['case_pairs'])==18)
        state['fail']=False
        for mode in ('cleanup-after-stop','evidence-read'):
            bad=copy.copy(args);bad.out=work/mode
            first_pid=50000+len(state['hosts'])
            original_source=r2.source
            def stop(p):
                if p:
                    p.wait()
                    if mode=='cleanup-after-stop'and p.pid==first_pid:
                        raise OSError('injected cleanup failure after observed exit')
            def source(p):
                if mode=='evidence-read'and p.name=='job0-baseline-server.log':
                    raise OSError('injected final log read failure')
                return original_source(p)
            with patch.object(r2.common,'stop',side_effect=stop),patch.object(r2,'source',side_effect=source):
                faulty=m.execute(cross,r2,bad)
            rows=[json.loads(Path(r['source']['path']).read_text())for r in faulty['runs']]
            check('all72 retained and later jobs complete after '+mode,len(rows)==4 and sum(len(r['cases'])for r in rows)==72 and rows[0]['finalization_errors']and all(r['status']=='ok'for r in rows[1:]))
            check('finalization failure cannot pass and leaves no owned process: '+mode,not faulty['all_cases_passed']and all(p.stopped for p in state['hosts'])and not(bad.out/'active-server.json').exists())
        bad=copy.copy(args);bad.out=work/'live-cleanup'
        def leave_alive(p):
            if p:raise OSError('injected cleanup failure while alive')
        before=len(state['hosts'])
        with patch.object(r2.common,'stop',side_effect=leave_alive):
            live=m.execute(cross,r2,bad)
        check('failed live cleanup retains marker and refuses later jobs',len(state['hosts'])==before+1 and not live['run_complete']and(bad.out/'active-server.json').exists())
        state['hosts'][-1].wait()
    fakeproc=work/'proc';(fakeproc/'123').mkdir(parents=True)
    target=Path(previous['deployments']['baseline-managed_final-native']['target']);expected=previous['deployments']['baseline-managed_final-native']['expected_core_binaries']['libGgmlOps.so']
    maps=fakeproc/'123/maps';maps.write_text('1-2 r--p 0 00:00 1 '+str((target/'libGgmlOps.so').resolve())+'\n')
    check('actual maps parser accepts selected immutable path',m.verify_native_mapping(cross,123,target,expected,fakeproc)['library_sha256']==expected)
    maps.write_text('1-2 r--p 0 00:00 1 /other/libGgmlOps.so\n')
    refused('wrong loaded native path rejected',lambda:m.verify_native_mapping(cross,123,target,expected,fakeproc))
    maps.write_text('1-2 r--p 0 00:00 1 '+str(target/'libGgmlOps.so')+' (deleted)\n')
    refused('deleted loaded native path rejected',lambda:m.verify_native_mapping(cross,123,target,expected,fakeproc))

r2.write(HERE/'qwen35-solo72-checks.json',{'passed':len(checks),'checks':checks,'runner_source':r2.source(HERE/'run-qwen35-solo72.py'),
    'guard_source':r2.source(Path(__file__)),'prerequisite_cross_guards':40,'scope':'Local identity/request/coverage/lifecycle/environment guards with simulated endpoints; no VM or GPU activity.'})
print('PASS',len(checks),'solo72 local guards')
