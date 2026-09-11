from pathlib import Path
import importlib.util, subprocess,tempfile,json,os,hashlib,sys
import numpy as np
script=Path('/Users/zhongkaifu/work/TensorSharp/eng/tests/dsv41-final-smoke18.py')
spec=importlib.util.spec_from_file_location('smoke18',script); module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
checks=[]
def check(name,condition): checks.append(dict(name=name,passed=bool(condition)))
def rejects(name,operation):
 try: operation()
 except ValueError: check(name,True)
 else: check(name,False)
x=np.arange(1,33,dtype=np.float64)
summary=dict(top_tokens=np.argsort(x)[-10:][::-1].tolist(),last_logits_l2=float(np.linalg.norm(x.astype(np.float32))))
check('reference_top10_and_float32_logged_norm_accepted',module.validate_reference_logits(x,summary)>0)
rejects('same_shape_same_norm_wrong_reference_top10_rejected',lambda:module.validate_reference_logits(x[::-1],summary))
rejects('same_top10_wrong_reference_norm_rejected',lambda:module.validate_reference_logits(x*2,summary))
rejects('zero_native_norm_rejected',lambda:module.metrics(np.zeros(32),x))
rejects('zero_reference_norm_rejected',lambda:module.metrics(x,np.zeros(32)))
source={'GGML_CUDA_DISABLE_FUSION':'1','GGML_CUDA_FORCE_MMQ':'1','TS_DSV4_NODE_DUMP':'/tmp/unwanted','TS_DSV41_TEST_FAIL_STAGE':'rank','TS_DSV41_TRACE_DIR':'/tmp/trace','NVIDIA_TF32_OVERRIDE':'0','PATH':'retained','UNRELATED':'retained'}
env,removed=module.normalized_environment(source)
check('numerical_and_diagnostic_overrides_cleared_unrelated_environment_preserved',env=={'PATH':'retained','UNRELATED':'retained'} and source['GGML_CUDA_FORCE_MMQ']=='1')
with tempfile.TemporaryDirectory(prefix='dsv41-smoke18-guards-') as temporary:
 root=Path(temporary); bindir=root/'bin';bindir.mkdir();fake=bindir/'nvidia-smi';fake.write_text('#!/bin/sh\nprintf "777, 24000 MiB\\n"\n');fake.chmod(0o755)
 environment=os.environ.copy();environment['PATH']=str(bindir)+os.pathsep+environment['PATH'];output=root/'must-not-create'
 result=subprocess.run([sys.executable,str(script),'--work',str(root),'--output',str(output)],env=environment,text=True,capture_output=True)
 check('resident_gpu_rejected_before_checkpoint_read_or_output_creation',result.returncode==2 and not output.exists() and 'compute processes are still resident' in result.stderr)
 output.mkdir();sentinel=output/'keep.txt';sentinel.write_text('immutable control');fake.unlink()
 result=subprocess.run([sys.executable,str(script),'--work',str(root),'--output',str(output)],env=environment,text=True,capture_output=True)
 check('existing_output_rejected_before_gpu_inspection',result.returncode==2 and sentinel.read_text()=='immutable control' and 'Refusing to overwrite' in result.stderr)
report=dict(scope='Local runner evidence/control checks only; fake GPU process listing and32-element arrays, no native load, checkpoint read, or GPU execution.',source_sha256=hashlib.sha256(script.read_bytes()).hexdigest(),checks=checks)
Path('/Users/zhongkaifu/work/TensorSharp/docs/validation/deepseek41/smoke18-reference/runner-guards.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report,indent=2));assert all(x['passed'] for x in checks)
