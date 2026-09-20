#!/usr/bin/env python3
"""Qualify one ISO using disposable UEFI VMs; never attach physical disks.

Requires the workspace Playwright and private QR decoder test dependencies.
Raw serial output, cookies and QR pixels stay under .build, outside releases.
"""
import argparse, hashlib, http.cookiejar, json, os, pathlib, re, subprocess, time
import urllib.parse, urllib.request, sys

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('iso', type=pathlib.Path)
p.add_argument('--prefix', required=True)
p.add_argument('--profiles',action='store_true')
a = p.parse_args()
assert re.fullmatch(r'[a-z0-9-]+', a.prefix)
repo = pathlib.Path(__file__).resolve().parents[2]
os.chdir(repo)
test = repo / 'tests/Xur.Media.Tests'
env = dict(os.environ, PYTHONPATH=str(repo / '.build/qr'))

def run(script, *args, node=False, receipt=None):
    try:
        result = subprocess.run(['node' if node else 'python3', str(test / script), *map(str, args)],
                                env=env, check=True, capture_output=True, text=True)
    except subprocess.CalledProcessError as error:
        sys.stderr.write(error.stderr);raise
    if receipt:
        data = json.loads(result.stdout)
        data['media'] = media
        (repo / '.build/evidence' / receipt).write_text(json.dumps(data, indent=2) + '\n')
    print(result.stdout.strip(), flush=True)

name = a.prefix + '-install'
run('start-vm.py', a.iso, '--name', name, '--usb-boot')
vm = repo / '.build/vms' / name
media = json.loads((vm / 'vm-manifest.json').read_text())
base = 'http://127.0.0.1:18081'
for _ in range(120):
    try:
        if urllib.request.urlopen(base + '/health', timeout=3).status == 200: break
    except OSError: pass
    time.sleep(2)
else: raise RuntimeError('Live web server did not start')
run('check-live.py', '--name', name)
run('check-settings-window.cjs', name, node=True, receipt='settings-ui.json')
run('capture-ui.cjs', base, name, node=True, receipt='web-ui.json')
run('check-login-format.cjs', name, node=True)
run('check-tailscale-login.cjs', name, node=True, receipt='tailscale-browser-login.json')
run('check-console-window.py', name)
run('check-visible-qr.py', name)
run('check-live.py', '--name', name, '--size-swap', '--approve', '--qr')

jar = http.cookiejar.LWPCookieJar(str(vm / 'session.private.cookies'))
jar.load(ignore_discard=True, ignore_expires=True)
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
for attempt in range(180):
    status = json.load(opener.open(base + '/api/installer', timeout=10))
    stage = status['operation']['stage']
    if stage == 'Complete': break
    if stage == 'Failed': raise RuntimeError('Installation failed; inspect redacted installer logs')
    if attempt % 6 == 0: print('Installation stage: ' + stage, flush=True)
    time.sleep(5)
else: raise RuntimeError('Installation did not complete in 15 minutes')
# Allow the console refresh to repair changes from installer console setup tools.
time.sleep(6)
with (vm / 'data.raw').open('rb') as source:
    digest = hashlib.file_digest(source, 'sha256').hexdigest()
assert digest == (vm / 'data-before.sha256').read_text().strip()
receipt = {'operation': status, 'dataDiskUnchanged': True, 'dataSha256': digest, 'media': media}
(repo / '.build/evidence/install-complete.json').write_text(json.dumps(receipt, indent=2) + '\n')
run('qmp.py', name, 'screendump', json.dumps({'filename': str(vm / 'display.private.ppm')}))
# Import only after the subprocesses have validated the QR decoder environment.
import sys
sys.path.insert(0, str(repo / '.build/qr'))
import zxingcpp
from PIL import Image
assert any('login.tailscale.com/' in r.text for r in
           zxingcpp.read_barcodes(Image.open(vm / 'display.private.ppm'), try_invert=True)), \
       'QR must remain readable after actual installation logs'
path = repo / '.build/evidence/console-window.json'
receipt = json.loads(path.read_text())
receipt['qrPreservedThroughActualInstall'] = True
path.write_text(json.dumps(receipt, indent=2) + '\n')
print('Install complete; data unchanged; QR still readable.', flush=True)
run('check-reboot-ui.cjs', name, node=True, receipt='reboot-ui.json')
run('check-installed.py', name)
run('prepare-guest.py', name)
run('check-kernel-console.py', name)
run('check-session-clock.py', name)
run('check-updates.cjs', name, node=True)
run('check-application-updates-ui.cjs', name, node=True)
run('check-updates-console.py', name)
run('check-bazzite-host.py', name)
if a.profiles:
    run('check-profiles.py', name)
    run('check-profiles-ui.cjs', name, node=True)
    run('check-catalog-workstation.cjs', name, node=True)
    run('check-application-updates.py', name, '--station')
    run('check-application-updates-ui.cjs', name, node=True)
    run('check-application-updates-console.py', name)
run('qmp.py', name, 'quit')
with (vm / 'data.raw').open('rb') as source:
    final_data_digest=hashlib.file_digest(source,'sha256').hexdigest()
assert final_data_digest==(vm/'data-before.sha256').read_text().strip(),'Data disk changed during updates or profile tests'
with (vm / 'installer-usb.raw').open('rb') as source:
    usb_digest = hashlib.file_digest(source, 'sha256').hexdigest()
assert usb_digest == media['isoSha256']
(repo / '.build/evidence/usb-preservation.json').write_text(json.dumps({
    'media': media, 'sha256Before': media['isoSha256'], 'sha256After': usb_digest,
    'unchanged': True,'dataUnchangedAfterUpdatesAndProfiles':True,'dataSha256':final_data_digest}, indent=2) + '\n')

for suffix, expected, fixtures in [
    ('no-disks', 'NoAnswer', []),
    ('one-answer', 'AnswerFound', ['answer-one.raw']),
    ('multiple-answers', 'Ambiguous', ['answer-one.raw', 'answer-two.raw'])
]:
    name = a.prefix + '-' + suffix
    args = [a.iso, '--name', name, '--no-disks']
    for fixture in fixtures: args += ['--extra-disk', repo / '.build/fixtures' / fixture]
    run('start-vm.py', *args)
    run('check-discovery.py', name, expected)
    if not fixtures: run('check-visible-qr.py', name)
    run('qmp.py', name, 'quit')
print(json.dumps({'suite': 'InstallerMedia', 'result': 'Passed', 'prefix': a.prefix,
                  'isoSha256': media['isoSha256']}), flush=True)
