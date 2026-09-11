#!/usr/bin/env python3
"""Read-only, manifest-driven audit of final placement and serial-workflow runs.

Exit 0: integrity/coverage valid (scenario failures may remain).
Exit 1: missing/inconsistent evidence, log errors/preemption, or invalid token work.
Exit 2: --require-success was supplied and a recorded scenario failed.
"""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import statistics

QUALITY = ['short', 'json', 'json_schema', 'multi_turn', 'tool_round_trip', 'agentic']
PHASES = {
    'placement': [('quality', QUALITY, [1, 4], 1, True, False),
                  ('steady', ['decode'], [1, 4], 3, False, False),
                  ('long', ['long_8k', 'long_32k'], [1], 1, False, False)],
    'serial-tools': [('serial-tools', ['tool_round_trip', 'agentic'], [1, 4], 1, True, True)],
}
METRICS = ('ttft_ms', 'prefill_tps', 'decode_tps', 'total_wall_ms')

def sha(data): return hashlib.sha256(data).hexdigest()
def digest(value): return sha(json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':')).encode())
def file_source(path):
    data = path.read_bytes()
    return {'path': str(path), 'sha256': sha(data), 'bytes': len(data)}

def analyze(manifest, manifest_path, analysis_path):
    raw = Path(manifest['raw_dir']); profile = manifest['profile']; expected = manifest['expected']
    phases = manifest.get('audited_phases',manifest.get('phases',[])); outer_phases = manifest.get('expected_outer_phases',phases); issues = []; observations = []; sources = {}
    def check(valid, message):
        if not valid: issues.append(message)
        return bool(valid)
    def read(label):
        path = raw / (profile + '-' + label + '.json')
        try:
            sources[str(path)] = file_source(path)
            value = json.loads(path.read_text())
            if not isinstance(value, dict): raise ValueError('report is not an object')
            return value
        except (OSError, ValueError) as error:
            issues.append(f'{label}: {error}'); return {}
    def flags(command):
        return {value: command[i+1] for i, value in enumerate(command[:-1]) if value.startswith('--') and not command[i+1].startswith('--')}
    def checked_hash(value, name):
        check(isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) is not None, 'manifest requires a concrete SHA256: ' + name)
    for name in ('native_sha256', 'runtime_sha256', 'suite_sha256'):
        checked_hash(expected.get(name), name)
    for name, value in expected['harness_sha256'].items(): checked_hash(value, name)
    for name, value in expected.get('additional_binary_sha256',{}).items(): checked_hash(value,name)
    check(phases and len(set(phases)) == len(phases) and all(phase in PHASES for phase in phases), 'manifest phases must be unique supported placement/serial-tools phases')
    launch = read('launch'); outer = read('profile-runs')
    check(launch.get('profile') == profile, 'launch profile mismatch')
    check(outer.get('profile') == profile, 'outer profile mismatch')
    launch_flags = flags(launch.get('command', []))
    for name, value in expected['command_flags'].items():
        check(launch_flags.get(name) == str(value), f'launch flag {name}: expected {value!r}, got {launch_flags.get(name)!r}')
    for name, value in expected['environment'].items():
        check(launch.get('environment', {}).get(name) == str(value), f'launch environment {name} mismatch')
    for name, key in [('native_sha256', 'libGgmlOps.so'), ('runtime_sha256', 'TensorSharp.Runtime.dll')]:
        check(launch.get('sha256', {}).get(key) == expected[name], key + ' SHA mismatch')
    for name,value in expected.get('additional_binary_sha256',{}).items():
        check(launch.get('sha256',{}).get(name)==value,name+' SHA mismatch')
    check(outer_phases and len(set(outer_phases))==len(outer_phases) and set(phases)<=set(outer_phases),'manifest outer phases must be unique and contain audited phases')
    check(outer.get('planned_phases') == outer_phases, 'outer planned phase order/coverage mismatch')
    check(outer.get('run_complete') is True, 'outer plan is incomplete')
    check(outer.get('runner_sha256') == expected['suite_sha256'], 'outer recorded suite SHA mismatch')
    outer_runs = outer.get('runs', [])
    check([run.get('phase') for run in outer_runs] == outer_phases, 'outer recorded phase coverage mismatch')
    check(outer.get('all_passed') == (bool(outer_runs) and all(run.get('exit_code') == 0 for run in outer_runs)), 'outer all_passed disagrees with child exits')
    # The outer script records the SUITE hash, not its own executing source hash.
    observations.append('run-profile.py records the suite SHA; it does not record its own executed source SHA. The supplied local snapshots are provenance, not proof of an unrecorded historical outer-source hash.')
    unaudited=[]
    for phase in outer_phases:
        if phase in phases:continue
        envelope=read(phase+'-runs');entries=envelope.get('runs',[]);plan=envelope.get('execution_plan',{})
        check(envelope.get('profile')==profile and envelope.get('phase')==phase and envelope.get('run_complete') is True,phase+': unaudited phase envelope incomplete/wrong identity')
        check(envelope.get('runner_sha256')==expected['suite_sha256'],phase+': unaudited phase suite SHA mismatch')
        labels=[x.get('label')for x in entries]
        check(bool(entries) and labels==plan.get('labels') and len(entries)==plan.get('expected_runs') and len(set(labels))==len(labels),phase+': unaudited phase envelope labels incomplete')
        envelope_success=bool(entries) and all(x.get('exit_code')==0 and x.get('child_validation',{}).get('all_passed') is True for x in entries)
        check(envelope.get('all_passed')==envelope_success,phase+': unaudited phase envelope aggregate status mismatch')
        outer_entry=[x for x in outer_runs if x.get('phase')==phase]
        check(len(outer_entry)==1 and outer_entry[0].get('exit_code')==int(not envelope_success),phase+': outer exit disagrees with unaudited phase envelope')
        unaudited.append({'phase':phase,'outer_exit_code':outer_entry[0].get('exit_code')if len(outer_entry)==1 else None,'envelope_complete':envelope.get('run_complete'),'envelope_all_passed_declared':envelope.get('all_passed'),'labels':labels,'scope':'Envelope completion/status only. Child cases, semantic outputs, native work and performance are not independently audited by this helper.'})
    log_path = raw / (profile + '-server.log')
    try: sources[str(log_path)] = file_source(log_path); server = log_path.read_bytes()
    except OSError as error: issues.append(str(error)); server = b''
    rows = []; all_failures = []; telemetry = []; previous_end = 0
    for phase in phases:
        if phase not in PHASES: continue
        phase_report = read(phase + '-runs'); specifications = PHASES[phase]
        check(phase_report.get('profile') == profile and phase_report.get('phase') == phase, phase + ': identity mismatch')
        check(phase_report.get('run_complete') is True, phase + ': phase incomplete')
        check(phase_report.get('runner_sha256') == expected['suite_sha256'], phase + ': suite SHA mismatch')
        check(phase_report.get('execution_plan') == {'labels': [x[0] for x in specifications], 'expected_runs': len(specifications)}, phase + ': expected labels mismatch')
        runs = phase_report.get('runs', [])
        check([run.get('label') for run in runs] == [x[0] for x in specifications], phase + ': measured labels mismatch')
        phase_success = True
        for label, names, degrees, repeats, structured, serial in specifications:
            matches = [run for run in runs if run.get('label') == label]
            if not check(len(matches) == 1, label + ': missing/duplicate execution'): phase_success = False; continue
            run = matches[0]; child = read(label); cases = child.get('cases', []); expected_count = len(names)*sum(degrees)*repeats
            check(child.get('run_complete') is True and child.get('profile') == profile, label + ': child incomplete/wrong profile')
            check(child.get('execution_plan') == {'scenarios': names, 'concurrency': degrees, 'repeats': repeats, 'expected_cases': expected_count}, label + ': child plan mismatch')
            check(child.get('weights_id') == expected['weights_id'], label + ': checkpoint mismatch')
            check(child.get('harness_sha256') == expected['harness_sha256'], label + ': harness source mismatch')
            check(child.get('sampling') == expected['sampling'], label + ': sampling mismatch')
            check(child.get('thinking') is False and child.get('stream') is True, label + ': thinking/stream mismatch')
            check(child.get('max_tokens_override') is None,label+': generation budget override differs from plan')
            check(bool(child.get('structured_tool_results')) == structured and bool(child.get('serial_tool_workflows')) == serial, label + ': workflow policy mismatch')
            plan_keys = {(name, f'{name}-c{degree}-r{repeat}-i{i}', degree, repeat) for name in names for degree in degrees for repeat in range(repeats) for i in range(degree)}
            measured_keys = [(c.get('scenario'), c.get('tag'), c.get('concurrency'), c.get('repeat')) for c in cases]
            check(len(cases) == expected_count and len(set(measured_keys)) == len(measured_keys) and set(measured_keys) == plan_keys, label + ': missing/duplicate/unexpected cases')
            failed = [c for c in cases if c.get('status') != 'ok']; all_failures.extend({'label': label, 'case': c} for c in failed)
            check(all(c.get('status') in ('ok', 'fail') for c in cases), label + ': unknown case status')
            check(run.get('exit_code') == int(bool(failed)), label + ': child exit does not preserve case failure')
            phase_success &= not failed
            child_source = sources.get(str(raw/(profile+'-'+label+'.json')), {})
            cv = run.get('child_validation', {})
            check(cv.get('run_complete') is True and cv.get('expected_cases') == expected_count and cv.get('actual_cases') == len(cases) and cv.get('failed_cases') == len(failed) and cv.get('all_passed') == (not failed) and cv.get('report_sha256') == child_source.get('sha256'), label + ': recorded child validation/hash mismatch')
            check(child.get('warmup', {}).get('status') == 'ok', label + ': warmup failed or missing')
            actual_flags = flags(run.get('command', []))
            check(actual_flags.get('--scenarios') == ','.join(names) and actual_flags.get('--concurrency') == ','.join(map(str,degrees)) and actual_flags.get('--repeats') == str(repeats), label + ': executed scenario command mismatch')
            check(('--serial-tool-workflows' in run.get('command', [])) == serial and ('--structured-tool-results' in run.get('command', [])) == structured, label + ': executed workflow command mismatch')
            wave_keys = {(name, degree, repeat) for name in names for degree in degrees for repeat in range(repeats)}
            waves = child.get('waves', []); observed_waves = [(x.get('scenario'), x.get('concurrency'), x.get('repeat')) for x in waves]
            check(len(observed_waves) == len(set(observed_waves)) and set(observed_waves) == wave_keys, label + ': wave coverage mismatch')
            for wave in waves:
                members = [c for c in cases if (c.get('scenario'),c.get('concurrency'),c.get('repeat')) == (wave.get('scenario'),wave.get('concurrency'),wave.get('repeat'))]
                tokens = sum(t.get('metrics',{}).get('completion_tokens',0) for c in members for t in c.get('turns',[]))
                check(wave.get('generated_tokens') == tokens and wave.get('all_passed') == all(c.get('status') == 'ok' for c in members), label + ': wave token/success accounting mismatch')
            a = run.get('native_accounting', {}); start, end = a.get('byte_start'), a.get('byte_end')
            valid_range = isinstance(start,int) and isinstance(end,int) and 0 <= start <= end <= len(server)
            check(valid_range, label + ': invalid/incomplete server log range')
            blob = server[start:end] if valid_range else b''
            check(valid_range and start >= previous_end, label + ': overlapping/out-of-order native ranges')
            if valid_range: previous_end = end
            check(sha(blob) == a.get('range_sha256'), label + ': native range SHA mismatch')
            forwards = [(int(n),float(t)) for n,t in re.findall(rb'\[dsv4\] forward (\d+) tokens in ([\d.]+)s',blob)]
            native = {'prefill_calls':sum(n>1 for n,t in forwards),'prefill_tokens':sum(n for n,t in forwards if n>1),'prefill_forward_seconds':sum(t for n,t in forwards if n>1),'decode_calls':sum(n==1 for n,t in forwards),'decode_forward_seconds':sum(t for n,t in forwards if n==1)}
            check(all(isinstance(a.get(k),(int,float)) and math.isclose(a[k],v,rel_tol=1e-12,abs_tol=1e-9) for k,v in native.items()), label + ': native counter mismatch')
            chats = [(int(n),int(pr),int(reused),reason.decode()) for n,pr,reused,reason in re.findall(rb'chat.complete tokens=(\d+) promptTokens=(\d+) kvReused=(\d+).*?finishReason=(\S+)',blob)]
            check(native['prefill_tokens'] == sum(pr-reused for n,pr,reused,reason in chats), label + ': prompt/reuse/native work does not reconcile')
            check(native['decode_calls'] == sum(n+(reason=='eos') for n,pr,reused,reason in chats), label + ': generated/EOS/native work does not reconcile')
            check(len(chats) == 1+sum(len(c.get('turns',[])) for c in cases), label + ': chat completion/recorded turn count mismatch')
            # Exclude model-output lines from diagnostic matching: their text can discuss errors.
            diagnostic = [x.decode(errors='replace') for x in blob.splitlines() if not any(marker in x for marker in (b'assistantOutput=',b'userInput=',b'fullInput='))]
            preemptions = [x for x in diagnostic if re.search(r'preempting|preempted',x,re.I)]
            error_lines = [x for x in diagnostic if re.search(r'^\s*(fail|crit|fatal):|^\[dsv4\].*(error|failed)|terminate called|GGML_ASSERT|CUDA error|Segmentation fault',x,re.I)]
            check(not preemptions, label + ': scheduler preemptions observed')
            check(not error_lines, label + ': server/native error events observed')
            check(a.get('preemption_lines') == preemptions, label + ': recorded preemption lines mismatch')
            summaries = {}; case_evidence = []
            for case in cases:
                turns = case.get('turns', []); name = case.get('scenario'); successful = case.get('status') == 'ok'
                expected_turns = {'tool_round_trip':2,'agentic':3,'multi_turn':2}.get(name,1)
                check(bool(turns) and (not successful or len(turns)==expected_turns), label + ': successful case has wrong turn count')
                for t in turns:
                    request=t.get('request',{}); m=t.get('metrics',{})
                    check(m.get('usage_present') is True and bool(m.get('finish_reason')), label + ': turn usage/finish missing')
                    if serial: check(request.get('tools') and request.get('extra_body',{}).get('parallel_tool_calls') is False, label + ': serial tool-bearing request lacks constraint')
                if name == 'decode': check(len(turns)==1 and turns[0]['metrics'].get('completion_tokens')==512, label + ': decode work is not 512 tokens')
                case_evidence.append({'scenario':name,'tag':case.get('tag'),'status':case.get('status'),'input_sha256':case.get('input_sha256'),'request_sha256':[digest(t.get('request')) for t in turns],'output_sha256':[sha(t.get('metrics',{}).get('output_text','').encode()) for t in turns],'turn_metrics':[{k:t.get('metrics',{}).get(k) for k in ('prompt_tokens','completion_tokens','finish_reason','decode_timing_source')+METRICS} for t in turns]})
                if successful:
                    key=f'{name}@c{case["concurrency"]}';summaries.setdefault(key,[]).append(case)
            metrics={}
            for key, members in summaries.items():
                value={'passed':len(members)}
                for field in METRICS:
                    values=[c['turns'][0]['metrics'].get(field) for c in members];values=[x for x in values if isinstance(x,(float,int)) and math.isfinite(x) and x>=0]
                    if values:value[field+'_median']=statistics.median(values)
                if key.startswith('decode@'):
                    matching=[x for x in waves if f'{x["scenario"]}@c{x["concurrency"]}'==key]
                    if matching:
                        wall=statistics.median(x['wall_ms'] for x in matching);degree=members[0]['concurrency'];value['whole_wave_wall_ms_median']=wall;value['whole_wave_tokens_per_second']=degree*512/(wall/1000)
                metrics[key]=value
            rows.append({'phase':phase,'label':label,'complete':child.get('run_complete'),'expected_cases':expected_count,'actual_cases':len(cases),'passed':len(cases)-len(failed),'failed':len(failed),'case_evidence':case_evidence,'metrics':metrics,'wrapper_wall_seconds':run.get('wall_seconds'),'native':native,'chat_complete_count':len(chats),'generated_tokens_with_warmup':sum(n for n,pr,reused,reason in chats),'eos_forward_count':sum(reason=='eos' for n,pr,reused,reason in chats),'logged_prompt_tokens':sum(pr for n,pr,reused,reason in chats),'reused_prompt_tokens':sum(reused for n,pr,reused,reason in chats),'range':{k:a.get(k) for k in ('byte_start','byte_end','range_sha256')},'error_event_count':len(error_lines),'error_lines':error_lines,'preemption_count':len(preemptions),'preemption_lines':preemptions,'performance_window_declared':label in manifest.get('qualified_labels',[])})
        check(phase_report.get('all_passed') == phase_success, phase + ': all_passed disagrees with outputs')
        matched_outer=[run for run in outer_runs if run.get('phase')==phase]
        check(len(matched_outer)==1 and matched_outer[0].get('exit_code')==int(not phase_success), phase + ': outer exit disagrees with phase')
        csv_path=raw/(profile+'-'+phase+'-gpu.csv')
        try:
            sources[str(csv_path)]=file_source(csv_path)
            with csv_path.open() as stream: samples=[{k.strip():v.strip() for k,v in row.items()} for row in csv.DictReader(stream)]
            gpu_ids=sorted({row['index'] for row in samples}); expected_ids=expected['environment']['CUDA_VISIBLE_DEVICES'].split(',')
            check(gpu_ids==sorted(expected_ids), phase+': telemetry does not cover expected GPUs')
            gpu_metrics={}
            for gpu in gpu_ids:
                subset=[row for row in samples if row['index']==gpu]; fields={}
                for field in subset[0]:
                    if field in ('timestamp','index'):continue
                    values=[float(row[field].split()[0]) for row in subset]
                    fields[field]={'min':min(values),'median':statistics.median(values),'max':max(values)}
                gpu_metrics[gpu]={'samples':len(subset),'metrics':fields}
            telemetry.append({'phase':phase,'samples':len(samples),'first_timestamp':samples[0]['timestamp'],'last_timestamp':samples[-1]['timestamp'],'gpus':gpu_metrics,'source_sha256':sources[str(csv_path)]['sha256']})
        except (OSError,ValueError,KeyError,IndexError) as error: issues.append(phase+': missing/invalid telemetry: '+str(error))
    if launch.get('startup_performance_qualified') is not True:observations.append('Startup is not qualified by this audit; model loading/source warming are not included in inference performance.')
    return {'format_version':1,'profile':profile,'scope':'Manifest-driven final placement/serial-tools audit. Integrity success is not scenario success, isolated performance causality, or llama.cpp parity.','integrity_valid':not issues,'all_audited_scenarios_passed':bool(rows) and all(r['complete'] is True and r['actual_cases']==r['expected_cases'] and r['failed']==0 for r in rows),'performance_qualification':'Window qualification is declared by the manifest; this helper checks evidence but cannot certify resource exclusivity between telemetry samples.','issues':issues,'observations':observations,'audited_phases':phases,'expected_outer_phases':outer_phases,'not_numerically_audited':unaudited,'rows':rows,'failed_cases':all_failures,'telemetry':telemetry,'launch':launch,'manifest':manifest,'sources':list(sources.values()),'manifest_source':file_source(manifest_path),'analysis_source':file_source(analysis_path)}

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('manifest',type=Path);parser.add_argument('output',type=Path);parser.add_argument('--require-success',action='store_true');a=parser.parse_args()
    manifest=json.loads(a.manifest.read_text());result=analyze(manifest,a.manifest,Path(__file__))
    a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'integrity_valid':result['integrity_valid'],'all_audited_scenarios_passed':result['all_audited_scenarios_passed'],'issues':result['issues'],'phases':[(r['label'],r['passed'],r['actual_cases'])for r in result['rows']]},indent=2))
    return 1 if not result['integrity_valid'] else 2 if a.require_success and not result['all_audited_scenarios_passed'] else 0
if __name__=='__main__':raise SystemExit(main())
