#!/usr/bin/env python3
"""Exercise a real DRM fd conflict, resume, streaming start and teardown in a disposable VM."""
import pathlib,json,time,urllib.request
from guest import execute
from test_auth import api_session
vm=pathlib.Path('.build/vms/editor-dev');assert json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm')
token=api_session(vm)
def api(path,data=None):
 req=urllib.request.Request('http://127.0.0.1:18081'+path,data=None if data is None else json.dumps(data).encode(),headers={'Authorization':'Bearer '+token,'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=20) as r:return json.loads(r.read() or 'null')
def finish():
 for _ in range(120):
  s=api('/api/profiles');o=s['operation']
  if o['stage'] in ['Complete','Failed']:return o
  time.sleep(1)
 raise AssertionError('Profile transition timed out')
s=api('/api/profiles');profile=s['active'];assert profile
station=next(w for w in profile['workloads'] if w['recipe']['kind']=='Workstation');gpu=next(g for g in s['runtime']['gpus'] if g['pci'] in station['gpus'])
plan=api('/api/profiles/unload/preview',{});api('/api/profiles/apply',{'id':plan['id'],'digest':plan['digest']});assert finish()['stage']=='Complete'
assert execute('editor-dev',['pgrep','-x','sunshine'])['code']==1
command="import time; f=open("+repr(gpu['cards'][0])+",'rb',buffering=0); time.sleep(120)"
r=execute('editor-dev',['systemd-run','--unit=xur-test-gpu-owner','--collect','/usr/bin/python3','-c',command]);assert r['code']==0,r
try:
 time.sleep(1);api('/api/profiles/'+profile['id']+'/load',{});blocked=finish();assert blocked['stage']=='Failed' and 'xur-test-gpu-owner.service' in blocked['error'],blocked
finally:execute('editor-dev',['systemctl','stop','xur-test-gpu-owner'])
api('/api/profiles/resume',{});assert finish()['stage']=='Complete'
streams=api('/api/workstations');assert any(s['state']=='running' and s['stream']['state']=='Ready' for s in streams)
try:api('/api/workstations/'+station['id']+'/pairings');raise AssertionError('Plain HTTP pairing allowed')
except urllib.error.HTTPError as e:assert e.code==403
for path in ['/api/models','/api/network-usage','/api/workstations']:
 try:urllib.request.urlopen('http://127.0.0.1:18081'+path);raise AssertionError('Anonymous inventory allowed')
 except urllib.error.HTTPError as e:assert e.code==401
result={'result':'Passed','realDrmOwnerBlocks':True,'ownerServiceNamed':True,'resumeAfterRelease':True,'sunshineStoppedOnUnload':True,'sunshineReadyAfterReload':True,'httpPairingDenied':True,'anonymousInventoryDenied':True}
(vm/'workstation-evidence/ownership.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
