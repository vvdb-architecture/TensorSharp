#!/usr/bin/env python3
"""Independent retained-artifact audit; no model or VM access."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def digest(value): return hashlib.sha256(json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest()


def audit(directory):
    root=json.loads((directory/'run.json').read_text())
    assert root['run_complete'] and root['expected_cases']==12 and not root['all_cases_passed']
    assert root['timings_qualified_for_comparison'] is False
    assert root['runner_source']['sha256']=='b5d3936196c5fe800c4a07e1f3676b06d510db7c3059d8ba8f6eefc2bcb85814'
    assert root['reviewed_r2_runner']['sha256']=='10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5'
    expected=[('baseline',256),('final',256),('final',76),('baseline',76),('baseline',64),('final',64)]
    assert [(r['job']['version'],r['job']['chunk'])for r in root['runs']]==expected
    assert root['request_sha256']==digest(root['request'])=='e1c5ae800ba0b4c69abce54cb2dd6301f7a8a6329b7a9892d83b8514eefaacfc'
    results=[];cases={};sources={};binaries={};weight_ids=set()
    for record in root['runs']:
        version,chunk=record['job']['version'],record['job']['chunk']
        p=directory/Path(record['source']['path']).name;assert sha(p)==record['source']['sha256'];child=json.loads(p.read_text());sources[p.name]=sha(p)
        assert child['run_complete'] and child['owned_process_exit_observed'] and not child.get('error')
        assert child['finalization_errors']==[] and child['deployment_changed_files']==[]
        assert child['cases']==record['cases'] and len(child['cases'])==2 and [c['repeat']for c in child['cases']]==[0,1]
        assert child['job']==record['job'];env=child['environment']
        assert env['TS_CB_DEBUG']=='1' and env['TS_SCHED_PREFIX_CACHE']=='0'
        assert env['TS_SCHED_MAX_BATCHED_TOKENS']=='256' and env['TS_SCHED_MAX_RUNNING_SEQS']=='4'
        assert env['TS_SCHED_PREFILL_CHUNK']==env['TS_SCHED_SOLO_PREFILL_CHUNK']==str(chunk)
        assert env['CUDA_VISIBLE_DEVICES']=='7' and env['KV_CACHE_DTYPE']=='f16'
        command=child['launch'];assert command[command.index('--prefill-chunk-size')+1]==str(chunk)
        assert '--no-prefix-cache'in command and '--no-spec'in command
        if version in binaries:assert binaries[version]==child['binary_sha256']
        else:binaries[version]=child['binary_sha256']
        weight_ids.add(child['weights']['sha256'])
        log=directory/Path(child['server_log']['path']).name;assert sha(log)==child['server_log']['sha256'];sources[log.name]=sha(log)
        text=log.read_text();steps={};completion=[]
        for line in text.splitlines():
            if line.startswith('[cb] '):
                m=re.fullmatch(r'\[cb\] SOLO step n=1 owner=.* work=\[([^,\]]+):([PD])@(\d+)\]',line);assert m,line
                key,phase,position=m.groups();steps.setdefault(key,[]).append((phase,int(position)))
            if 'chat.complete 'in line:
                m=re.search(r'tokens=(\d+) promptTokens=(\d+) kvReused=(\d+)',line);assert m;completion.append(tuple(map(int,m.groups())))
        assert len(steps)==len(completion)==2
        expected_p=[0]if chunk==256 else[0,chunk]
        lengths=[]
        for sequence in steps.values():
            first_decode=next(i for i,x in enumerate(sequence)if x[0]=='D')
            assert sequence[:first_decode]==[('P',x)for x in expected_p]
            assert all(phase=='D'for phase,_ in sequence[first_decode:]) and sequence[first_decode][1]==90
            assert [p for _,p in sequence[first_decode:]]==list(range(90,90+len(sequence)-first_decode))
            lengths.append([b-a for a,b in zip(expected_p,expected_p[1:]+[90])])
        for repeat,case in enumerate(child['cases']):
            assert case['tag']=='json-c4-r0-i0' and case['concurrency']==1 and len(case['turns'])==1
            turn=case['turns'][0];metric=turn['metrics'];assert turn['request']==root['request'] and digest(turn['request'])==root['request_sha256']
            assert metric['usage_present']is True and metric['prompt_tokens']==90 and metric['finish_reason']=='stop'
            assert metric['tool_calls']==[] and metric['reasoning_text']==''
            output=metric['assistant_message']['content'];assert output==metric['output_text']==case['validated_content']
            parsed=json.loads(output);semantic=parsed=={'name':'Mars','moons':2,'habitable':False}
            assert case['status']==('ok'if semantic else'fail')
            expected_tokens=25 if chunk==76 else 21
            assert metric['completion_tokens']==expected_tokens and semantic==(chunk!=76)
            assert completion[repeat]==(expected_tokens,90,0)
            identity={'assistant_message':metric['assistant_message'],'finish_reason':metric['finish_reason'],'prompt_tokens':metric['prompt_tokens'],'completion_tokens':metric['completion_tokens'],'tool_calls':metric['tool_calls'],'reasoning_text':metric['reasoning_text']}
            cases[(version,chunk,repeat)]=identity
        results.append({'version':version,'chunk_limit':chunk,'observed_prefill_lengths':lengths,'semantic_passed':sum(c['status']=='ok'for c in child['cases']),'cases':2,'completion_tokens':[c['turns'][0]['metrics']['completion_tokens']for c in child['cases']],'status':child['all_cases_passed']})
    assert len(cases)==12 and len(weight_ids)==1
    pairs=[]
    for chunk in(256,76,64):
        for repeat in range(2):
            a,b=cases[('baseline',chunk,repeat)],cases[('final',chunk,repeat)]
            assert a==b
            pairs.append({'chunk_limit':chunk,'repeat':repeat,'response_and_counts_identical':True,'identity_sha256':digest(a)})
        for version in('baseline','final'):assert cases[(version,chunk,0)]==cases[(version,chunk,1)]
    assert binaries['baseline']['libGgmlOps.so']=='e67e4c922138a7038bfc07d110d22eeec0f3f4cf10d545fae2971ca3f7dc3093'
    assert binaries['final']['libGgmlOps.so']=='6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014'
    return {'audit_passed':True,'cases':12,'paired_build_comparisons':pairs,'results':results,'binary_sha256':binaries,'weights_id':list(weight_ids)[0],
            'run_sha256':sha(directory/'run.json'),'child_and_server_log_sha256':sources,
            'conclusion':'The same exactrequest shows reproducible chunk-dependent semantic output in both preservedbuilds. No introduced within-shape output difference was observed in these six paired comparisons.',
            'limitations':['The original concurrent-run chunk lengths were not logged; this does not prove those calls used76+14.','No token logits or intermediate activation traces were collected, so kernel rounding versus an inherited chunking defect is not resolved.','Output equality here means recorded assistant response fields and prompt/completion counts, not bitwise internal logits or a sampledtoken-ID trace.','The four semantic failures remain failures. The diagnostic is not a no-regression or quality-parity blanket pass.','Timing is unqualified: debug logging, cold firstrequest and warm secondrequest, onlytwo repetitions.']}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--input',type=Path,default=Path(__file__).with_name('raw'));p.add_argument('--output',type=Path,default=Path(__file__).with_name('audit.json'));a=p.parse_args();result=audit(a.input);a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');print('12 cases audited; all six matching-build response/count pairs identical; four semantic failures preserved')
