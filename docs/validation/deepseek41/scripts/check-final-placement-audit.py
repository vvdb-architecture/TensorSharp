import copy,hashlib,importlib.util,json,pathlib,shutil,tempfile
source=pathlib.Path('/tmp/deepseek41-reference/audit-final-placement.py');spec=importlib.util.spec_from_file_location('audit',source);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
base=json.load(open('/tmp/deepseek41-reference/final-audit-historical-manifest.json'));profile=base['profile'];raw=pathlib.Path(base['raw_dir']);checks=[]
for mode in ['historical-failure-preserved','incomplete-outer','missing-child','duplicate-case','wrong-count','wrong-native-counter','wrong-native-sha','wrong-thread-setting','short-decode','log-error','preemption','serial-policy-followup','extra-phase-envelope-only','extra-phase-wrong-exit']:
 with tempfile.TemporaryDirectory()as directory:
  work=pathlib.Path(directory)
  for path in raw.glob(profile+'*'):shutil.copyfile(path,work/path.name)
  manifest=copy.deepcopy(base);manifest['raw_dir']=str(work)
  def load(label):return json.load(open(work/(profile+'-'+label+'.json')))
  def save(label,value):(work/(profile+'-'+label+'.json')).write_text(json.dumps(value))
  if mode=='incomplete-outer':d=load('profile-runs');d['run_complete']=False;save('profile-runs',d)
  elif mode=='missing-child':(work/(profile+'-long.json')).unlink()
  elif mode=='duplicate-case':d=load('quality');d['cases'][1]=copy.deepcopy(d['cases'][0]);save('quality',d)
  elif mode=='wrong-count':d=load('quality');d['cases'].pop();save('quality',d)
  elif mode=='wrong-native-counter':d=load('placement-runs');d['runs'][0]['native_accounting']['prefill_tokens']+=1;save('placement-runs',d)
  elif mode=='wrong-native-sha':manifest['expected']['native_sha256']='0'*64
  elif mode=='wrong-thread-setting':manifest['expected']['command_flags']['--cpu-moe-threads']='1'
  elif mode=='short-decode':d=load('steady');d['cases'][0]['turns'][0]['metrics']['completion_tokens']=511;save('steady',d)
  elif mode in ('log-error','preemption'):
   d=load('placement-runs');at=d['runs'][0]['native_accounting']['byte_start'];line=b'fail: synthetic audit error event\n'if mode=='log-error'else b'      preempted synthetic sequence\n';path=work/(profile+'-server.log');data=path.read_bytes();data=data[:at]+line+data[at:];path.write_bytes(data)
   for i,r in enumerate(d['runs']):
    a=r['native_accounting'];a['byte_start']+=len(line)if i else 0;a['byte_end']+=len(line);a['range_sha256']=hashlib.sha256(data[a['byte_start']:a['byte_end']]).hexdigest()
   save('placement-runs',d)
  elif mode=='serial-policy-followup':
   # Synthetic outer envelope around the real separately run serial follow-up.
   # This tests audit coverage, not a claim that the historical outer ran it.
   d=load('serial-tools-runs');manifest['phases']=['serial-tools'];manifest['qualified_labels']=[];manifest['expected']['suite_sha256']=d['runner_sha256'];manifest['expected']['harness_sha256']=load('serial-tools')['harness_sha256'];save('profile-runs',{'profile':profile,'planned_phases':['serial-tools'],'run_complete':True,'all_passed':True,'runner_sha256':d['runner_sha256'],'runs':[{'phase':'serial-tools','exit_code':0}]})
  if mode.startswith('extra-phase-'):
   manifest['audited_phases']=['placement'];manifest['expected_outer_phases']=['placement','tools'];d=load('profile-runs');d['planned_phases']=['placement','tools'];d['runs'].append({'phase':'tools','exit_code':0 if mode=='extra-phase-wrong-exit'else 1});save('profile-runs',d)
   save('tools-runs',{'profile':profile,'phase':'tools','runner_sha256':manifest['expected']['suite_sha256'],'run_complete':True,'all_passed':False,'execution_plan':{'labels':['tool-policies'],'expected_runs':1},'runs':[{'label':'tool-policies','exit_code':1,'child_validation':{'all_passed':False}}]})
  mp=work/'manifest.json';mp.write_text(json.dumps(manifest));result=module.analyze(manifest,mp,source)
  good=mode in ('historical-failure-preserved','serial-policy-followup','extra-phase-envelope-only');assert result['integrity_valid']==good,(mode,result['issues'])
  if mode=='historical-failure-preserved':assert not result['all_audited_scenarios_passed']and len(result['failed_cases'])==1 and result['failed_cases'][0]['case']['tag']=='agentic-c4-r0-i2'
  if mode=='serial-policy-followup':assert result['all_audited_scenarios_passed']and result['rows'][0]['actual_cases']==10
  if mode=='log-error':assert result['rows'][0]['error_event_count']==1
  if mode=='preemption':assert result['rows'][0]['preemption_count']==1
  if mode=='extra-phase-envelope-only':assert len(result['not_numerically_audited'])==1 and result['not_numerically_audited'][0]['envelope_all_passed_declared']is False and 'case'not in result['not_numerically_audited'][0]
  checks.append({'name':mode,'passed':True,'integrity_valid':result['integrity_valid'],'all_audited_scenarios_passed':result['all_audited_scenarios_passed'],'issues':result['issues']})
pathlib.Path('/tmp/deepseek41-reference/final-placement-audit-checks.json').write_text(json.dumps({'scope':'Local read-only audit validation using historical CPU4 evidence and explicitly mutated temporary copies; no final6b3 inference or VM activity.','checks':checks,'audit_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),'test_sha256':hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()},indent=2)+'\n')
print(len(checks),'audit checks passed')
