#!/usr/bin/env python3
import json,pathlib,re,sys,time,urllib.request
from guest import execute
name=sys.argv[1];path=pathlib.Path('.build/vms')/name;raw=(path/'session.private.cookies').read_text();token=re.search(r'Set-Cookie3: xur.session=([^;]+)',raw).group(1).strip('"');base='http://127.0.0.1:18081'
def request(path):
 with urllib.request.urlopen(urllib.request.Request(base+path,headers={'Authorization':'Bearer '+token}),timeout=5) as r:return json.load(r)
before=request('/api/status')['bootId'];identity=request('/api/application-updates')['current']['id']
result=execute(name,['systemd-run','--on-active=2s','--unit=xur-account-reboot-test','systemctl','reboot']);assert result['code']==0
for _ in range(180):
 try:
  status=request('/api/status')
  if status['bootId']!=before and request('/api/application-updates')['current']['id']==identity:break
 except (OSError,ValueError):pass
 time.sleep(1)
else:raise AssertionError('Manager cookie did not survive reboot')
result=execute(name,['/var/lib/xur/app/current/control/Xur.Control','login','show']);assert result['code']==0 and 'Access code:' not in result['output'] and 'owner' in result['output']
credentials=json.loads((path/'account.private.json').read_text())
with urllib.request.urlopen(urllib.request.Request(base+'/api/auth/login',data=json.dumps(credentials).encode(),headers={'Content-Type':'application/json'})) as r:assert r.status==200
receipt={'suite':'AccountReboot','result':'Passed','bundle':identity,'actualVmReboot':True,'managerSessionSurvives':True,'passwordLoginSurvives':True,'consoleTokenAbsent':True}
pathlib.Path('.build/evidence/updates/account-reboot.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
