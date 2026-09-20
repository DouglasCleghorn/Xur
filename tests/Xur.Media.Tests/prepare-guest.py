#!/usr/bin/env python3
"""Add a root test transport to an already-tested disposable installed VM.

The released ISO is unchanged. An extra GRUB boot opens a local debug console;
that console configures a test copy of QEMU's agent, confined only by the VM
boundary. SELinux remains enforcing for the product and all other services.
Never use this test harness on a physical machine or an operator installation.
"""
import argparse,json,pathlib,re,socket,subprocess,time,urllib.request
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args()
assert re.fullmatch('[a-z0-9-]+',a.name)
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
media=json.loads((vm/'vm-manifest.json').read_text())
assert media['firmware']=='UEFI OVMF' and (vm/'target.raw').is_file()
sock=socket.socket(socket.AF_UNIX);sock.settimeout(120);sock.connect(str(vm/'qmp.sock'))
stream=sock.makefile('rwb',buffering=0);json.loads(stream.readline())
def qmp(command,args=None):
    stream.write((json.dumps({'execute':command,'arguments':args or {}})+'\n').encode())
    while True:
        result=json.loads(stream.readline())
        if 'error' in result:raise RuntimeError(result['error'])
        if 'return' in result:return result['return']
qmp('qmp_capabilities')
def key(*names):
    qmp('send-key',{'keys':[{'type':'qcode','data':name} for name in names],'hold-time':20});time.sleep(.08)
def type_text(text):
    mapping={' ':'spc','\n':'ret','/':'slash','-':'minus','.':'dot','=':'equal',':':'semicolon','_':'minus','>':'dot','[':'bracket_left',']':'bracket_right'}
    for char in text:
        keys=[mapping.get(char,char.lower())]
        if char.isupper() or char in ':_>':keys.insert(0,'shift')
        key(*keys)
# Gracefully reboot using the real authenticated product API. Watch QMP's RESET
# event before sending keys, so shutdown time cannot make us miss GRUB.
from test_auth import api_session
def api(path,data,token=None):
    headers={'Content-Type':'application/json'}
    if token:headers['Authorization']='Bearer '+token
    with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:18081'+path,data=json.dumps(data).encode(),headers=headers),timeout=10) as response:
        return response.status,response.read()
token=api_session(vm)
assert api('/api/power/reboot',{},token)[0]==202
while json.loads(stream.readline()).get('event')!='RESET':pass
for _ in range(12):time.sleep(.25);key('esc')
type_text('normal\n');key('e');time.sleep(1)
key('ctrl','home')
for _ in range(3):key('down')
key('end');type_text(' systemd.debug_shell=1');key('ctrl','x')
from guest import execute
for _ in range(90):
    time.sleep(2)
    try:
        if 'systemd.debug_shell=1' in execute(a.name,['/usr/bin/cat','/proc/cmdline'])['output']:break
    except (OSError,RuntimeError,ValueError):pass
else:raise RuntimeError('Test console boot did not become ready')
key('alt','f9')
# Use echo lines, avoiding terminal keymap-dependent backslash escapes.
directory='/etc/systemd/system/qemu-guest-agent.service.d'
config=directory+'/xur-test.conf'
type_text('mkdir -p /var/lib/xur-test '+directory+'\n')
type_text('cp --remove-destination /usr/bin/qemu-ga /var/lib/xur-test/qemu-ga\n')
type_text('chcon -t bin_t /var/lib/xur-test/qemu-ga\n')
for index,line in enumerate(['[Service]','SELinuxContext=system_u:system_r:unconfined_service_t:s0','ExecStart=','ExecStart=/var/lib/xur-test/qemu-ga --method=virtio-serial --path=/dev/virtio-ports/org.qemu.guest_agent.0']):
    type_text('echo '+line+(' > ' if index==0 else ' >> ')+config+'\n')
type_text('systemctl daemon-reload\nsystemctl restart qemu-guest-agent\n')
for _ in range(30):
    time.sleep(1)
    try:
        if 'unconfined_service_t' in execute(a.name,['/usr/bin/id','-Z'])['output']:break
    except (OSError,RuntimeError,ValueError):pass
else:raise RuntimeError('Root test transport did not start')
assert execute(a.name,['/usr/sbin/getenforce'])['output'].strip()=='Enforcing'
execute(a.name,['/usr/bin/chvt','3'])
media['testTransport']={'type':'Local QEMU guest agent','testOnlyServiceDomain':'unconfined_service_t','productSelinux':'Enforcing','setup':'Extra GRUB debug-console boot after normal install/reboot assertions','isoModified':False}
(vm/'vm-manifest.json').write_text(json.dumps(media,indent=2)+'\n')
print(json.dumps({'testTransportReady':True,'productSelinux':'Enforcing','isoModified':False}))
