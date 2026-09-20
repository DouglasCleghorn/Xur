#!/usr/bin/env python3
"""Read-only rejection of an older installed bundle against real workstation state."""
import json,pathlib,sys
from guest import execute

name=sys.argv[1]
old=sys.argv[2]
assert len(old)==64 and all(c in '0123456789abcdef' for c in old)
script=r'''
import importlib.machinery,importlib.util,json,pathlib
root=pathlib.Path('/var/lib/xur/app')
loader=importlib.machinery.SourceFileLoader('updater',str(root/'current/host/app-update'))
spec=importlib.util.spec_from_loader(loader.name,loader);u=importlib.util.module_from_spec(spec);loader.exec_module(u)
old=OLD_ID
assert (root/'releases'/old/'bundle.json').is_file()
before=u.local('/workloads','/run/xur/agent.sock')
bundle=u.current()['id']
def no_mutation(*args):raise AssertionError('Compatibility rejection attempted mutation')
u.progress=no_mutation
try:u.activate({'id':old})
except ValueError as error:assert 'workstation users' in str(error) or 'username and password' in str(error)
else:raise AssertionError('Old bundle accepted named-user state')
assert u.current()['id']==bundle
after=u.local('/workloads','/run/xur/agent.sock')
assert {i['id']:i['pid'] for i in before['instances']}=={i['id']:i['pid'] for i in after['instances']}
assert not u.BLOCK.exists() and not u.TX.exists()
print(json.dumps({'suite':'InstalledUserUpdateCompatibility','result':'Passed','bundle':bundle,'olderBundle':old,'realSavedStateInspected':True,'workloadPidsPreserved':True,'noMaintenanceOrServiceMutation':True}))
'''.replace('OLD_ID',repr(old))
result=execute(name,['python3','-c',script])
assert result['code']==0,result.get('error','Compatibility test failed')
receipt=json.loads(result['output'])
pathlib.Path('.build/evidence/updates/user-update-compatibility.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
