#!/usr/bin/env python3
"""Architecture probe on a disposable VM; interrupts its Xur desktops temporarily.

This tests native Plasma/logind seats, not Xur profile orchestration or streaming.
The agent is restarted in finally to restore its saved workstation selection.
"""
import argparse,json,pathlib,time
from guest import execute

p=argparse.ArgumentParser()
p.add_argument('--name',required=True)
p.add_argument('--evidence',type=pathlib.Path,required=True)
a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2]
assert json.loads((repo/'.build/vms'/a.name/'vm-manifest.json').read_text()).get('developmentVm')

def guest(script):
    r=execute(a.name,['python3','-c',script])
    if r['code']!=0:raise AssertionError(r['error'] or r['output'])
    return r['output']

common=r'''
import subprocess,json,pathlib,os,pwd,time
def run(*args):return subprocess.check_output(args,text=True).strip()
def call(*args):subprocess.run(args,check=True,stdout=subprocess.DEVNULL)
names=['xurseatprobe1','xurseatprobe2']
rule=pathlib.Path('/run/udev/rules.d/72-xur-seat-probe.rules')
'''

guest(common+r'''
assert run('systemd-detect-virt') in ('kvm','qemu'), 'Use the disposable QEMU development VM'
assert not rule.exists(), 'Previous probe needs cleanup'
for name in names:
 try:pwd.getpwnam(name)
 except KeyError:pass
 else:raise AssertionError('Probe account already exists')
assert len(list(pathlib.Path('/sys/class/drm').glob('card[0-9]')))>=2
''')
try:
    setup=json.loads(guest(common+r'''
call('systemctl','stop','xur-agent.service')
for pattern in ('xur-stream-*.service','xur-station-*.service','xur-console-*.service'):
 call('systemctl','stop',pattern)
for session in json.loads(run('loginctl','list-sessions','--json=short')):
 user=session.get('user','')
 if isinstance(user,str) and user.startswith(('xurws','xurtmp','xuruser')):
  subprocess.run(['loginctl','terminate-user',user],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
cards=sorted(pathlib.Path('/sys/class/drm').glob('card[0-9]'))[:2]
rule.parent.mkdir(parents=True,exist_ok=True)
rule.write_text(''.join(f'SUBSYSTEM=="drm", KERNEL=="{card.name}", ENV{{ID_SEAT}}="seat-xur-probe{i+1}"\n' for i,card in enumerate(cards)))
call('udevadm','control','--reload')
for card in cards:call('udevadm','trigger',str(card))
call('udevadm','settle')
stations=[]
for i,(name,card) in enumerate(zip(names,cards)):
 home='/var/home/'+name
 call('useradd','--create-home','--user-group','--home-dir',home,'--shell','/bin/bash',name)
 uid=pwd.getpwnam(name).pw_uid
 node='/dev/dri/'+card.name
 policy=pathlib.Path(f'/run/systemd/system/user-{uid}.slice.d/50-xur-seat-probe.conf')
 policy.parent.mkdir(parents=True,exist_ok=True)
 policy.write_text('[Slice]\nDevicePolicy=closed\nDeviceAllow='+node+' rw\n')
 call('systemctl','daemon-reload')
 call('runuser','-u',name,'--','mkdir','-p',home+'/.config/environment.d')
 call('runuser','-u',name,'--','python3','-c',f"from pathlib import Path;Path({home+'/.config/environment.d/90-xur-gpu.conf'!r}).write_text({'KWIN_DRM_DEVICES='+node+chr(10)!r})")
 seat=f'seat-xur-probe{i+1}'
 call('systemd-run','--unit=xur-seat-probe'+str(i+1),'--property=Type=exec','--property=User='+name,'--property=PAMName=login','--property=Slice=user-'+str(uid)+'.slice','--property=KillMode=control-group','--setenv=HOME='+home,'--setenv=XDG_SEAT='+seat,'--setenv=XDG_SESSION_TYPE=wayland','--setenv=XDG_SESSION_CLASS=user','--setenv=XDG_CURRENT_DESKTOP=KDE','--setenv=QT_QPA_PLATFORM=wayland','--setenv=XDG_RUNTIME_DIR=/run/user/'+str(uid),'--setenv=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/'+str(uid)+'/bus','--setenv=KWIN_DRM_DEVICES='+node,'/usr/bin/startplasma-wayland')
 stations.append({'name':name,'uid':uid,'seat':seat,'card':node})
print(json.dumps(stations))
'''))
    for attempt in range(90):
        try:
            observed=json.loads(guest(common+'\nstations='+repr(setup)+r'''
for station in stations:
 uid=str(station['uid'])
 station['kwin']=int(run('pgrep','-u',uid,'-x','kwin_wayland'))
 station['plasma']=int(run('pgrep','-u',uid,'-x','plasmashell'))
 station['handles']=[]
 for fd in pathlib.Path('/proc/'+str(station['kwin'])+'/fd').iterdir():
  try:target=os.readlink(fd)
  except FileNotFoundError:continue
  if target.startswith('/dev/dri/'):station['handles'].append(target)
 assert station['handles'] and set(station['handles'])=={station['card']}, 'Compositor opened another GPU'
sessions=run('loginctl','list-sessions','--no-legend')
assert all(station['seat'] in sessions for station in stations)
print(json.dumps(stations))
'''))
            break
        except AssertionError:
            if attempt==89:raise
            time.sleep(1)
    guest(common+'\nstations='+repr(observed)+r'''
call('systemctl','stop','xur-seat-probe1.service')
call('loginctl','terminate-user',stations[0]['name'])
uid=str(stations[1]['uid'])
assert int(run('pgrep','-u',uid,'-x','kwin_wayland'))==stations[1]['kwin']
assert int(run('pgrep','-u',uid,'-x','plasmashell'))==stations[1]['plasma']
''')
    result={'result':'Passed','scope':'Native logind/Plasma architecture probe, not Xur profile orchestration','twoUsers':True,'twoSeats':True,'assignedGpuOnly':True,'stations':observed,'stopPreservedOtherDesktop':True,'usbHotplugTested':False,'audioRoutingTested':False,'concurrentStreamingTested':False}
finally:
    guest(common+r'''
for i,name in enumerate(names):
 subprocess.run(['systemctl','stop','xur-seat-probe'+str(i+1)+'.service'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 try:account=pwd.getpwnam(name)
 except KeyError:continue
 assert account.pw_dir=='/var/home/'+name and account.pw_uid>=1000
 subprocess.run(['loginctl','terminate-user',name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 subprocess.run(['pkill','-KILL','-u',name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 subprocess.run(['systemctl','stop',f'user@{account.pw_uid}.service'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 for attempt in range(100):
  if subprocess.run(['pgrep','-u',name],stdout=subprocess.DEVNULL).returncode==1:break
  time.sleep(.2)
 else:raise AssertionError('Probe user processes did not stop')
 pathlib.Path(f'/run/systemd/system/user-{account.pw_uid}.slice.d/50-xur-seat-probe.conf').unlink(missing_ok=True)
 call('userdel','--remove',name)
rule.unlink(missing_ok=True)
call('udevadm','control','--reload')
for card in pathlib.Path('/sys/class/drm').glob('card[0-9]'):call('udevadm','trigger',str(card))
call('udevadm','settle')
call('systemctl','daemon-reload')
call('systemctl','start','xur-agent.service')
''')

result["cleanupComplete"]=True
a.evidence.parent.mkdir(parents=True,exist_ok=True)
a.evidence.write_text(json.dumps(result,indent=2)+"\n")
print(json.dumps(result))
