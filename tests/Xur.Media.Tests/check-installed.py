#!/usr/bin/env python3
"""Verify a real installed boot with installer media still attached."""
import argparse,hashlib,http.cookiejar,json,pathlib,re,time,urllib.request,urllib.parse,urllib.error,subprocess
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
o=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def request(path,data=None):
    try:
        with o.open('http://127.0.0.1:18081'+path,None if data is None else urllib.parse.urlencode(data).encode(),timeout=5) as r:return r.status,r.read().decode()
    except urllib.error.HTTPError as e:return e.code,e.read().decode()
def token(body):return re.search(r'name="__RequestVerificationToken" value="([^"]+)"',body).group(1)
for _ in range(120):
    try:
        status=json.loads(request('/api/status')[1])
        if status['mode']=='Installed':break
    except (OSError,ValueError):pass
    time.sleep(2)
else:raise AssertionError('Installed deployment did not boot')
assert request('/')[0]==200
assert request('/api/disks')[0]==401
from test_auth import browser_login
browser_login(vm,request)
agent=json.loads(request('/api/installer')[1]);assert not agent['installer'];assert agent['operation']['stage']=='Complete',agent
assert request('/install/plan',{'path':'/dev/vda','__RequestVerificationToken':token(request('/tailscale')[1])})[0]==409
blocks=json.loads(subprocess.check_output(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'query-block']))['return']
assert any('inserted' in b and (b['device']=='usbmedia' or b['inserted']['file'].endswith('.iso')) for b in blocks)
with (vm/'data.raw').open('rb') as f:after=hashlib.file_digest(f,'sha256').hexdigest()
assert after==(vm/'data-before.sha256').read_text().strip()
receipt={'suite':'InstalledBoot','vm':a.name,'installedMode':True,'installerMediaStillAttached':True,'webRoot':200,'newInstallDenied':True,'operation':agent['operation'],'dataSha256Before':after,'dataSha256After':after,'environment':'UEFI QEMU/KVM'}
receipt['media']=json.loads((vm/'vm-manifest.json').read_text())
(repo/'.build/evidence'/f'installed-{a.name}.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
