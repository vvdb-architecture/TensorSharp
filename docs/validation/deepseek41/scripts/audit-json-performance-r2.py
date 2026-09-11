#!/usr/bin/env python3
"""Local audit of completed JSON A/B, retaining every planned failure."""
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import statistics
import sys

WORK=Path('/tmp/deepseek41-reference')
ROOT=Path('/Users/zhongkaifu/work/TensorSharp')
RAW=WORK/'final-json-performance-r2'
R1=WORK/'final-json-performance'
QUALITY=WORK/'final-existing-regressions'
OUT=ROOT/'docs/validation/deepseek41/json-performance/completed-r2'
RUNNER=WORK/'run-final-json-performance.py'
sys.path.insert(0,str(ROOT/'benchmarks/engine_comparison'))
spec=importlib.util.spec_from_file_location('r2_runner',RUNNER)
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def source(p):return {'path':str(p),'sha256':sha(p),'bytes':p.stat().st_size}
def read(p):return json.loads(p.read_text())
def key(c):return c['scenario'],c['tag'],c['concurrency'],c.get('repeat',0)
def output(c):return m.output_identity(c)
def near(a,b):return math.isclose(a,b,rel_tol=1e-12,abs_tol=1e-10)

def check_case(c):
    assert len(c['turns'])==1
    t=c['turns'][0];spec=m.validation.case_spec('json',c['tag'])
    initial={**spec,'sampling':m.validation.SAMPLING,'thinking':False,'stream':True}
    assert c['input_sha256']==m.validation.digest(initial)
    expected={'messages':spec['messages'],'tools':None,'response_format':{'type':'json_object'},
              'extra_body':{**m.validation.SAMPLING,'think':False},'max_tokens':256,'stream':True}
    assert t['request']==expected
    metric=t['metrics'];text=metric['assistant_message'].get('content')
    assert (c['status']=='ok')==m.validation.check_answer('json',text)
    assert metric['usage_present'] and metric['prompt_tokens']>0 and metric['completion_tokens']>0
    assert metric['finish_reason']=='stop' and not metric['reasoning_text']
    assert metric['decode_timing_source']=='stream_window'
    assert near(metric['ttft_ms'],(metric['t_first_abs']-metric['t_start_abs'])*1000)
    assert near(metric['total_wall_ms'],(metric['t_end_abs']-metric['t_start_abs'])*1000)
    # Zero-span records are retained, not used as a positive decode estimate.
    span=metric['t_last_abs']-metric['t_first_abs']
    if span>0 and metric['decode_tps']>0:
        assert near(metric['decode_tps'],(metric['completion_tokens']-1)/span)


def telemetry(name,d):
    p=RAW/(name+'-telemetry.jsonl')
    assert sha(p)==d['telemetry_source']['sha256']
    rows=[json.loads(line)for line in p.read_text().splitlines()]
    a,b=d['timed_monotonic_interval'];rows=[r for r in rows if a<=r['monotonic']<=b]
    assert len(rows)>=2
    owner=rows[0]['compute_clients'][0]['pid'];runner=rows[0]['cpu'][str(owner)]['ppid']
    gpu=[];foreign=[]
    for row in rows:
        assert len(row['compute_clients'])==1 and row['compute_clients'][0]['pid']==owner
        g=[g for g in row['gpus']if g['index']=='7'];assert len(g)==1
        assert row['compute_clients'][0]['gpu_uuid']==g[0]['uuid'];gpu.append(g[0])
    for before,after in zip(rows,rows[1:]):
        allowed={owner,runner}
        while True:
            expanded=allowed|{int(pid)for pid,r in after['cpu'].items()if r['ppid']in allowed}
            if expanded==allowed:break
            allowed=expanded
        for pid,r in after['cpu'].items():
            old=before['cpu'].get(pid)
            if int(pid)not in allowed and old and old['start']==r['start']:
                delta=max(0,r['ticks']-old['ticks'])
                if delta:foreign.append({'pid':int(pid),'comm':r['comm'],'ticks':delta,'interval_seconds':after['monotonic']-before['monotonic']})
    stats={}
    for field in m.GPU_FIELDS.split(',')[2:]:
        values=[float(g[field])for g in gpu];assert all(math.isfinite(v)for v in values)
        stats[field]={'min':min(values),'median':statistics.median(values),'max':max(values)}
    q=d['telemetry_qualification'];assert q['qualified'] and q['issues']==[]
    assert q['samples_in_timed_interval']==len(rows) and q['gpu7']==stats
    assert not q['foreign_gpu_clients'] and not q['foreign_cpu_processes']
    for field,bound in [('clocks.sm',.03),('clocks.mem',.005)]:
        v=stats[field];assert v['min']>0 and (v['max']-v['min'])/v['median']<=bound
    # Retain raw non-owned tick deltas; only one1-tick sshd increment occurs.
    assert all(v['ticks']<=1 and v['interval_seconds']>=1 for v in foreign)
    return {'source':source(p),'samples':len(rows),'owner_pid':owner,'runner_pid':runner,'gpu7':stats,
            'foreign_gpu_clients':[],'nonzero_foreign_cpu_tick_deltas':foreign,
            'scope':'Raw GPU/client/clock statistics recomputed. Producer CPU qualification retained; non-owned raw tick deltas are shown without assuming a local OS clock-tick rate. Periodic sampling cannot prove every in-flight condition.'}


def first_warm(a,b):
    x,y=a['warmups'][0],b['warmups'][0]
    assert x['tag']==y['tag']=='json-warmup-c1-i0'
    req=m.request_identity(x)==m.request_identity(y);out=output(x)==output(y)
    clean=x['status']==y['status']=='ok'
    summary={'request_matches':req,'output_finish_token_counts_match':out,'both_semantically_pass':clean,
             'diagnostic_comparable':req and out and clean,'baseline':x,'final':y,
             'qualified':False,'scope':'First JSON use after the same unmeasured512-token decode warmup on each fresh host. One observation/version/model, before telemetry; no isolated grammar-compilation or stable cold-start claim.'}
    for d,c in [(a,x),(b,y)]:
        assert d['decode512_warmup']['turns'][0]['metrics']['t_end_abs']<=c['turns'][0]['metrics']['t_start_abs']<d['timed_monotonic_interval'][0]
    return summary


def main():
    r=read(RAW/'run.json');assert sha(RUNNER)==r['runner_source']['sha256']=='10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5'
    assert sha(m.HELPER)==r['shared_helper']['sha256']==m.HELPER_SHA
    for name,h in r['harness_sha256'].items():assert sha(ROOT/'benchmarks/engine_comparison'/name)==h
    assert sha(QUALITY/'run.json')==r['preceding_quality_run']['sha256']
    assert r['run_complete'] is True and r['expected_timed_cases']==90 and len(r['runs'])==6
    assert r['planned_jobs']==m.job_plan()
    assert [(v['model'],v['version'])for v in r['runs']]==[(j['model'],v)for j in m.job_plan()for v in j['versions']]
    reports={};tele={};qualified=[];changed=[]
    for run in r['runs']:
        name=run['model']+'-'+run['version'];p=RAW/(name+'.json');d=read(p)
        assert sha(p)==run['report']['sha256'] and run['cases']==15 and run['run_complete']
        assert d['run_complete'] and m.complete(d) and len(d['cases'])==15
        assert len(d['waves'])==6 and len(d['warmups'])==5
        assert d['binary_sha256']==r['deployments'][run['version']]['expected_core_binaries']
        for binary,h in d['binary_sha256'].items():assert r['deployments'][run['version']]['files_sha256'][binary]==h
        assert d['profile']==m.common.PROFILE and d['environment']==m.common.ENV
        assert d['sampling']==m.validation.SAMPLING and d['thinking'] is False and d['stream'] is True
        assert d['harness_sha256']==r['harness_sha256'] and d['deployment_changed_files']==[] and not d.get('error')
        logdir=Path(d['logging_environment']['TENSORSHARP_LOG_DIR'])
        assert logdir.name==name+'-file-logs' and Path(r['deployments'][run['version']]['target'])not in logdir.parents
        assert any((RAW/logdir.name).glob('*.jsonl'))
        assert sha(RAW/(name+'-server.log'))==d['server_log']['sha256']
        assert d['decode512_warmup']['status']=='ok' and d['decode512_warmup']['turns'][0]['metrics']['completion_tokens']==512
        for c in d['cases']+d['warmups']:check_case(c)
        for w in d['waves']:
            cases=[c for c in d['cases']if c['concurrency']==w['concurrency'] and c['repeat']==w['repeat']]
            assert len(cases)==w['concurrency']
            assert w['generated_tokens']==sum(c['turns'][0]['metrics']['completion_tokens']for c in cases)
            assert w['all_passed']==all(c['status']=='ok'for c in cases)
        reports[name]=d;tele[name]=telemetry(name,d)
    comparisons={};warm={};quality_pairs=[]
    for model in m.NAMES:
        a,b=reports[model+'-baseline'],reports[model+'-final']
        computed=m.compare(a,b);assert computed==r['comparisons'][model]
        comparisons[model]=computed;warm[model]=first_warm(a,b)
        aa={key(c):c for c in a['cases']};bb={key(c):c for c in b['cases']}
        for k in aa:
            assert m.request_identity(aa[k])==m.request_identity(bb[k])
            if aa[k]['status']=='ok' and bb[k]['status']!='ok':changed.append({'model':model,'key':list(k),'baseline':aa[k],'final':bb[k]})
        for degree,g in computed['concurrency_groups'].items():
            if not g['comparable']:
                assert 'speedup_final_over_baseline'not in g;continue
            qualified.append([model,int(degree)])
            for version,d in [('baseline',a),('final',b)]:
                cases=[c for c in d['cases']if c['concurrency']==int(degree)]
                summ=g['summaries'][version]
                for field,outfield in [('ttft_ms','ttft_ms_median'),('decode_tps','json_decode_tps_median'),('total_wall_ms','request_wall_ms_median')]:
                    assert near(summ[outfield],statistics.median(c['turns'][0]['metrics'][field]for c in cases))
                waves=[w for w in d['waves']if w['concurrency']==int(degree)]
                assert near(summ['whole_wave_wall_ms_median'],statistics.median(w['wall_ms']for w in waves))
            old,new=g['summaries']['baseline'],g['summaries']['final'];rr=g['speedup_final_over_baseline']
            for metric,field in [('ttft','ttft_ms_median'),('request_wall','request_wall_ms_median'),('whole_wave_wall','whole_wave_wall_ms_median')]:assert near(rr[metric],old[field]/new[field])
            assert near(rr['json_decode'],new['json_decode_tps_median']/old['json_decode_tps_median'])
        qpath=QUALITY/'quality'/(model+'-tensorsharp.json');q=read(qpath)
        assert q['binary_sha256']==b['binary_sha256']
        for c in q['cases']:
            if c['scenario']!='json':continue
            target=bb[key(c)];assert m.request_identity(c)==m.request_identity(target)
            quality_pairs.append({'model':model,'key':list(key(c)),'initial_fixture_sha256':c['input_sha256'],'recorded_http_request_sha256':m.validation.digest(c['turns'][0]['request']),'full_requests_equal':True,
                                  'quality_source':source(qpath),'quality_status':c['status'],'r2_final_status':target['status'],
                                  'quality_output':output(c),'r2_final_output':output(target)})
    assert qualified==[['qwen35',1],['qwen35',4]]
    assert len(changed)==1 and changed[0]['model']=='qwen3' and changed[0]['key'][1]=='json-c4-r0-i0'
    assert len(quality_pairs)==15
    assert sum(len(d['cases'])for d in reports.values())==90
    r1a,r1b=read(R1/'qwen3-baseline.json'),read(R1/'qwen3-final.json')
    result={'scope':'Independent local completion/identity/semantic/timing audit of all90 planned JSON cases. Every failure and incomparable group retained. Whole-build comparison, not isolated Unicode-only causal attribution.',
            'analysis_source':source(Path(__file__)),'run_source':source(RAW/'run.json'),'run_complete':True,'expected_cases':90,
            'completed_cases':90,'decode512_warmups':6,'json_warmups':30,'integrity_valid':True,
            'all_cases_passed':False,'passed_by_job':{k:sum(c['status']=='ok'for c in d['cases'])for k,d in reports.items()},
            'all_groups_comparable':False,'comparable_groups':qualified,'comparisons':comparisons,'introduced_failed_cases':changed,
            'telemetry':tele,'first_json_warmup_diagnostics':warm,'r1_qwen3_first_json_warmup':first_warm(r1a,r1b),
            'identical_request_quality_vs_r2':quality_pairs,
            'quality_scope':'All15 r0 JSON requests are identical to the corresponding earlier75quality-run requests. That paired run had zero newly failed cases; it does not establish universal behavior for those fixtures under different preceding warmups/repeats/scheduling. Qwen3 finaljson-c4-r0-i0 passed there and failed in both JSON A/B attempts with identical requesthash and finalbinaries.',
            'deployment_scope':'All producer checks report no frozen-file changes; core/config manifests and reportsource hashes cross-checked locally. This audit does not newly read/re-hash VM deployment files or modelweights.',
            'sources':[source(p)for p in sorted(RAW.rglob('*'))if p.is_file()]}
    OUT.mkdir(parents=True,exist_ok=True)
    for p in list(RAW.glob('*.json'))+list(RAW.glob('*-telemetry.jsonl')):
        dest=OUT/p.name
        if dest.exists():assert dest.read_bytes()==p.read_bytes()
        else:dest.write_bytes(p.read_bytes())
    (OUT/'audit.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'completed':90,'passed_by_job':result['passed_by_job'],'comparable_groups':qualified,'introduced':[v['key']for v in changed]},indent=2))

if __name__=='__main__':main()
