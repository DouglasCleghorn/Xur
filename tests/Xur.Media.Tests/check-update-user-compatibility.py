#!/usr/bin/env python3
"""Read-only rejection of an older installed bundle against real workstation state."""
import json,pathlib,sys
from guest import execute

name=sys.argv[1]
old=sys.argv[2]
assert len(old)==64 and all(c in '0123456789abcdef' for c in old)
script=r'''
import http.client,json,pathlib,socket,subprocess
root=pathlib.Path('/var/lib/xur/app')
class UnixHTTP(http.client.HTTPConnection):
 def connect(self):
  self.sock=socket.socket(socket.AF_UNIX);self.sock.settimeout(10);self.sock.connect('/run/xur/agent.sock')
def workloads():
 connection=UnixHTTP('localhost',timeout=10)
 try:
  connection.request('GET','/workloads');response=connection.getresponse();assert response.status==200;return json.loads(response.read())
 finally:connection.close()
old=OLD_ID
assert (root/'releases'/old/'bundle.json').is_file()
before=workloads()
bundle=json.loads((root/'current/bundle.json').read_text())['id']
result=subprocess.run([str(root/'current/host/xurutil'),'app-update','compatibility',old],capture_output=True,text=True,timeout=30)
assert result.returncode!=0 and ('workstation users' in result.stderr or 'username and password' in result.stderr),result
assert json.loads((root/'current/bundle.json').read_text())['id']==bundle
after=workloads()
assert {i['id']:i['pid'] for i in before['instances']}=={i['id']:i['pid'] for i in after['instances']}
assert not (root/'maintenance').exists() and not (root/'transaction.json').exists()
print(json.dumps({'suite':'InstalledUserUpdateCompatibility','result':'Passed','bundle':bundle,'olderBundle':old,'realSavedStateInspected':True,'workloadPidsPreserved':True,'noMaintenanceOrServiceMutation':True}))
'''.replace('OLD_ID',repr(old))
result=execute(name,['python3','-c',script])
assert result['code']==0,result.get('error','Compatibility test failed')
receipt=json.loads(result['output'])
pathlib.Path('.build/evidence/updates/user-update-compatibility.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
