#!/usr/bin/env python3
import argparse,json,pathlib,time
from guest import execute
p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('workload');a=p.parse_args()
def run(argv):
 r=execute(a.name,argv)
 if r['code']!=0:raise RuntimeError(r['error'])
 return r['output'].strip()
old=run(['/usr/bin/cat','/proc/sys/kernel/random/boot_id'])
try:run(['/usr/bin/systemctl','reboot','--no-block'])
except (OSError,ValueError):pass
for _ in range(120):
 time.sleep(2)
 try:
  if run(['/usr/bin/cat','/proc/sys/kernel/random/boot_id'])==old:continue
  state=json.loads(run(['/usr/bin/curl','--silent','--fail','--unix-socket','/run/xur/agent.sock','http://localhost/workloads']))
  if not state['instances'] or any(i['state']!='running' for i in state['instances']):continue
  assert any(i['id']==a.workload for i in state['instances'])
  print(json.dumps({'workstationRestoredAfterReboot':True,'newBoot':True}));break
 except (OSError,RuntimeError,ValueError):pass
else:raise RuntimeError('The saved workstation did not return after reboot')
