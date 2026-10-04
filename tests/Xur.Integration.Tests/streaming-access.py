#!/usr/bin/env python3
"""Check streaming probe errors through disposable system-manager units (requires root)."""
import json, os, pathlib, pwd, re, subprocess, tempfile, uuid

root = pathlib.Path(__file__).resolve().parents[2]
if os.geteuid() != 0:
    raise SystemExit('Run this integration check as root against a running systemd system manager.')
user = pwd.getpwnam('nobody').pw_name
source = (root / 'src/Xur.Agent/StationStreaming.cs').read_text()
invocation = re.search(r'await Run\("systemd-run",\[(.*?)\]\);', source, re.S).group(1)
tokens = [json.loads(token) for token in re.findall(r'"(?:[^"\\]|\\.)*"', invocation)]
executable = tokens.index('/usr/bin/python3')
flags, python = tokens[:executable], tokens[executable:executable + 3]
evidence = root / '.build/evidence'
evidence.mkdir(parents=True, exist_ok=True)
observations = []

def probe(nodes, pipe=True, command=None):
    unit = 'xur-unit-probe-stream-access-' + uuid.uuid4().hex + '.service'
    args = ['--unit=' + unit if flag == '--unit=' else
            '--property=User=' + user if flag == '--property=User=' else flag for flag in flags]
    if not pipe:
        args.remove('--pipe')
    try:
        return subprocess.run(['systemd-run', *args, '--property=DeviceAllow=/dev/null rw',
                               *(python if command is None else command), *nodes],
                              capture_output=True, text=True, timeout=20)
    finally:
        subprocess.run(['systemctl', 'stop', unit], capture_output=True)
        subprocess.run(['systemctl', 'reset-failed', unit], capture_output=True)

with tempfile.TemporaryDirectory(prefix='stream-access-', dir=evidence) as directory:
    fixture = pathlib.Path(directory)
    fixture.chmod(0o755)
    denied = fixture / 'denied-device'
    denied.touch(mode=0o600)
    missing = pathlib.Path('/dev/xur-unit-probe-missing-' + uuid.uuid4().hex)
    # Demonstrate the reported failure with --wait and journal-only output.
    before = probe([str(missing)], pipe=False)
    assert before.returncode != 0 and not (before.stdout + before.stderr).strip(), before
    for name, nodes, expected in [('allowed', ['/dev/null'], None),
                                  ('missing', [str(missing)], 'No such file or directory'),
                                  ('denied', [str(denied)], 'Permission denied')]:
        result = probe(nodes)
        output = result.stdout + result.stderr
        if expected is None:
            assert result.returncode == 0, output
        else:
            assert result.returncode != 0 and expected in output and nodes[0] in output, output
        observations.append({'case': name, 'exitCode': result.returncode,
                             'errorCaptured': expected is not None})
    result = probe([], command=[str(missing)])
    output = result.stdout + result.stderr
    assert result.returncode != 0 and 'No such file or directory' in output and str(missing) in output, output
    observations.append({'case': 'missing-executable', 'exitCode': result.returncode, 'errorCaptured': True})

print(json.dumps({'suite': 'StreamingAccess', 'result': 'Passed', 'observations': observations}))
