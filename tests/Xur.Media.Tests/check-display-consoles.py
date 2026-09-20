#!/usr/bin/env python3
"""Actual two-DRM-device console/Plasma handoffs in the disposable development VM."""
import argparse,json,pathlib,socket,time,urllib.request
from guest import execute
from test_auth import api_session
p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('--reboot-only',action='store_true');p.add_argument('--cycles',type=int,default=3);a=p.parse_args();repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
assert json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm')
token=api_session(vm);evidence=vm/'display-console-evidence';evidence.mkdir(exist_ok=True)
def api(path,data=None):
 req=urllib.request.Request('http://127.0.0.1:18081'+path,data=None if data is None else json.dumps(data).encode(),headers={'Authorization':'Bearer '+token,'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=30) as r:return json.loads(r.read() or 'null')
def guest(script):
 r=execute(a.name,['/usr/bin/python3','-c',script]);assert r['code']==0,r['error'];return json.loads(r['output'])
def consoles():
 return guest("import subprocess,json\nu=subprocess.check_output(['systemctl','list-units','--plain','--no-legend','--state=active','xur-console-*.service']).decode().splitlines()\nprint(json.dumps([dict(x.split('=',1) for x in subprocess.check_output(['systemctl','show',line.split()[0],'-p','Id,MainPID,Environment,MemoryCurrent']).decode().splitlines()) for line in u]))")
def finish():
 for _ in range(180):
  s=api('/api/profiles');op=s['operation']
  if op['stage']=='Complete':return s
  assert op['stage']!='Failed',op
  time.sleep(1)
 raise AssertionError('Profile transition timed out')
def screenshot(label,secondary=False):
 with socket.socket(socket.AF_UNIX) as sock:
  sock.connect(str(vm/'qmp.sock'));s=sock.makefile('rwb',buffering=0);s.readline()
  def q(cmd,args={}):
   s.write((json.dumps({'execute':cmd,'arguments':args})+'\n').encode())
   while True:
    r=json.loads(s.readline())
    if 'error' in r:raise AssertionError(r)
    if 'return' in r:return r['return']
  q('qmp_capabilities');q('screendump',{'filename':str(evidence/(label+'.ppm')),**({'device':'secondary'} if secondary else {})})
 from PIL import Image
 im=Image.open(evidence/(label+'.ppm'));im.save(evidence/(label+'.png'));(evidence/(label+'.ppm')).unlink()
 # Actual rendered menu must have its broad highlight and border/content pixels.
 if 'console' in label:
  pixels=im.convert('L');bright=sum(1 for value in pixels.get_flattened_data() if value>160)
  assert bright>im.width*8,(label,'Blank or missing console',bright)
 return label+'.png'
def unload():
 if not api('/api/profiles')['runtime']['instances']:return
 plan=api('/api/profiles/unload/preview',{});api('/api/profiles/apply',{'id':plan['id'],'digest':plan['digest']});finish()
if a.reboot_only:
 old=api('/api/status')['bootId'];api('/api/power/reboot',{})
 for _ in range(180):
  try:
   if api('/api/status')['bootId']!=old and len(consoles())==2:break
  except (OSError,ValueError,AssertionError):pass
  time.sleep(1)
 else:raise AssertionError('Consoles did not return after reboot')
 time.sleep(5)
 images=[screenshot('reboot-console-primary'),screenshot('reboot-console-secondary',True)]
 assert api('/api/profiles')['runtime']['instances']==[]
 (evidence/'reboot.json').write_text(json.dumps({'result':'Passed','sameAuthentication':True,'consoles':consoles(),'images':images},indent=2)+'\n')
 print('Reboot and authentication checks passed');raise SystemExit
unload();time.sleep(4);initial=consoles();assert len(initial)==2,initial
images=[screenshot('both-console-primary'),screenshot('both-console-secondary',True)]
s=api('/api/profiles');gpus=s['runtime']['gpus'];assert len(gpus)==2
recipe=next(r for r in api('/api/recipes') if r['id']=='gaming-workstation')
profile=api('/api/profiles/create',{});pid=profile['id'];profile['name']='Display handoff test'
profile['workloads']=[{'id':'console-station','name':'Desktop','recipe':recipe,'gpus':[gpus[0]['pci']],'route':'console-station'}]
api('/api/profiles',profile)
cycles=[]
assert 1<=a.cycles<=20
for cycle in range(a.cycles):
 before=consoles();other=next(c for c in before if 'XUR_CONSOLE_CARD='+gpus[1]['cards'][0]+' ' in c['Environment'])
 api('/api/profiles/'+pid+'/load',{});s=finish();time.sleep(4)
 active=consoles();assert len(active)==1 and active[0]['MainPID']==other['MainPID'],active
 images += [screenshot(f'cycle-{cycle}-desktop'),screenshot(f'cycle-{cycle}-other-console',True)]
 instance=next(i for i in s['runtime']['instances'] if i['id']=='console-station');assert instance['state']=='running'
 unload();time.sleep(4);returned=consoles();assert len(returned)==2,returned
 images += [screenshot(f'cycle-{cycle}-returned-console'),screenshot(f'cycle-{cycle}-other-returned-console',True)]
 cycles.append({'stationPid':instance['pid'],'unassignedConsolePid':other['MainPID'],'restoredConsoles':returned})
receipt={'result':'Passed','cycles':cycles,'initial':initial,'images':images,'rendering':'kmscon drm2d on two QEMU bochs DRM adapters; real Plasma session','gpuPowerHardwareTest':'No adjustable physical GPU present'}
(evidence/'result.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({'result':'Passed','cycles':len(cycles),'evidence':str(evidence)}))
