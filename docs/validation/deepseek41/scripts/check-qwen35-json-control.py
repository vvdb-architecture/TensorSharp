import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch
sys.path.insert(0,'/Users/zhongkaifu/work/TensorSharp/benchmarks/engine_comparison')
PATH=Path('/tmp/deepseek41-reference/run-qwen35-json-control.py')
spec=importlib.util.spec_from_file_location('control',PATH);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
RAW=Path('/tmp/deepseek41-reference/final-json-performance-r2')
checks=[]
with tempfile.TemporaryDirectory()as tmp:
    work=Path(tmp);prior=work/'r2';prior.mkdir();root=json.loads((RAW/'run.json').read_text());children={}
    for version in ('baseline','final'):
        target=work/'frozen'/version/'bin';target.mkdir(parents=True)
        for name in m.r2.BINARY_NAMES:(target/name).write_bytes((version+name).encode())
        (target/'appsettings.json').write_text('{"literal":"config"}')
        (target/'logs').mkdir();(target/'logs/tensorsharp-server-20260911.jsonl').write_text('inheritedlog')
        hashes={str(p.relative_to(target)):m.r2.common.file_sha(p)for p in target.rglob('*')if p.is_file()}
        root['deployments'][version].update(target=str(target),files_sha256=hashes,expected_core_binaries={n:hashes[n]for n in m.r2.BINARY_NAMES})
    for r in root['runs']:
        name=r['model']+'-'+r['version'];d=json.loads((RAW/(name+'.json')).read_text());d['binary_sha256']=root['deployments'][r['version']]['expected_core_binaries'];p=prior/(name+'.json');m.r2.write(p,d);r['report']=m.r2.source(p);children[(r['model'],r['version'])]=d
    parent=prior/'run.json';m.r2.write(parent,root);pinned=m.r2.common.file_sha(parent)
    before={str(p):p.read_bytes()for p in(work/'frozen').rglob('*')if p.is_file()}
    with patch.object(m,'R2_RUN_SHA',pinned):
        loaded,_,_=m.verify_inputs(parent);checks.append({'name':'complete-r2-and-all-frozen-binary-config-hashes-accepted','passed':True})
        target=Path(root['deployments']['final']['target'])
        for name in ['TensorSharp.Runtime.dll','appsettings.json','logs/tensorsharp-server-20260911.jsonl']:
            p=target/name;old=p.read_bytes();p.write_bytes(old+b'tamper')
            try:m.verify_inputs(parent)
            except ValueError:checks.append({'name':'frozen-tamper-rejected-'+name,'passed':True})
            else:raise AssertionError('Tamperaccepted')
            p.write_bytes(old)
        p=prior/'gemma4-final.json';old=p.read_bytes();p.write_bytes(old+b' ')
        try:m.verify_inputs(parent)
        except ValueError:checks.append({'name':'even-unmeasured-r2-child-hash-tamper-rejected','passed':True})
        else:raise AssertionError('Childtamperaccepted')
        p.write_bytes(old)
        old=parent.read_bytes();parent.write_bytes(old+b' ')
        try:m.verify_inputs(parent)
        except ValueError:checks.append({'name':'r2-root-manifest-tamper-rejected','passed':True})
        else:raise AssertionError('Roottamperaccepted')
        parent.write_bytes(old)
        for mode in ['complete','one-semantic-failure','uncaught-job-error']:
            calls=[]
            def run(args,name,version,target,deployment,out,weight,historical,harness):
                calls.append((out.name,version))
                assert m.r2.common.unchanged(target,deployment)==[]
                if mode=='uncaught-job-error' and len(calls)==1:raise OSError('injected job failure')
                d=copy.deepcopy(children[(name,version)])
                if mode=='one-semantic-failure'and len(calls)==2:
                    d['cases'][0]['status']='fail';d['status']='fail';d['waves'][0]['all_passed']=False
                p=out/(name+'-'+version+'.json');m.r2.write(p,d);return d
            argv=[str(PATH),'--r2-run',str(parent),'--label',mode,'--exclusive-window']
            with patch.object(m,'WORK',work),patch.object(m.r2.common,'identity',return_value=children[('qwen35','baseline')]['weights']),patch.object(m.r2,'run_version',side_effect=run),patch.object(m.r2,'freeze_version',side_effect=AssertionError('Must not copy hosts')),patch.object(m.r2.shutil,'copytree',side_effect=AssertionError('Must not copy hosts')),patch.object(sys,'argv',argv),patch('builtins.print'):
                code=m.main()
            result=json.loads((work/'existing-regressions'/mode/'run.json').read_text())
            assert calls==[('pair1','baseline'),('pair1','final'),('pair2','final'),('pair2','baseline')]
            assert len(result['runs'])==4 and len(result['comparisons'])==2 and len(result['paired_case_distributions'])==2
            assert {str(p):p.read_bytes()for p in(work/'frozen').rglob('*')if p.is_file()}==before
            if mode=='complete':
                assert code==0 and result['run_complete']and result['all_cases_passed']and result['all_groups_comparable']
                assert sum(r['cases']for r in result['runs'])==60
                old_values=[p['baseline']['ttft_ms']for p in result['paired_case_distributions']['pair1']['1']]
                assert old_values==[c['turns'][0]['metrics']['ttft_ms']for c in children[('qwen35','baseline')]['cases']if c['concurrency']==1]
                assert max(old_values)>150 # preserved actual R2outlier
            elif mode=='one-semantic-failure':
                assert code==1 and result['run_complete']and not result['all_cases_passed']and not result['all_groups_comparable']
                assert not result['comparisons']['pair1']['concurrency_groups']['1']['comparable']
                assert result['comparisons']['pair2']['all_groups_comparable']
            else:
                assert code==1 and not result['run_complete']and sum(r['cases']for r in result['runs'])==45
                assert result['comparisons']['pair2']['all_groups_comparable']
            checks.append({'name':mode+'-all-four-jobs-preserved-no-host-copies','passed':True})
            with patch.object(m,'WORK',work),patch.object(sys,'argv',argv),patch.object(m.r2,'run_version',side_effect=AssertionError('Must not launch')),patch('sys.stderr'):
                try:m.main()
                except SystemExit as e:assert e.code==2
                else:raise AssertionError('Overwriteaccepted')
        checks.append({'name':'existing-output-refused-before-launch','passed':True})
    broken=copy.deepcopy(root);broken['run_complete']=False;m.r2.write(parent,broken)
    with patch.object(m,'R2_RUN_SHA',m.r2.common.file_sha(parent)):
        try:m.verify_inputs(parent)
        except ValueError:checks.append({'name':'incomplete-r2-refused-even-with-matching-test-manifest-hash','passed':True})
        else:raise AssertionError('IncompleteR2accepted')
output=Path('/tmp/deepseek41-reference/qwen35-json-control-checks.json')
m.r2.write(output,{'scope':'Local simulated four-job orchestration using retained real R2cases and real temporary frozen files. No native/HTTP/VM inference or timings executed. Existing reviewed run_version remains unchanged.','runner_source':m.r2.source(PATH),'test_source':m.r2.source(Path(__file__)),'checks':checks})
print(len(checks),'checks passed')
