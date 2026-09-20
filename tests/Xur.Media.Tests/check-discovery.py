#!/usr/bin/env python3
"""Check answer discovery in a running real ISO without releasing installation."""
import argparse,hashlib,http.cookiejar,json,pathlib,re,time,urllib.request,urllib.parse,urllib.error
p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('expected',choices=['NoAnswer','AnswerFound','Ambiguous']);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
jar=http.cookiejar.CookieJar();o=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
def req(path,data=None):
    try:
        with o.open('http://127.0.0.1:18081'+path,None if data is None else urllib.parse.urlencode(data).encode(),timeout=5) as r:return r.status,r.read().decode()
    except urllib.error.HTTPError as e:return e.code,e.read().decode()
def token(body):return re.search(r'name="__RequestVerificationToken" value="([^"]+)"',body).group(1)
for _ in range(120):
    try:
        if req('/health')[0]==200:break
    except OSError:pass
    time.sleep(2)
else:raise AssertionError('Live web server did not start')
assert req('/')[0]==200
assert req('/api/disks')[0]==401
from test_auth import browser_login
browser_login(vm,req)
for _ in range(60):
    status=json.loads(req('/api/installer')[1])
    if status['scan']['state']!='Starting':break
    time.sleep(1)
scan=status['scan'];assert scan['state']==a.expected,scan
assert status['operation'] is None
inventory=json.loads(req('/api/disks')[1]);disks=inventory['disks']
protected=[]
for disk in disks:
    if disk['serial'].startswith('XUR-CONFIG-'):
        assert any('ancestry' in reason for reason in disk['blocked']),disk
        protected.append(disk['serial'])
    if disk['serial']=='XUR-BOOT-USB001':
        assert disk['mounts'] and any('child partition' in reason for reason in disk['blocked']),disk
        protected.append(disk['serial'])
page=req('/install/storage')[1]
if a.expected!='NoAnswer':assert req('/install/plan',{'path':'/dev/vda','__RequestVerificationToken':token(req('/tailscale')[1])})[0]==409
hashes=json.loads((repo/'.build/fixtures/answer-hashes.json').read_text())
for entry in hashes:
    with (repo/entry['file']).open('rb') as f:assert hashlib.file_digest(f,'sha256').hexdigest()==entry['sha256']
receipt={'suite':'LiveDiscovery','vm':a.name,'state':scan['state'],'rootUI':True,'unauthenticatedInventoryDenied':True,'operation':None,'protectedParents':protected,'fixtureHashesUnchanged':True,'scan':scan}
if (vm/'vm-manifest.json').exists():receipt['media']=json.loads((vm/'vm-manifest.json').read_text())
(repo/'.build/evidence'/f'discovery-{a.name}.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
