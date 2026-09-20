#!/usr/bin/env python3
"""Real systemd/API updates of an installed VM; test-only releases stay private."""
import argparse,functools,hashlib,os,http.cookiejar,http.server,json,pathlib,re,shutil,subprocess,sys,tarfile,tempfile,threading,time,urllib.error,urllib.request
p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('--bootstrap',action='store_true');p.add_argument('--station',action='store_true');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];sys.path.insert(0,str(repo/'tests/Xur.Media.Tests'));from guest import execute
vm=repo/'.build/vms'/a.name;base='http://127.0.0.1:18081'
jar=http.cookiejar.LWPCookieJar(str(vm/'session.private.cookies'));jar.load(ignore_discard=True,ignore_expires=True);token=next(c.value for c in jar if c.name=='xur.session')
def call(path,data=None,auth=True):
 req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if auth else {})})
 try:
  with urllib.request.urlopen(req,timeout=30) as r:return r.status,json.loads(r.read() or 'null')
 except urllib.error.HTTPError as e:return e.code,e.read().decode()
def guest(script):
 r=execute(a.name,['/usr/bin/python3','-c',script]);assert r['code']==0,r['error'];return r['output']
if a.bootstrap:
 assert json.loads((vm/'vm-manifest.json').read_text()).get('developmentVm')
 key=(repo/'os/bootc/application-update-key.pem').read_text();unit=(repo/'os/bootc/systemd/xur-app-recovery.service').read_text();updater=(repo/'os/bootc/app-update').read_text()
 guest('import pathlib,subprocess\n'+f"pathlib.Path('/etc/xur/application-update-key.pem').write_text({key!r})\npathlib.Path('/etc/systemd/system/xur-app-recovery.service').write_text({unit!r})\npathlib.Path('/var/lib/xur/updater').mkdir(exist_ok=True)\npathlib.Path('/var/lib/xur/updater/app-update').write_text({updater!r})\n"+"subprocess.run(['systemctl','daemon-reload'],check=True)\nsubprocess.run(['systemctl','enable','xur-app-recovery.service'],check=True)")
assert call('/api/application-updates',auth=False)[0]==401
assert call('/api/application-updates',{'action':'update'},False)[0]==401
assert call('/api/application-updates',{'action':'development','development':True})[0]==202
assert call('/api/application-updates',{'action':'configure','server':'192.168.0.134'})[0]==202
assert call('/api/application-updates',{'action':'check'})[0]==202

def finish(previous=None):
 for _ in range(420):
  try:
   code,s=call('/api/application-updates')
   if code==200 and not s['busy'] and s['operation'] and s['operation']['id']!=previous and s['operation']['stage'] in ('Complete','Failed','RolledBack'):return s
  except (OSError,ValueError):pass
  time.sleep(1)
 raise AssertionError('Update operation did not finish')
assert finish()['operation']['stage']=='Complete'
original=call('/api/application-updates')[1]['current']['id']
# Preserve representative existing profile, cache, home, login key and real model process.
status,profiles=call('/api/profiles');assert status==200
recipes=call('/api/recipes')[1];recipe=next(r for r in recipes if r['id']=='smollm2-135m-cpu')
id='update-test';existing=next((p for p in profiles['profiles'] if p['id']==id),None)
workload={'id':'update-model','name':'Update test model','recipe':recipe,'gpus':[],'route':'update-model'}
selected=[workload]
if a.station:
 gpu=next(g for g in profiles['runtime']['gpus'] if g.get('cards') and g.get('displays'))
 station=next(r for r in recipes if r['id']=='gaming-workstation')
 selected.append({'id':'update-station','name':'Update workstation','recipe':station,'gpus':[gpu['pci']],'route':'update-station'})
assert call('/api/profiles',{'id':id,'name':'Update test','revision':existing['revision'] if existing else 0,'workloads':selected})[0]==200
plan=call('/api/profiles/'+id+'/preview',{})[1];assert call('/api/profiles/apply',{'id':plan['id'],'digest':plan['digest']})[0]==200
for _ in range(600):
 profiles=call('/api/profiles')[1]
 if profiles['operation']['stage']=='Complete':break
 assert profiles['operation']['stage']!='Failed',profiles['operation'];time.sleep(1)
else:raise AssertionError('Model startup timed out')
instance=next(i for i in profiles['runtime']['instances'] if i['id']=='update-model')
station_instance=next((i for i in profiles['runtime']['instances'] if i['id']=='update-station'),None)
def desktop():return guest("import pathlib,subprocess,json\nprint(json.dumps({'vt':pathlib.Path('/sys/class/tty/tty0/active').read_text(),'kwin':subprocess.check_output(['pgrep','-x','kwin_wayland']).decode().strip()}))")
station_desktop=desktop() if a.station else None
files=guest("import pathlib,hashlib,json\np=pathlib.Path('/var/home/xur-update-preservation');p.mkdir(exist_ok=True);(p/'saved-data').write_bytes(b'Xur update preservation\\0'*1000)\npaths=[p/'saved-data',*pathlib.Path('/var/lib/xur').glob('*key*')]\nprint(json.dumps({str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in paths if p.is_file()}))")
class Quiet(http.server.SimpleHTTPRequestHandler):
 def log_message(self,*args):pass
results=[]
with tempfile.TemporaryDirectory(prefix='update-test-',dir=repo/'.build') as tmp:
 directory=pathlib.Path(tmp);public=directory/'public';public.mkdir();bundle=directory/'bundle';shutil.copytree(repo/'.build/context/rootfs/usr/share/xur/app-bundle',bundle)
 server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Quiet,directory=public));threading.Thread(target=server.serve_forever,daemon=True).start()
 assert call('/api/application-updates',{'action':'configure','server':f'10.71.1.2:{server.server_port}'})[0]==202
 def publish(label,broken=False,tamper=False):
  (bundle/'test-version.txt').write_text(label)
  apphost=bundle/'control/Xur.Control';saved=apphost.read_bytes()
  if broken:apphost.write_bytes(b'#!/bin/sh\nexit 23\n')
  files={str(p.relative_to(bundle)):hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in sorted(bundle.rglob('*')) if p.is_file() and p.name!='bundle.json'}
  identity=hashlib.sha256(json.dumps(files,sort_keys=True).encode()).hexdigest();meta={'schema':1,'hostAbi':1,'id':identity,'version':label,'files':files};(bundle/'bundle.json').write_text(json.dumps(meta))
  archive=public/(identity+'.tar.gz')
  with tarfile.open(archive,'w:gz',compresslevel=1,dereference=True) as tar:tar.add(bundle,arcname='.')
  apphost.write_bytes(saved)
  entry={'schema':1,'hostAbi':1,'dataSchema':1,'id':identity,'version':label,'sequence':int(time.time()),'file':archive.name,'bytes':archive.stat().st_size,'sha256':hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()}
  descriptor=public/(identity+'.json');descriptor.write_text(json.dumps(entry));subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(pathlib.Path(os.environ.get('XUR_UPDATE_SIGNING_KEY',pathlib.Path.home()/'.local/share/xur-updates/signing-key.pem'))),'-rawin','-in',str(descriptor),'-out',str(descriptor)+'.sig'],check=True)
  (public/'latest').write_text(identity)
  if tamper:descriptor.write_text(descriptor.read_text()+' ')
  return identity
 def update():
  previous=call('/api/application-updates')[1]['operation']['id'];assert call('/api/application-updates',{'action':'update'})[0]==202;return previous
 def preserved():
  s=call('/api/profiles')[1];now=next(i for i in s['runtime']['instances'] if i['id']=='update-model')
  assert all(now[k]==instance[k] for k in ('pid','instanceId','bootId','fingerprint')),now
  assert s['active']==profiles['active']
  if a.station:
   now_station=next(i for i in s['runtime']['instances'] if i['id']=='update-station')
   assert all(now_station[k]==station_instance[k] for k in ('pid','instanceId','bootId','fingerprint'))
   assert desktop()==station_desktop,'Workstation renderer or foreground VT changed'
  guest('import hashlib,json,pathlib\n'+f'files=json.loads({files!r})\n'+"assert all(hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()==digest for p,digest in files.items())")
  # Reuse the pre-update browser cookie, not just a new access token.
  browser=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar));assert browser.open(base+'/api/profiles').status==200
 first=publish('Update test one')
 started=threading.Event();done=threading.Event();chunks=[];errors=[]
 def stream():
  try:
   req=urllib.request.Request(base+'/inference/update-model/completion',data=json.dumps({'prompt':'Once upon a time','n_predict':4096,'ignore_eos':True,'stream':True}).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+token})
   with urllib.request.urlopen(req,timeout=300) as response:
    for line in response:
     if line.startswith(b'data: '):chunks.append(json.loads(line[6:]));started.set()
  except Exception as e:errors.append(type(e).__name__)
  finally:done.set()
 threading.Thread(target=stream,daemon=True).start();assert started.wait(60)
 previous=update();observed_drain=False
 for _ in range(300):
  s=call('/api/application-updates')[1]
  if s['operation']['stage']=='Draining':observed_drain=True;break
  time.sleep(.2)
 assert observed_drain and not done.is_set(),'No active streaming request at drain'
 assert call('/api/profiles/create',{})[0]==503
 assert done.wait(300) and not errors and chunks[-1].get('stop') is True,errors
 s=finish(previous);assert s['operation']['stage']=='Complete' and s['current']['id']==first,s;preserved();results.append('StreamingDrainAndFirstUpdate')
 second=publish('Update test two');s=finish(update());assert s['operation']['stage']=='Complete' and s['current']['id']==second,s;preserved();results.append('SecondUpdate')
 publish('Tampered metadata',tamper=True);s=finish(update());assert s['operation']['stage']=='Failed' and s['current']['id']==second,s;preserved();results.append('TamperedSignatureRejected')
 bad=publish('Broken application',broken=True);s=finish(update());assert s['operation']['stage']=='RolledBack' and s['current']['id']==second,s;preserved();results.append('FailedHealthRollback')
 old=s['operation']['id'];assert call('/api/application-updates',{'action':'rollback'})[0]==202;s=finish(old);assert s['current']['id']==first,s;preserved();results.append('ExplicitRollback')
 # Interrupt a real activation after its durable transaction is on disk, then
 # reboot the VM with the bad release selected. The independent recovery unit
 # must restore the previous release without relying on the manager.
 update()
 for _ in range(120):
  selected=guest("import pathlib,json\np=pathlib.Path('/var/lib/xur/app/transaction.json');print(json.dumps({'transaction':p.exists(),'current':json.loads(pathlib.Path('/var/lib/xur/app/current/bundle.json').read_text())['id']}))")
  observed=json.loads(selected)
  if observed['transaction'] and observed['current']==bad:break
  time.sleep(.5)
 else:raise AssertionError('Interrupted activation did not reach the target release')
 guest("import subprocess\nsubprocess.run(['systemctl','kill','--kill-whom=main','--signal=KILL','xur-app-update.service'],check=True)")
 before_boot=instance['bootId']
 try:execute(a.name,['/usr/bin/systemctl','reboot'])
 except (OSError,RuntimeError):pass
 for _ in range(180):
  try:
   code,s=call('/api/application-updates')
   code2,state=call('/api/profiles')
   if code==200 and s['operation']['stage']=='RolledBack' and code2==200:
    restored=next((i for i in state['runtime']['instances'] if i['id']=='update-model' and i['state']=='running' and i['bootId']!=before_boot),None)
    if restored:break
  except (OSError,ValueError):pass
  time.sleep(1)
 else:raise AssertionError('Boot recovery failed')
 assert s['current']['id']==first and restored['instanceId']==instance['instanceId']
 instance=restored
 if a.station:
  for _ in range(90):
   station_state=call('/api/profiles')[1]
   station_instance=next((i for i in station_state['runtime']['instances'] if i['id']=='update-station' and i['state']=='running'),None)
   if station_instance:
    try:station_desktop=desktop();break
    except AssertionError:pass
   time.sleep(1)
  else:raise AssertionError('Workstation did not recover after reboot')
 preserved();results.append('InterruptedActivationBootRecovery')

 # Restore actual published production files through the same signed API path.
 shutil.rmtree(bundle);shutil.copytree(repo/'.build/context/rootfs/usr/share/xur/app-bundle',bundle)
 (bundle/'test-version.txt').unlink(missing_ok=True)
 # Publish helper adds a test marker, so restore from the LAN production repository separately below.
 server.shutdown();server.server_close()
assert call('/api/application-updates',{'action':'development','development':True})[0]==202
assert call('/api/application-updates',{'action':'configure','server':'192.168.0.134'})[0]==202
# Local repository sequence can predate the test releases. Preserve anti-replay
# and publish a fresh descriptor rather than bypassing the client boundary.
subprocess.run(['python3','eng/update-repository.py','publish','--version','2026.09.13'],cwd=repo,check=True,stdout=subprocess.DEVNULL)
s=finish(update());assert s['operation']['stage']=='Complete',s;preserved();results.append('ProductionRepositoryRestore')
receipt={'suite':'ApplicationUpdates','result':'Passed','vm':a.name,'results':results,'activeStreamingChunks':len(chunks),'workloadPid':instance['pid'],'preservedBrowserSession':True,'workstationPidAndRendererAndForegroundPreserved':a.station,'repository':'http://192.168.0.134:8088','finalBundle':s['current']['id'],'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/application-updates.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
