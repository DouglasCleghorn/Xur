#!/usr/bin/env python3
"""Update only the app in a reusable, disposable installed QEMU VM.

Create once with --clone-from an installed media-test VM prepared by
prepare-guest.py. Subsequent runs keep the OS, model cache and test state.
No physical machine, release ISO or release artifact is changed.
"""
import argparse,fcntl,hashlib,http.server,http.cookiejar,json,os,pathlib,re,secrets,shutil,subprocess,sys,tarfile,tempfile,threading,time,urllib.parse,urllib.request
repo=pathlib.Path(__file__).resolve().parents[1];os.chdir(repo)
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--name',required=True);p.add_argument('--clone-from');p.add_argument('--skip-publish',action='store_true')
p.add_argument('--iso',type=pathlib.Path,default=repo/'dist/xur-installer-x86_64.iso')
a=p.parse_args();started=time.monotonic();os.umask(0o077)
assert re.fullmatch('[a-z0-9-]+',a.name)
root=repo/'.build/vms';vm=root/a.name
sys.path.insert(0,str(repo/'tests/Xur.Media.Tests'))
from guest import execute

def running(directory):
 try:os.kill(int((directory/'qemu.pid').read_text()),0);return True
 except (FileNotFoundError,ProcessLookupError):return False

if a.clone_from:
 assert re.fullmatch('[a-z0-9-]+',a.clone_from) and not vm.exists()
 source=root/a.clone_from;manifest=json.loads((source/'vm-manifest.json').read_text())
 assert not running(source),'Stop the source VM before cloning'
 assert manifest.get('testTransport',{}).get('isoModified') is False,'Prepare the installed source with prepare-guest.py first'
 vm.mkdir()
 for name in ('target.raw','data.raw','data-before.sha256','OVMF_VARS.fd'):
  subprocess.run(['cp','--reflink=auto','--sparse=always',str(source/name),str(vm/name)],check=True)
 if (source/'account.private.json').exists():shutil.copy2(source/'account.private.json',vm/'account.private.json')
 manifest.update(developmentVm=True,modifiedForDiagnosis=True,clonedFrom=a.clone_from)
 (vm/'vm-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
else:
 manifest=json.loads((vm/'vm-manifest.json').read_text())
 assert manifest.get('developmentVm'),'Only a VM created with this script can receive development bundles'
if not running(vm):
 subprocess.run(['python3','tests/Xur.Media.Tests/start-vm.py',str(a.iso),'--name',a.name],check=True)
 # start-vm records the boot media, but this disk contains an app override and
 # must never be accepted as a clean final-ISO test.
 boot=json.loads((vm/'vm-manifest.json').read_text())
 manifest.update(boot,developmentVm=True,modifiedForDiagnosis=True)
 (vm/'vm-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
for _ in range(90):
 try:
  result=execute(a.name,['/usr/bin/test','-f','/var/lib/xur/installed'])
  if result['code']==0:break
 except (OSError,RuntimeError):pass
 time.sleep(1)
else:raise RuntimeError('Installed development VM did not start')
if not a.skip_publish:subprocess.run(['bash','eng/publish.sh'],check=True,stdout=(repo/'.build/dev-publish.log').open('w'),stderr=subprocess.STDOUT)
context_lock=(repo/'.build/context.lock').open('w');fcntl.flock(context_lock,fcntl.LOCK_SH)
bundle=repo/'.build/context/rootfs/usr/share/xur/app-bundle';meta=json.loads((bundle/'bundle.json').read_text())
for name,digest in meta['files'].items():assert hashlib.sha256((bundle/name).read_bytes()).hexdigest()==digest
with tempfile.TemporaryDirectory(prefix='xur-app-',dir=repo/'.build') as temp:
 directory=pathlib.Path(temp);filename=secrets.token_hex(16)+'.tgz';archive=directory/filename
 with tarfile.open(archive,'w:gz') as tar:tar.add(bundle,arcname='.')
 fcntl.flock(context_lock,fcntl.LOCK_UN);context_lock.close()
 digest=hashlib.sha256(archive.read_bytes()).hexdigest()
 class Handler(http.server.SimpleHTTPRequestHandler):
  def __init__(self,*args,**kwargs):super().__init__(*args,directory=directory,**kwargs)
  def log_message(self,*args):pass
 server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
 threading.Thread(target=server.serve_forever,daemon=True).start()
 script=r'''
import hashlib,json,os,pathlib,shutil,subprocess,tarfile,urllib.request
url,digest,bundle_id=ARGS
archive=pathlib.Path('/var/tmp/xur-development.tgz')
urllib.request.urlretrieve(url,archive)
assert hashlib.sha256(archive.read_bytes()).hexdigest()==digest
root=pathlib.Path('/var/lib/xur/app');release=root/'releases'/bundle_id
stage=root/'releases'/('development-'+bundle_id)
if stage.exists():shutil.rmtree(stage)
stage.mkdir()
with tarfile.open(archive) as tar:tar.extractall(stage,filter='data')
meta=json.loads((stage/'bundle.json').read_text());assert meta['id']==bundle_id
for name,expected in meta['files'].items():assert hashlib.sha256((stage/name).read_bytes()).hexdigest()==expected
subprocess.run(['systemctl','stop','xur-control','xur-agent','xur-gateway'],check=True)
previous=os.readlink(root/'current')
try:
 if release.exists():shutil.rmtree(stage)
 else:stage.rename(release)
 subprocess.run(['restorecon','-RF',str(release)],check=True)
 link=root/'development-current';link.unlink(missing_ok=True);link.symlink_to('releases/'+bundle_id);link.replace(root/'current')
 subprocess.run(['systemctl','start','xur-gateway','xur-agent','xur-control'],check=True)
except:
 link=root/'development-current';link.unlink(missing_ok=True);link.symlink_to(previous);link.replace(root/'current')
 subprocess.run(['systemctl','start','xur-gateway','xur-agent','xur-control'],check=True)
 raise
finally:archive.unlink(missing_ok=True)
print(json.dumps({'bundleId':bundle_id,'previousBundle':previous}))
'''
 try:
  result=execute(a.name,['/usr/bin/python3','-c','ARGS='+repr([f'http://10.71.1.2:{server.server_port}/{filename}',digest,meta['id']])+'\n'+script])
  assert result['code']==0,result['error']
 finally:server.shutdown();server.server_close()
manifest['developmentBundle']=meta['id'];(vm/'vm-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
base='http://127.0.0.1:18081'
for _ in range(60):
 try:
  if urllib.request.urlopen(base+'/health',timeout=2).status==200:break
 except OSError:pass
 time.sleep(1)
else:raise RuntimeError('Updated manager did not become ready')
jar=http.cookiejar.LWPCookieJar(str(vm/'session.private.cookies'))
browser=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
def request(path,data=None):
 with browser.open(base+path,None if data is None else urllib.parse.urlencode(data).encode()) as response:return response.status,response.read().decode()
from test_auth import browser_login
browser_login(vm,request)
assert browser.open(base+'/api/profiles').status==200
jar.save(ignore_discard=True,ignore_expires=True)
receipt={'result':'Passed','vm':a.name,'bundleId':meta['id'],'seconds':round(time.monotonic()-started,1),'isoRebuilt':False,'releaseEvidence':False}
(repo/'.build/dev-app.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
