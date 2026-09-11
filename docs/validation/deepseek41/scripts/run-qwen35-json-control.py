#!/usr/bin/env python3
"""VM-specific60-case alternating Qwen3.5 JSON latency control; no builds or copies.

Reuses the exact reviewed R2 run_version and frozen hosts. Preserves each whole
15-case job and every failed/incomparable pair. Root must reserve the window.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import time

R2_RUNNER=Path(__file__).with_name('run-final-json-performance.py')
R2_RUNNER_SHA='10ab194bb0c1397f353a3eda7e2eaa990a6d4f950d4e366f8d64940bf41553c5'
R2_RUN_SHA='7ba46725c2a73a7131b859855533b09cf864e4f5b6d64ecb494eb6f1eeb16763'
if hashlib.sha256(R2_RUNNER.read_bytes()).hexdigest()!=R2_RUNNER_SHA:
    raise RuntimeError('Reviewed R2 runner changed')
spec=importlib.util.spec_from_file_location('reviewed_r2',R2_RUNNER)
r2=importlib.util.module_from_spec(spec);spec.loader.exec_module(r2)
WORK=r2.WORK
LABEL='final3651-native6b3-qwen35-json-alternating'
PLAN=[{'pair':'pair1','versions':['baseline','final']},{'pair':'pair2','versions':['final','baseline']}]


def verify_inputs(path):
    if r2.common.file_sha(path)!=R2_RUN_SHA:
        raise ValueError('Completed R2 run manifest differs from pinned evidence')
    root=json.loads(path.read_text())
    if (root.get('run_complete') is not True or root.get('expected_timed_cases')!=90
            or root.get('planned_jobs')!=r2.job_plan() or len(root.get('runs',[]))!=6):
        raise ValueError('R2 must have all90 planned cases in six jobs')
    if root['runner_source']['sha256']!=R2_RUNNER_SHA or root['shared_helper']['sha256']!=r2.HELPER_SHA:
        raise ValueError('R2 source provenance changed')
    if r2.common.file_sha(r2.HELPER)!=r2.HELPER_SHA:
        raise ValueError('Reviewed shared helper changed')
    harness={Path(p).name:r2.common.file_sha(Path(p))for p in (r2.validation.__file__,r2.validation.engines.__file__,r2.validation.scenarios.__file__)}
    if harness!=root['harness_sha256']:
        raise ValueError('Portable harness differs from complete R2')
    expected=[(j['model'],v)for j in r2.job_plan()for v in j['versions']]
    if [(r['model'],r['version'])for r in root['runs']]!=expected:
        raise ValueError('R2 job coverage/order changed')
    children={}
    for record in root['runs']:
        child_path=Path(record['report']['path'])
        if r2.common.file_sha(child_path)!=record['report']['sha256']:
            raise ValueError('R2 child report hash changed: '+child_path.name)
        child=json.loads(child_path.read_text());version=record['version']
        if (not r2.complete(child) or record['cases']!=15 or record['run_complete'] is not True
                or child.get('deployment_changed_files') or child.get('error')):
            raise ValueError('R2 child coverage/deployment failure: '+child_path.name)
        if child['binary_sha256']!=root['deployments'][version]['expected_core_binaries']:
            raise ValueError('R2 child binary provenance changed')
        if child['environment']!=r2.common.ENV or child['harness_sha256']!=harness:
            raise ValueError('R2 child inference/harness settings changed')
        children[(record['model'],version)]=child
    if sum(len(c['cases'])for c in children.values())!=90:
        raise ValueError('R2 case total changed')
    for version,deployment in root['deployments'].items():
        target=Path(deployment['target'])
        changed=r2.common.unchanged(target,deployment)
        if changed:
            raise ValueError('Frozen '+version+' deployment changed: '+str(changed))
        for name in r2.BINARY_NAMES:
            if r2.common.file_sha(target/name)!=deployment['expected_core_binaries'][name]:
                raise ValueError('Frozen core binary changed: '+name)
    a,b=children[('qwen35','baseline')],children[('qwen35','final')]
    if a['weights_id']!=b['weights_id']:
        raise ValueError('R2 Qwen3.5 weight hashes differ')
    return root,children,harness


def distributions(a,b,comparison):
    aa={r2.key(c):c for c in a.get('cases',[])};bb={r2.key(c):c for c in b.get('cases',[])}
    result={}
    for degree in (1,4):
        records=[]
        for key in sorted(k for k in r2.planned_keys()if k[2]==degree):
            pair={'key':list(key),'group_comparable':comparison['concurrency_groups'][str(degree)]['comparable']}
            for version,cases in [('baseline',aa),('final',bb)]:
                c=cases.get(key)
                if c is None:pair[version]=None;continue
                metric=c['turns'][0]['metrics'] if len(c.get('turns',[]))==1 else {}
                pair[version]={'status':c['status'],'input_sha256':c.get('input_sha256'),
                               'output_sha256':r2.validation.digest(r2.output_identity(c)),
                               **{k:metric.get(k)for k in ('prompt_tokens','completion_tokens','ttft_ms','decode_tps','total_wall_ms','decode_timing_source','t_first_abs','t_last_abs')}}
            records.append(pair)
        result[str(degree)]=records
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--r2-run',type=Path,default=WORK/'existing-regressions/final3651-native6b3-json-performance-r2/run.json')
    parser.add_argument('--label',default=LABEL)
    parser.add_argument('--stop-file',type=Path)
    parser.add_argument('--exclusive-window',action='store_true')
    parser.add_argument('--plan-only',action='store_true')
    args=parser.parse_args()
    if args.plan_only:
        print(json.dumps({'plan':PLAN,'timed_cases':60,'decode512_warmups':4,'json_warmups':20,'source':r2.source(Path(__file__)),'reused_runner':r2.source(R2_RUNNER)},indent=2));return 0
    if not args.exclusive_window:parser.error('Root must reserve an exclusive measurement window')
    if not args.label or Path(args.label).name!=args.label:parser.error('Invalid label')
    out=WORK/'existing-regressions'/args.label
    if out.exists():parser.error('Refusing existing output; preserve previous evidence')
    args.stop_file=args.stop_file or out/'STOP'
    if args.stop_file.exists():parser.error('Stop marker already exists; no job started')
    previous,children,harness=verify_inputs(args.r2_run)
    weight=r2.common.identity('qwen35',r2.common.MODELS['qwen35'])
    if weight['sha256']!=children[('qwen35','baseline')]['weights_id']:
        raise ValueError('Current model bytes differ from R2')
    out.mkdir(parents=True)
    report={'label':args.label,'run_complete':False,'all_cases_passed':False,'all_groups_comparable':False,
            'planned_pairs':PLAN,'expected_timed_cases':60,'decode512_warmups':4,'json_warmups':20,
            'runner_source':r2.source(Path(__file__)),'reused_runner_source':r2.source(R2_RUNNER),
            'preceding_r2_source':r2.source(args.r2_run),'shared_helper':r2.source(r2.HELPER),
            'harness_sha256':harness,'reused_frozen_deployments':previous['deployments'],
            'weights':weight,'runs':[],'comparisons':{},'paired_case_distributions':{},
            'scope':'Two complete alternating pairs, exact same tagged15-case jobs each. No selected successful subsets, no production changes, no host copies; raw latency distributions remain even when a group is incomparable.'}
    r2.write(out/'run.json',report)
    for pair in PLAN:
        pair_out=out/pair['pair'];pair_out.mkdir();completed={}
        for version in pair['versions']:
            deployment=previous['deployments'][version]
            try:
                child=r2.run_version(args,'qwen35',version,Path(deployment['target']),deployment,pair_out,weight,
                                     {'source':children[('qwen35',version)]['historical_reference']},harness)
            except Exception as error:
                # Preserve a partial report and still attempt every later planned job.
                child_path=pair_out/('qwen35-'+version+'.json')
                child=json.loads(child_path.read_text())if child_path.exists()else {'cases':[],'waves':[],'warmups':[]}
                child.update(run_complete=False,status='fail',outer_error=type(error).__name__+': '+str(error))
                r2.write(child_path,child)
            completed[version]=child
            child_path=pair_out/('qwen35-'+version+'.json')
            report['runs'].append({'pair':pair['pair'],'version':version,'run_complete':child.get('run_complete')is True,
                                   'status':child.get('status'),'cases':len(child.get('cases',[])),'report':r2.source(child_path)})
            r2.write(out/'run.json',report)
        comparison=r2.compare(completed['baseline'],completed['final'])
        report['comparisons'][pair['pair']]=comparison
        report['paired_case_distributions'][pair['pair']]=distributions(completed['baseline'],completed['final'],comparison)
        r2.write(out/'run.json',report)
    report['run_complete']=len(report['runs'])==4 and all(r['run_complete']for r in report['runs'])and sum(r['cases']for r in report['runs'])==60
    report['all_cases_passed']=report['run_complete']and all(r['status']=='ok'for r in report['runs'])
    report['all_groups_comparable']=report['run_complete']and all(c['all_groups_comparable']for c in report['comparisons'].values())
    report['finished_utc']=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())
    r2.write(out/'run.json',report)
    print(json.dumps({'report':str(out/'run.json'),'run_complete':report['run_complete'],'all_cases_passed':report['all_cases_passed'],'all_groups_comparable':report['all_groups_comparable']},indent=2))
    return 0 if report['all_cases_passed']and report['all_groups_comparable']else 1

if __name__=='__main__':raise SystemExit(main())
