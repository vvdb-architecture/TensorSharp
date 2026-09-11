#!/usr/bin/env python3
"""Read-only loaded-library observation for the four instrumented crossing jobs."""
import hashlib
import json
from pathlib import Path
import time
ROOT = Path('/workspace/deepseek41-work/existing-regressions/qwen35-managed-native-cross-phase-r1')
OUT = Path('/workspace/deepseek41-work/qwen35-cross-loaded-libraries.json')
if OUT.exists():
    raise SystemExit('Refusing to overwrite existing observation')
report = {'observer_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), 'scope': 'Read-only /proc mapping observations once per owned diagnostic host; does not rehash live libraries or qualify timings.', 'observations': []}
seen = set()
end = time.monotonic() + 900
while time.monotonic() < end:
    try:
        active = json.loads((ROOT / 'active-server.json').read_text())
        pid = int(active['pid'])
        command = [v.decode() for v in Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0') if v]
        if command != active['command']:
            raise RuntimeError('Active PID command does not match owned launch record')
        if pid not in seen:
            mappings = [s for s in Path(f'/proc/{pid}/maps').read_text().splitlines() if 'libGgmlOps.so' in s]
            if mappings:
                seen.add(pid)
                report['observations'].append({'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'pid': pid, 'version': active['version'], 'command': command, 'mappings': mappings})
                OUT.write_text(json.dumps(report, indent=2) + '\n')
    except (FileNotFoundError, ProcessLookupError, json.JSONDecodeError):
        pass
    try:
        run = json.loads((ROOT / 'run.json').read_text())
        if run.get('finished_utc'):
            report['runner_finished_utc'] = run['finished_utc']
            break
    except (FileNotFoundError, json.JSONDecodeError):
        pass
    time.sleep(.5)
report['finished_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
report['observed_hosts'] = len(seen)
OUT.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({'observed_hosts': len(seen), 'output': str(OUT)}))
