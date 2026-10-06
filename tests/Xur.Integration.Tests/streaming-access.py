#!/usr/bin/env python3
"""Check streaming probe errors through disposable system-manager units (requires root)."""
import json, os, pathlib, pwd, re, subprocess, tempfile, uuid
from types import SimpleNamespace

root = pathlib.Path(__file__).resolve().parents[2]
if os.geteuid() != 0:
    raise SystemExit('Run this integration check as root against a running systemd system manager.')
user = pwd.getpwnam('nobody').pw_name
source = (root / 'src/Xur.Agent/StationStreaming.cs').read_text()
invocation = re.search(r'await Run\("systemd-run",\[(.*?)\]\);', source, re.S).group(1)
tokens = [json.loads(token) for token in re.findall(r'"(?:[^"\\]|\\.)*"', invocation)]
executable = tokens.index('devices')
flags = tokens[:executable]
# systemd binds ignored fixtures into its private mount namespace so nobody can execute
# them without changing the checkout or home directory permissions.
native = pathlib.Path(os.environ.get('XUR_UTIL',root/'.build/xurutil/xurutil'))
journal_invocation = re.search(r'await Processes.Run\("journalctl",\[(.*?)\]', source, re.S).group(1)
journal_flags = [json.loads(token) for token in re.findall(r'"(?:[^"\\]|\\.)*"', journal_invocation)]
evidence = root / '.build/evidence'
evidence.mkdir(parents=True, exist_ok=True)
observations = []

def probe(nodes, include_journal=True, command=None):
    unit = 'xur-stream-access-' + uuid.uuid4().hex
    args = ['--unit=' + unit if flag == '--unit=' else
            '--property=User=' + user if flag == '--property=User=' else flag for flag in flags]
    try:
        result = subprocess.run(['systemd-run', *args, '--property=DeviceAllow=/dev/null rw',
                                 '--property=TemporaryFileSystem=/opt',
                                 '--property=BindReadOnlyPaths=' + str(native) + ':/opt/xurutil ' + str(denied) + ':/opt/denied-device',
                                 *(probe_command if command is None else command), *nodes],
                                capture_output=True, text=True, timeout=20)
        output = result.stdout + result.stderr
        if result.returncode and include_journal:
            query = ['--unit=' + unit + '.service' if flag == '--unit=' else flag
                     for flag in journal_flags if flag != '.service']
            journal = subprocess.run(['journalctl', *query], capture_output=True, text=True, timeout=5)
            assert journal.returncode == 0, journal.stderr
            output += journal.stdout
        return SimpleNamespace(returncode=result.returncode, stdout=output, stderr='')
    finally:
        subprocess.run(['systemctl', 'stop', unit + '.service'], capture_output=True)
        subprocess.run(['systemctl', 'reset-failed', unit + '.service'], capture_output=True)

with tempfile.TemporaryDirectory(prefix='stream-access-', dir=evidence) as directory:
    fixture = pathlib.Path(directory)
    probe_command=['/usr/bin/env','/opt/xurutil','devices','check','--']
    denied = fixture / 'denied-device'
    denied.touch(mode=0o600)
    missing = pathlib.Path('/dev/xur-unit-probe-missing-' + uuid.uuid4().hex)
    # Demonstrate the reported failure with --wait and journal-only output.
    before = probe([str(missing)], include_journal=False)
    assert before.returncode != 0 and not (before.stdout + before.stderr).strip(), before
    for name, nodes, expected in [('allowed', ['/dev/null'], None),
                                  ('missing', [str(missing)], 'No such file or directory'),
                                  ('denied', ['/opt/denied-device'], 'Permission denied')]:
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
