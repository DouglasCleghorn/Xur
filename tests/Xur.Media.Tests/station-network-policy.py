#!/usr/bin/env python3
"""Check the compiled managed-user rule against polkit in a disposable development VM."""
import argparse,importlib.util,json,pathlib
root=pathlib.Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser();parser.add_argument("--name",required=True);args=parser.parse_args()
name=args.name
assert name and all(c in "abcdefghijklmnopqrstuvwxyz0123456789-" for c in name)
spec=importlib.util.spec_from_file_location('guest',root/'tests/Xur.Media.Tests/guest.py');guest=importlib.util.module_from_spec(spec);spec.loader.exec_module(guest)
assert json.loads((root/'.build/vms'/name/'vm-manifest.json').read_text())['developmentVm']
rule=(root/'.build/fast/station-network-test.rules').read_text().replace('"xurtest"','"xurpolkitprobe"')
probe='''import os,json,gi
gi.require_version("Polkit","1.0")
from gi.repository import Polkit
a=Polkit.Authority.get_sync(None)
s=Polkit.UnixProcess.new_for_owner(os.getpid(),0,os.getuid())
out={}
for action in ["org.freedesktop.NetworkManager.network-control","org.freedesktop.NetworkManager.settings.modify.system"]:
 r=a.check_authorization_sync(s,action,None,Polkit.CheckAuthorizationFlags.NONE,None)
 out[action]=[r.get_is_authorized(),r.get_is_challenge()]
print(json.dumps(out))
'''
script='''import pathlib,subprocess,time,json
user="xurpolkitprobe"
path=pathlib.Path("/etc/polkit-1/rules.d/00-xur-workstation-network-probe.rules")
assert not path.exists()
assert subprocess.run(["id","-u",user],capture_output=True).returncode!=0
subprocess.run(["useradd","--no-create-home","--shell","/usr/sbin/nologin",user],check=True)
def read():
 r=subprocess.run(["runuser","-u",user,"--","python3","-c",PROBE],capture_output=True,text=True)
 assert r.returncode==0,r.stderr
 return json.loads(r.stdout)
try:
 before=read()
 path.write_text(RULE);path.chmod(0o644)
 deadline=time.monotonic()+10
 while True:
  after=read()
  if after["org.freedesktop.NetworkManager.network-control"]==[False,False]:break
  assert time.monotonic()<deadline,after
  time.sleep(.2)
 assert before["org.freedesktop.NetworkManager.network-control"]==[False,True],before
 assert after["org.freedesktop.NetworkManager.settings.modify.system"]==before["org.freedesktop.NetworkManager.settings.modify.system"]
 print(json.dumps({"result":"Passed","before":before,"after":after}))
finally:
 path.unlink(missing_ok=True)
 subprocess.run(["userdel",user],check=True)
'''
r=guest.execute(name,['/usr/bin/python3','-c','RULE='+repr(rule)+'\nPROBE='+repr(probe)+'\n'+script])
print(json.dumps(r));assert r['code']==0
(root/'.build/fast/station-network-vm.json').write_text(r['output'])
