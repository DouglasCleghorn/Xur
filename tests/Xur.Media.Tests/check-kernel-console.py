#!/usr/bin/env python3
"""Force kernel output at maximum verbosity; VT3 must never receive it."""
import argparse,json,pathlib
from guest import execute
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
media=json.loads((vm/'vm-manifest.json').read_text());assert media.get('testTransport')
script=r'''
import fcntl,json,os,pathlib,subprocess,time
terminal=os.open('/dev/tty3',os.O_WRONLY|os.O_NOCTTY)
def redirect():
 payload=bytearray([17]);fcntl.ioctl(terminal,0x541c,payload);return payload[0]
subprocess.run(['chvt','3'],check=True)
# Reproduce another tool changing both kernel redirection and verbosity.
fcntl.ioctl(terminal,0x541c,bytearray([11,0]))
pathlib.Path('/proc/sys/kernel/printk').write_text('8')
time.sleep(1)
assert redirect()==1,'Kernel messages are still routed to the active VT'
marker=b'XUR-KERNEL-CONSOLE-REGRESSION'
with open('/dev/kmsg','wb',buffering=0) as kernel:
 for n in range(100):
  pathlib.Path('/proc/sys/kernel/printk').write_text('8')
  kernel.write(b'<0>'+marker+b' '+str(n).encode()+b'\n')
  time.sleep(.01)
  assert marker not in pathlib.Path('/dev/vcs3').read_bytes(),'Kernel output entered the menu VT'
assert marker.decode() in subprocess.check_output(['dmesg'],text=True),'Kernel messages were discarded instead of retained'
time.sleep(1)
assert redirect()==1
assert int(pathlib.Path('/proc/sys/kernel/printk').read_text().split()[0])<=1
os.close(terminal)
print(json.dumps({'kernelMessagesGenerated':100,'menuVtUncontaminated':True,'kernelRingRetained':True,'redirectionRestoredAfterExternalReset':True}))
'''
result=execute(a.name,['/usr/bin/python3','-c',script]);assert result['code']==0,result['error']
receipt={'suite':'KernelConsole','result':'Passed',**json.loads(result['output']),'media':media}
(repo/'.build/evidence/kernel-console.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
