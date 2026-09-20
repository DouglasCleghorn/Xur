#!/usr/bin/env python3
"""Observe the native workstation's real user processes and visible desktop."""
import argparse,json,pathlib,subprocess,time
from guest import execute
p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('workload');p.add_argument('--stopped',action='store_true');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
script=r'''
import hashlib,json,pathlib,pwd,subprocess,time
workload,stopped,fresh=ARGS
user='xurws'+hashlib.sha256(json.dumps(workload,separators=(',',':')).encode()).hexdigest()[:12]
uid=pwd.getpwnam(user).pw_uid
processes=[]
for p in pathlib.Path('/proc').glob('[0-9]*'):
 try:
  if p.stat().st_uid==uid:processes.append((int(p.name),(p/'comm').read_text().strip()))
 except FileNotFoundError:pass
if stopped:
 assert not processes,processes
else:
 if fresh:
  home=pathlib.Path(pwd.getpwnam(user).pw_dir)
  for app in ['bazzite-portal','steam','bazzite-announcement']:
   assert 'Hidden=true' in (home/'.config/autostart'/(app+'.desktop')).read_text()
 assert any(name=='kwin_wayland' for _,name in processes),processes
 assert any(name=='plasmashell' for _,name in processes),processes
 # Confirm the compositor received the selected-DRM-device constraint.
 pid=next(pid for pid,name in processes if name=='kwin_wayland')
 environment=pathlib.Path('/proc/'+str(pid)+'/environ').read_bytes().split(b'\0')
 assert any(e.startswith(b'KWIN_DRM_DEVICES=/dev/dri/card') for e in environment)
 for _ in range(60):
  if subprocess.run(['pgrep','-u',user,'-x','ksplashqml'],stdout=subprocess.DEVNULL).returncode==1:break
  time.sleep(1)
 else:raise AssertionError('The desktop stayed on its splash screen')
 environment=['XDG_RUNTIME_DIR=/run/user/'+str(uid),'DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/'+str(uid)+'/bus','WAYLAND_DISPLAY=wayland-0']
 subprocess.run(['runuser','-u',user,'--','env',*environment,'systemd-run','--user','--unit=xur-render-exercise','--collect','/usr/bin/vkcube','--wsi','xcb'],check=True,capture_output=True)
 time.sleep(3)
 assert subprocess.run(['pgrep','-u',user,'-x','vkcube'],stdout=subprocess.DEVNULL).returncode==0,'Vulkan renderer did not stay running'
print(json.dumps({'nativeProcesses':processes,'stopped':stopped,'gpuSelectionPresent':not stopped}))
'''
r=execute(a.name,['/usr/bin/python3','-c','ARGS='+repr([a.workload,a.stopped,not json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm',False)])+'\n'+script]);assert r['code']==0,r['error']
if not a.stopped:
 subprocess.run(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'screendump',json.dumps({'filename':str(vm/'workstation.ppm')})],check=True,stdout=subprocess.DEVNULL)
 # This is a fresh generated workstation account: no login credentials are on screen.
 import sys;sys.path.insert(0,str(repo/'.build/qr'))
 from PIL import Image,ImageChops
 first=Image.open(vm/'workstation.ppm').copy();time.sleep(1)
 subprocess.run(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'screendump',json.dumps({'filename':str(vm/'workstation.ppm')})],check=True,stdout=subprocess.DEVNULL)
 second=Image.open(vm/'workstation.ppm').copy()
 changed=sum(1 for pixel in ImageChops.difference(first,second).convert('RGB').getdata() if any(pixel))
 assert changed>1000,'The Vulkan exercise did not visibly animate: '+str(changed)+' changed pixels'
 second.save(repo/'.build/evidence/ui/workstation.png')
print(r['output'])
