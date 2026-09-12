import argparse, hashlib, json, os, pathlib, shutil, signal, subprocess, time, urllib.request

p = argparse.ArgumentParser()
p.add_argument('profile')
p.add_argument('--previous', required=True)
p.add_argument('--server-dir', required=True)
p.add_argument('--source-server-dir', default='server-v41-final3635')
p.add_argument('--tp', type=int, default=0)
p.add_argument('--cpu-moe', type=int, default=0)
p.add_argument('--cpu-threads', type=int, default=32)
a = p.parse_args()
w = pathlib.Path('/workspace/deepseek41-work')
report_path = w / (a.profile + '-preparation.json')
if report_path.exists() or (w / (a.profile + '-launch.json')).exists() or (w / a.server_dir).exists():
    raise RuntimeError('Refusing to overwrite an existing profile or host directory')
previous = json.loads((w / (a.previous + '-launch.json')).read_text())
pid = previous['pid']
proc = pathlib.Path('/proc') / str(pid)
if proc.exists():
    cmd = (proc / 'cmdline').read_bytes().split(b'\0')
    expected = previous['command'][1].encode()
    if expected not in cmd:
        raise RuntimeError('Previous PID does not match its recorded host')
    os.kill(pid, signal.SIGTERM)
    deadline = time.monotonic() + 30
    while proc.exists() and time.monotonic() < deadline:
        if (proc / 'stat').read_text().split()[2] == 'Z':
            break
        time.sleep(1)
    if proc.exists() and (proc / 'stat').read_text().split()[2] != 'Z':
        os.kill(pid, signal.SIGKILL)
        time.sleep(2)
source = w / a.source_server_dir
shutil.copytree(source, w / a.server_dir)
native = w / 'native-v41-shared-pins-6b3b5ab3.so'
native_sha = hashlib.file_digest(native.open('rb'), 'sha256').hexdigest()
if native_sha != '6b3b5ab3c333c59423bc10efe9f18b5f0483fd0a478823647471c5de7e736014':
    raise RuntimeError('Final native archive hash mismatch')
r = {'profile': a.profile, 'previous_profile': a.previous,
     'source_sha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
     'started_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
     'startup_performance_qualified': False,
     'startup_note': 'Reload from existing model files; expert source warming overlaps model load. Startup is diagnostic only.',
     'ready': False}
report_path.write_text(json.dumps(r, indent=2) + '\n')
cmd = ['python3', str(w / 'launch-final.py'), a.profile,
       '--server-dir', a.server_dir, '--native', str(native),
       '--tp', str(a.tp), '--cpu-moe', str(a.cpu_moe), '--cpu-threads', str(a.cpu_threads),
       '--gpus', '8', '--ubatch', '1024', '--compact', '1',
       '--prefill-chunk', '1024', '--solo-prefill-chunk', '1024',
       '--max-running', '4', '--max-batched', '4096',
       '--mmproj', '/workspace/models/DeepSeek-V4.1-Flash-Q2_K/deepseek41.vision.gguf']
subprocess.run(cmd, check=True)
launch_path = w / (a.profile + '-launch.json')
launch = json.loads(launch_path.read_text())
launch.update({key: r[key] for key in ('startup_performance_qualified', 'startup_note')})
launch_path.write_text(json.dumps(launch, indent=2) + '\n')
warm_path = w / (a.profile + '-expert-source-warming.json')
warm_log = w / (a.profile + '-expert-source-warming.log')
warm_cmd = [str(w / 'reference-venv/bin/python'), '/workspace/TensorSharp/eng/dsv41-warm-experts.py',
            '/workspace/models/DeepSeek-V4.1-Flash-Q2_K/DeepSeek-V4.1-Flash-Q2_K-00001-of-00007.gguf',
            '--layers', str(a.cpu_moe if a.cpu_moe else 40), '--workers', '4', '--report', str(warm_path)]
with warm_log.open('w') as warm_out:
    warmer = subprocess.Popen(warm_cmd, stdout=warm_out, stderr=subprocess.STDOUT)
    deadline = time.monotonic() + 1800
    last_notice = 0
    while time.monotonic() < deadline:
        if not (pathlib.Path('/proc') / str(launch['pid'])).exists():
            raise RuntimeError('Host exited while loading')
        endpoint_ready = False
        try:
            with urllib.request.urlopen('http://127.0.0.1:5000/health', timeout=2) as response:
                endpoint_ready = response.status == 200
        except Exception:
            pass
        if endpoint_ready and warmer.poll() is not None:
            r['warm_exit_code'] = warmer.returncode
            if warmer.returncode != 0:
                raise RuntimeError('Source warming failed; inspect its retained log')
            r['ready'] = True
            break
        if time.monotonic() - last_notice >= 30:
            print(json.dumps({'profile': a.profile, 'endpoint_ready': endpoint_ready,
                              'warming_complete': warmer.poll() is not None}), flush=True)
            last_notice = time.monotonic()
        time.sleep(5)
    if not r['ready']:
        raise RuntimeError('Profile did not become ready within 30 minutes')
r['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
r['launch_sha256'] = hashlib.sha256(launch_path.read_bytes()).hexdigest()
r['warming_sha256'] = hashlib.sha256(warm_path.read_bytes()).hexdigest()
report_path.write_text(json.dumps(r, indent=2) + '\n')
print(json.dumps(r), flush=True)
