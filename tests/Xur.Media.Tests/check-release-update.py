#!/usr/bin/env python3
"""Install the exact staged signed archive; preserve running workloads and login."""
import ssl,argparse,functools,http.server,importlib.util,json,pathlib,sys,threading,time,urllib.request
from guest import execute
from test_auth import api_session
p=argparse.ArgumentParser();p.add_argument('--name',required=True);p.add_argument('--evidence',type=pathlib.Path,required=True);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
assert json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm')
token=api_session(vm)
def api(path,body=None):
 req=urllib.request.Request('https://127.0.0.1:18443'+path,data=None if body is None else json.dumps(body).encode(),headers={'Authorization':'Bearer '+token,'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=120,context=ssl._create_unverified_context()) as r:return json.loads(r.read() or 'null')
def guest(args):
 r=execute(a.name,args);assert r['code']==0,r['error'];return r['output']
def identities():
 return {i['id']:{k:i[k] for k in ('pid','instanceId','bootId','fingerprint','state')} for i in api('/api/profiles')['runtime']['instances']}
def stream_pids():
 return guest(['python3','-c',"import subprocess,json\nu=json.loads(subprocess.check_output(['systemctl','list-units','--type=service','--state=running','--output=json','xur-stream-*']))\nprint(json.dumps({x['unit']:subprocess.check_output(['systemctl','show',x['unit'],'--property=MainPID','--value']).decode().strip() for x in u},sort_keys=True))"]).strip()
def finish(old):
 for _ in range(300):
  try:
   s=api('/api/application-updates');o=s['operation']
   if not s['busy'] and o and o['id']!=old and o['stage'] in ('Complete','Failed','RolledBack'):
    assert o['stage']=='Complete',o;return s
  except (OSError,ValueError,KeyError):pass
  time.sleep(1)
 raise AssertionError('Update did not finish')
stage=repo/'.build/update-staging';expected=(stage/'latest').read_text().strip()
entry=json.loads((stage/(expected+'.json')).read_text())
assert json.loads((repo/'.build/context/rootfs/usr/share/xur/app-bundle/bundle.json').read_text())['id']==expected
spec=importlib.util.spec_from_file_location('repository',repo/'eng/update-repository.py');r=importlib.util.module_from_spec(spec);spec.loader.exec_module(r)
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(r.Handler,directory=stage));threading.Thread(target=server.serve_forever,daemon=True).start()
# A retried pipeline must exercise activation rather than pass an already-installed no-op.
initial=api('/api/application-updates')
if initial['current']['id']==expected:
 assert initial.get('previous') and initial['previous']['id']!=expected,'A different prior test release is required'
 old=initial['operation']['id'] if initial['operation'] else None
 api('/api/application-updates',{'action':'rollback'});finish(old)
before=identities();streams=stream_pids()
previous=api('/api/application-updates')['current']['id'];assert previous!=expected
# Restore the VM's repository setting after the loopback-only staging server closes.
config=guest(['python3','-c',"import pathlib,json\np=pathlib.Path('/etc/xur/application-updates.json');print(json.dumps(p.read_text() if p.exists() else None))"])
try:
 api('/api/application-updates',{'action':'development','development':True})
 api('/api/application-updates',{'action':'configure','server':f'10.71.1.2:{server.server_port}'})
 s=api('/api/application-updates');old=s['operation']['id'] if s['operation'] else None
 api('/api/application-updates',{'action':'check'});s=finish(old);assert s['available']['id']==expected
 api('/api/application-updates',{'action':'update'});s=finish(s['operation']['id']);assert s['current']['id']==expected
 assert before==identities(),'Workload identity changed during app update'
 assert streams==stream_pids(),'Sunshine restarted during app update'
 report=api('/api/diagnostics/display');assert report['agentBundle']==report['controlBundle']==expected
 for name in ('nvidiaGpuMapping','nvidiaTopology','nvidiaNvlinkStatus'):
  probe=report['commands'][name];assert probe['command']=='nvidia-smi' and 'exitCode' in probe
 assert token not in json.dumps(report)
 result={'result':'Passed','version':entry['version'],'bundleId':expected,'signedUpdate':True,'previousBundleId':previous,'realActivation':True,'preservedWorkloadCount':len(before),'sameWorkloadInstances':True,'sameSunshinePids':True,'sameAuthentication':True,'diagnosticsProbesPresent':True,'physicalNvlinkTested':False}
 a.evidence.mkdir(parents=True,exist_ok=True);(a.evidence/'signed-update.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
finally:
 saved=json.loads(config)
 guest(['python3','-c',"import pathlib\np=pathlib.Path('/etc/xur/application-updates.json')\n"+(f'p.write_text({saved!r})' if saved is not None else 'p.unlink(missing_ok=True)')])
 server.shutdown();server.server_close()
