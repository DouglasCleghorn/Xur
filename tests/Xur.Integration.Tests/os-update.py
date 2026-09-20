#!/usr/bin/env python3
"""Exercise same-channel staging without modifying host deployments."""
import runpy,io,subprocess
from unittest.mock import patch
m=runpy.run_path('os/bootc/os-update',run_name='test_os_update');stage=m['stage'];verify=m['verify_stage'];channel=m['CHANNEL']
def deployment(digest):return {'image':{'version':digest,'imageDigest':digest,'image':{'image':channel}}}
for switched in (False,True):
 calls=[]
 def run(args,**kwargs):calls.append(args)
 state={'status':{'booted':deployment('old'),'staged':deployment('new') if switched else None}}
 with patch.dict(stage.__globals__,boot_status=lambda:state),patch.object(subprocess,'run',run):stage(io.StringIO())
 assert calls[0]==['bootc','switch','--enforce-container-sigpolicy',channel]
 assert calls[1:]==([] if switched else [['bootc','upgrade']])
state={'current':{'digest':'old'},'available':{'digest':'new'},'pending':None}
try:verify(state)
except RuntimeError as e:assert 'no deployment was staged' in str(e)
else:raise AssertionError('A successful command without a deployment must not report success')
verify({**state,'pending':{'image':channel,'digest':'new'}})
verify({**state,'available':{'digest':'old'}})
with patch.object(subprocess,'run',side_effect=subprocess.CalledProcessError(1,['bootc','switch'])):
 try:stage(io.StringIO())
 except subprocess.CalledProcessError:pass
 else:raise AssertionError('Signature failures must stop staging')
print('OS staging checks passed: same-channel upgrade, signature failure, staged and current deployments, no-op rejection')
