#!/usr/bin/env python3
"""Reboot only an explicitly marked disposable VM and verify existing model bytes."""
import ssl,argparse,hashlib,json,pathlib,sys,time,urllib.request
from guest import execute
from test_auth import api_session
p=argparse.ArgumentParser();p.add_argument('--name',default='editor-dev');a=p.parse_args()
vm=pathlib.Path('.build/vms')/a.name
assert json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm'),'Requires a disposable development VM'
token=api_session(vm)
def api(path):
 with urllib.request.urlopen(urllib.request.Request('https://127.0.0.1:18443'+path,headers={'Authorization':'Bearer '+token}),timeout=10,context=ssl._create_unverified_context()) as r:return json.load(r)
def observe():
 script='''import pathlib,hashlib,json
root=pathlib.Path('/var/lib/xur/models')
print(json.dumps({'boot':pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip(),'models':{str(p):hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in root.glob('*.gguf')},'tls':hashlib.sha256(pathlib.Path('/var/lib/xur/manager-tls.pfx').read_bytes()).hexdigest()}))'''
 r=execute(a.name,['python3','-c',script]);assert r['code']==0,r['error'];return json.loads(r['output'])
bundle=api('/api/diagnostics/display')['controlBundle']
before=observe();assert before['models'],'Needs real downloaded models before reboot'
data=hashlib.file_digest((vm/'data.raw').open('rb'),'sha256').hexdigest()
r=execute(a.name,['systemd-run','--unit=xur-model-test-reboot','--on-active=2s','/usr/bin/systemctl','reboot']);assert r['code']==0,r['error']
for _ in range(150):
 try:
  after=observe()
  if after['boot']!=before['boot'] and api('/api/models')['models']:break
 except (OSError,RuntimeError,ValueError,KeyError):pass
 time.sleep(1)
else:raise AssertionError('VM did not return after reboot')
assert before['models']==after['models'],'Model bytes changed'
assert before['tls']==after['tls'],'Manager certificate changed'
assert data==hashlib.file_digest((vm/'data.raw').open('rb'),'sha256').hexdigest(),'Non-target data changed'
assert all(any(m['path']==path for m in api('/api/models')['models']) for path in before['models'])
assert api('/api/diagnostics/display')['controlBundle']==bundle
result={'result':'Passed','bundleId':bundle,'vm':a.name,'rebootObserved':True,'modelHashes':after['models'],'sameAuthentication':True,'sameTlsCertificate':True,'dataDiskUnchanged':True}
folder=vm/'workstation-evidence';folder.mkdir(exist_ok=True);(folder/'model-persistence.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
