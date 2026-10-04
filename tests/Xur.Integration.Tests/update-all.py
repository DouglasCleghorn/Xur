#!/usr/bin/env python3
import copy,runpy,tempfile,pathlib,json
module=runpy.run_path('os/bootc/update-all',run_name='test_update_all')
execute=module['execute']
def scenario(fail_os=False,pending=False,server=True,current=False,no_stage=False):
 calls=[];receipts=[];staged=False
 def invoke(tool,action):
  nonlocal staged
  calls.append((tool,action))
  if tool=='os-update' and action=='stage' and not no_stage:staged=True
  if tool=='os-update' and fail_os:raise RuntimeError('Offline')
  if action=='status':
   key='digest' if tool=='os-update' else 'id'
   return dict(busy=False,pending={'version':'next'} if pending or staged else None,server='server' if server else '',current={key:'old'},available={key:'old' if current else 'new'})
 result=execute(invoke,lambda x:receipts.append(copy.deepcopy(x)))
 return calls,result,receipts
calls,result,receipts=scenario()
assert calls.index(('os-update','stage'))<calls.index(('app-update','update'))
assert result['stage']=='Complete' and len(result['results'])==2
assert all(action not in ('reboot','rollback') for _,action in calls)
calls,result,_=scenario(fail_os=True)
assert ('app-update','update') in calls and result['stage']=='Failed'
calls,result,_=scenario(pending=True,server=False)
assert calls==[('os-update','status'),('app-update','status')]
assert all(r['stage']=='Skipped' for r in result['results'])
calls,result,_=scenario(current=True)
assert not any(action in ('update','stage') for _,action in calls)
assert receipts[0]['stage']=='Running' and receipts[-1]['stage']=='Complete'
# An unheld durable Running receipt is reported as interrupted after restart.
with tempfile.TemporaryDirectory() as root:
 status=module['status'];status.__globals__['STATE']=pathlib.Path(root)
 (pathlib.Path(root)/'operation.json').write_text(json.dumps(receipts[0]))
 assert status()['operation']['stage']=='Interrupted'
print(json.dumps(dict(suite='UpdateAll',result='Passed',osBeforeApplication=True,failureIsolation=True,noReboot=True,skipQueuedOrUnconfigured=True,noOpWhenCurrent=True,durableInterruption=True)))

calls,result,_=scenario(no_stage=True)
assert result['stage']=='Failed' and result['results'][0]['stage']=='Failed'
assert ('app-update','update') in calls

# New bundles dispatch application updates directly to the native command.
from unittest.mock import patch
import subprocess
Path=pathlib.Path
evidence=Path('.build/evidence/xurutil');evidence.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=evidence) as directory:
 host=Path(directory);invoke=module['call'];invoke.__globals__['HOST']=host
 def checked(argv,**kwargs):
  assert kwargs['timeout']==3700 and kwargs['capture_output'] and kwargs['text']
  expected=[str(host/'xurutil'),'app-update','status'] if (host/'xurutil').exists() else ['/usr/bin/python3',str(host/'app-update'),'status']
  assert argv==expected
  return subprocess.CompletedProcess(argv,0,'{"busy":false}','')
 with patch('subprocess.run',checked):
  assert invoke('app-update','status')=={'busy':False}
  (host/'xurutil').touch()
  assert invoke('app-update','status')=={'busy':False}
