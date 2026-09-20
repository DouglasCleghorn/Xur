#!/usr/bin/env python3
"""Disposable-VM regression: start without monitors and deliver uinput to Plasma."""
import argparse,base64,json,pathlib,textwrap
from guest import execute
p=argparse.ArgumentParser();p.add_argument('--name',required=True);p.add_argument('--evidence',type=pathlib.Path,required=True);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2]
assert json.loads((repo/'.build/vms'/a.name/'vm-manifest.json').read_text()).get('developmentVm')
def guest(s):
 r=execute(a.name,['python3','-c',s])
 if r['code']:raise AssertionError(r['error']+r['output'])
 return r['output']
script=textwrap.dedent((repo/'src/Xur.Agent/StationRuntime.cs').read_text().split('internal const string UserConfiguration="""\n')[1].split('""";')[0])
authorize=textwrap.dedent((repo/'src/Xur.Agent/StationVirtualDisplay.cs').read_text().split('const string Authorize="""\n')[1].split('""";')[0])
binary=base64.b64encode((repo/'.build/virtual-display-runtime/xur-virtual-output').read_bytes()).decode()
common=r'''
import subprocess,pathlib,json,os,time,pwd,re,base64,fcntl,struct
def run(*args):return subprocess.check_output(args,text=True).strip()
def call(*args):subprocess.run(args,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
state=pathlib.Path('/run/xur-headless-probe.json')
'''
try:
 setup=guest(common+'\nscript='+repr(script)+'\nbinary='+repr(binary)+r'''
assert not state.exists(),'Previous probe needs cleanup'
config=pathlib.Path('/etc/plasmalogin.conf.d/90-xur-workstation.conf')
user=re.search(r'^User=(.+)$',config.read_text(),re.M)[1];account=pwd.getpwnam(user);uid=str(account.pw_uid);home=account.pw_dir
assert user.startswith(('xurws','xuruser'))
override=pathlib.Path(home+'/.config/systemd/user/plasma-kwin_wayland.service.d/90-xur-headless.conf')
envfile=pathlib.Path(home+'/.config/environment.d/90-xur-gpu.conf')
policy=pathlib.Path('/run/systemd/system/user-'+uid+'.slice.d/50-xur.conf')
desktop=pathlib.Path(home+'/.local/share/applications/dev.xur.VirtualDisplay.desktop')
files={str(p):base64.b64encode(p.read_bytes()).decode() if p.exists() else None for p in (override,envfile,policy,desktop)}
services=[unit for unit in ('xur-agent.service','xur-control.service','xur-gateway.service') if subprocess.run(['systemctl','is-active','--quiet',unit]).returncode==0]
state.write_text(json.dumps(dict(user=user,uid=uid,home=home,files=files,services=services)))
call('systemctl','stop','xur-agent.service')
for pattern in ('xur-stream-*.service','xur-virtual-output-*.service','xur-station-*.service','xur-console-*.service'):call('systemctl','stop',pattern)
subprocess.run(['loginctl','terminate-user',user],capture_output=True)
time.sleep(2)
for status in pathlib.Path('/sys/class/drm').glob('card*-*/status'):status.write_text('off')
card='/dev/dri/card0'
call('modprobe','uinput');call('udevadm','settle')
s=json.loads(state.read_text());st=os.stat('/dev/uinput');s['uinput']=[st.st_uid,st.st_gid,st.st_mode&0o777];state.write_text(json.dumps(s))
os.chown('/dev/uinput',0,account.pw_gid);os.chmod('/dev/uinput',0o660)
policy.parent.mkdir(parents=True,exist_ok=True);policy.write_text('[Slice]\nDevicePolicy=closed\nDeviceAllow='+card+' rw\nDeviceAllow=char-input rw\nDeviceAllow=/dev/uinput rw\nDeviceAllow=char-tty rw\nDeviceAllow=/dev/tty rw\nDeviceAllow=char-alsa rw\n')
call('runuser','-u',user,'--','bash','-c',script,'xur',home,card,'headless','')
call('restorecon','-RF',home)
helper=pathlib.Path('/run/xur-virtual-output-probe');helper.write_bytes(base64.b64decode(binary));helper.chmod(0o755);call('chcon','-t','bin_t',str(helper))
call('systemctl','daemon-reload')
call('systemd-run' ,'--unit=xur-headless-probe','--collect','--property=Type=exec','--property=KillMode=control-group','--setenv=KWIN_DRM_DEVICES='+card,'/usr/bin/plasmalogin')
print(json.dumps(dict(user=user,uid=uid)))
''')
 print('Started headless DRM session',setup,flush=True)
 observed=guest(common+'\nauthorize='+repr(authorize)+r'''
s=json.loads(state.read_text());uid=s['uid'];user=s['user'];home=s['home']
base=['runuser','-u',user,'--','env','XDG_RUNTIME_DIR=/run/user/'+uid,'DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/'+uid+'/bus']
for attempt in range(90):
 r=subprocess.run(base+['systemctl','--user','show-environment'],capture_output=True,text=True)
 env=dict(line.split('=',1) for line in r.stdout.splitlines() if '=' in line)
 if env.get('WAYLAND_DISPLAY'):break
 time.sleep(1)
else:raise AssertionError('Wayland did not start')
call('runuser','-u',user,'--','bash','-c',authorize,'xur','/run/xur-virtual-output-probe')
call(*base,'kbuildsycoca6','--noincremental')
call('systemd-run','--unit=xur-headless-output-probe','--collect','--property=Type=notify','--property=TimeoutStartSec=20','--property=User='+user,'--property=Slice=user-'+uid+'.slice','--setenv=XDG_RUNTIME_DIR=/run/user/'+uid,'--setenv=WAYLAND_DISPLAY='+env['WAYLAND_DISPLAY'],'/run/xur-virtual-output-probe')
for k in ('DISPLAY','WAYLAND_DISPLAY','XAUTHORITY'):
 if k in env:base.append(k+'='+env[k])
for attempt in range(60):
 r=subprocess.run(base+['kscreen-doctor','-j'],text=True,capture_output=True)
 if 'Xur-Stream' in r.stdout and subprocess.run(['pgrep','-u',uid,'-x','ksplashqml'],capture_output=True).returncode:break
 time.sleep(1)
else:raise AssertionError('Virtual desktop not ready: '+r.stdout+r.stderr)
info=json.loads(r.stdout)
assert all(p.read_text().strip()=='disconnected' for p in pathlib.Path('/sys/class/drm').glob('card*-*/status'))
kwin=run('pgrep','-u',uid,'-x','kwin_wayland');cmd=pathlib.Path('/proc/'+kwin+'/cmdline').read_bytes().replace(b'\0',b' ').decode()
assert '--drm' in cmd and '--virtual' not in cmd,cmd
call('systemd-run','--unit=xur-input-access-probe','--wait','--collect','--property=User='+user,'--property=Slice=user-'+uid+'.slice','python3','-c',"import os;os.close(os.open('/dev/uinput',os.O_RDWR))")
# Linux uinput ABI: real evdev events, the same injection path used by Sunshine.
f=os.open('/dev/uinput',os.O_WRONLY|os.O_NONBLOCK)
fcntl.ioctl(f,0x40045564,1) # EV_KEY
for key in (28,272):fcntl.ioctl(f,0x40045565,key)
fcntl.ioctl(f,0x40045564,2) # EV_REL
for axis in (0,1):fcntl.ioctl(f,0x40045566,axis)
os.write(f,struct.pack('80sHHHHI',b'Xur headless input regression',3,0x1234,1,1,0)+bytes(64*4*4))
fcntl.ioctl(f,0x5501);time.sleep(3)
def event(t,c,v):os.write(f,struct.pack('llHHi',0,0,t,c,v))
def sync():event(0,0,0)
def key(k):event(1,k,1);sync();time.sleep(.1);event(1,k,0);sync()
results=[]
try:
 for mode in ('keyboard','mouse'):
  client="""
import ctypes as c,sys
x=c.CDLL('libX11.so.6');x.XOpenDisplay.restype=c.c_void_p
x.XDefaultRootWindow.argtypes=[c.c_void_p];x.XDefaultRootWindow.restype=c.c_ulong
x.XCreateSimpleWindow.argtypes=[c.c_void_p,c.c_ulong,c.c_int,c.c_int,c.c_uint,c.c_uint,c.c_uint,c.c_ulong,c.c_ulong];x.XCreateSimpleWindow.restype=c.c_ulong
x.XStoreName.argtypes=[c.c_void_p,c.c_ulong,c.c_char_p]
x.XSelectInput.argtypes=[c.c_void_p,c.c_ulong,c.c_long];x.XMapWindow.argtypes=[c.c_void_p,c.c_ulong];x.XFlush.argtypes=[c.c_void_p];x.XNextEvent.argtypes=[c.c_void_p,c.c_void_p]
d=x.XOpenDisplay(None);assert d
w=x.XCreateSimpleWindow(d,x.XDefaultRootWindow(d),100,100,300,100,0,0,0)
x.XStoreName(d,w,b'XurInputProbe');x.XSelectInput(d,w,1|4);x.XMapWindow(d,w);x.XFlush(d)
e=c.create_string_buffer(192)
while True:
 x.XNextEvent(d,e)
 if c.c_int.from_buffer(e).value==int(sys.argv[1]):sys.exit(42)
"""
  message=subprocess.Popen(base+['python3','-c',client,'2' if mode=='keyboard' else '4'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  time.sleep(2)
  wid=run(*base,'xdotool','search','--name','XurInputProbe').splitlines()[-1]
  call(*base,'xdotool','windowactivate','--sync',wid)
  if mode=='keyboard':key(28)
  else:
   # Move into the test window through evdev, not XTest.
   geometry=dict(l.split('=',1) for l in run(*base,'xdotool','getwindowgeometry','--shell',wid).splitlines())
   target=(int(geometry['X'])+150,int(geometry['Y'])+50)
   for n in range(80):
    pos=dict(l.split('=',1) for l in run(*base,'xdotool','getmouselocation','--shell').splitlines())
    dx=target[0]-int(pos['X']);dy=target[1]-int(pos['Y'])
    if abs(dx)<3 and abs(dy)<3:break
    event(2,0,max(-40,min(40,dx)));event(2,1,max(-40,min(40,dy)));sync();time.sleep(.1)
   key(272)
  try:code=message.wait(timeout=10)
  except subprocess.TimeoutExpired:message.terminate();raise AssertionError(mode+' input failed to activate button')
  assert code==42,(mode,code)
  results.append(mode)
finally:
 fcntl.ioctl(f,0x5502);os.close(f)
print(json.dumps(dict(result='Passed',kwin=cmd,noPhysicalOutputs=True,workstationUinputAccess=True,virtualOutput=info,input=results)))
''')
 result=json.loads(observed);print(observed,flush=True)
finally:
 print(guest(common+r'''
if state.exists():
 s=json.loads(state.read_text())
 for unit in ('xur-headless-output-probe','xur-headless-probe'):subprocess.run(['systemctl','stop',unit],capture_output=True)
 subprocess.run(['loginctl','terminate-user',s['user']],capture_output=True)
 for name,value in s['files'].items():
  p=pathlib.Path(name)
  if value is None:p.unlink(missing_ok=True)
  else:p.write_bytes(base64.b64decode(value))
 for status in pathlib.Path('/sys/class/drm').glob('card*-*/status'):status.write_text('detect')
 if 'uinput' in s:os.chown('/dev/uinput',*s['uinput'][:2]);os.chmod('/dev/uinput',s['uinput'][2])
 pathlib.Path('/run/xur-virtual-output-probe').unlink(missing_ok=True)
 call('systemctl','daemon-reload')
 services=s.get('services',['xur-agent.service','xur-control.service','xur-gateway.service'])
 if services:call('systemctl','start',*services)
 state.unlink()
 print('Probe cleaned up; previously running services restored')
'''),flush=True)
a.evidence.parent.mkdir(parents=True,exist_ok=True);a.evidence.write_text(json.dumps(result,indent=2)+'\n')
