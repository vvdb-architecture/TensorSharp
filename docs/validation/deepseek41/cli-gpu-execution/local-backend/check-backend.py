import argparse,ctypes,json,hashlib,os
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--library',required=True);p.add_argument('--backend',required=True);p.add_argument('--expect',choices=['accept','reject'],required=True);p.add_argument('--report',required=True);a=p.parse_args()
for key in list(os.environ):
 if key.startswith(('TS_DSV4','TS_DSV41','GGML_')):os.environ.pop(key)
f=Path('/tmp/deepseek41-reference/fixture-cuda-index/deepseek41-fixture.gguf')
lib=ctypes.CDLL(str(Path(a.library).resolve()));fn=lib.TSGgml_Dsv4LoadModel;fn.argtypes=[ctypes.c_char_p]+[ctypes.c_int]*5+[ctypes.c_char_p];fn.restype=ctypes.c_void_p
free=lib.TSGgml_Dsv4Free;free.argtypes=[ctypes.c_void_p];free.restype=None
h=fn(str(f).encode(),1,256,32,2,0,a.backend.encode());accepted=bool(h)
if h:free(h)
r=dict(backend=a.backend,accepted=accepted,expected=a.expect,passed=accepted==(a.expect=='accept'),library=str(Path(a.library).resolve()),library_sha256=hashlib.sha256(Path(a.library).read_bytes()).hexdigest(),fixture_sha256=hashlib.sha256(f.read_bytes()).hexdigest(),scope='Actual native model-load return value and cleanup, no inference and no log-string assertions.')
Path(a.report).write_text(json.dumps(r,indent=2)+'\n');print(json.dumps(r));raise SystemExit(0 if r['passed'] else 1)
