#!/usr/bin/env python3
"""Online-source pinning and live-update failure recovery without disks or root."""
import importlib.machinery,importlib.util,json,pathlib,subprocess,tempfile,types
repo=pathlib.Path(__file__).resolve().parents[2]
def load(name,path):
 loader=importlib.machinery.SourceFileLoader(name,str(repo/path));spec=importlib.util.spec_from_loader(name,loader);m=importlib.util.module_from_spec(spec);loader.exec_module(m);return m
source=load('source','os/installer/resolve-source');app=load('live','os/installer/app-bootstrap')
ks=(repo/'os/installer/install-template.ks').read_text();calls=[]
def inspect(args,**kwargs):calls.append(args);return 'sha256:'+'a'*64+'\n'
resolved,image=source.resolve(ks,inspect)
assert 'registry:'+image in resolved and '--target-imgref '+source.CHANNEL in resolved
assert calls[0][-1]=='docker://'+source.CHANNEL
assert source.CHANNEL in (repo/'os/bootc/os-update').read_text()
for bad in ['sha256:bad','sha256:'+'b'*64+'\nclearpart --all']:
 try:source.resolve(ks,lambda *args,**kw:bad)
 except ValueError:pass
 else:raise AssertionError('Untrusted digest became kickstart instructions')
with tempfile.TemporaryDirectory() as directory:
 root=pathlib.Path(directory);app.ROOT=root/'app';app.BUNDLED=root/'bundled';app.BUNDLED.mkdir();app.READY=root/'ready'
 (app.BUNDLED/'bundle.json').write_text('{"id":"bundled"}')
 class Updater:
  SERVICES=['xur-control','xur-agent','xur-gateway']
  def atomic(self,path,data):path.write_text(json.dumps(data))
  def channel(self):return 'stable'
  def check(self,stage):raise OSError('Network unavailable')
  def read(self,path,default=None):return json.loads(path.read_text()) if path.exists() else default
  def healthy(self,identity,seconds):return identity=='bundled'
  def call(self,args,timeout):calls.append(args)
 app.updater=lambda:Updater();app.prepare();assert (app.ROOT/'current').resolve()==app.BUNDLED and not app.READY.exists()
 app.verify();assert app.READY.exists()
 # A signed but nonfunctional live app must restore bundled executables before unlock.
 app.READY.unlink();broken=root/'broken';broken.mkdir();(broken/'bundle.json').write_text('{"id":"broken"}');app.select(broken)
 app.verify();assert (app.ROOT/'current').resolve()==app.BUNDLED and app.READY.exists()
 # Verify the ISO manifest stays payload-free and retains SELinux labeling.
 manifest={'pipelines':[{'name':'os-tree','stages':[{'type':'org.osbuild.container-deploy'}]},{'name':'bootiso-tree','stages':[{'type':'org.osbuild.squashfs','inputs':{'tree':{'origin':'org.osbuild.pipeline','references':['name:os-tree']}},'options':{'filename':'LiveOS/squashfs.img','exclude_paths':['boot/efi/.*'],'compression':{'method':'zstd'}}},{'type':'org.osbuild.xorrisofs','options':{'volid':'fixture'}}]}]}
 before=root/'before';after=root/'after';before.write_text(json.dumps(manifest))
 subprocess.run(['python3',str(repo/'eng/label-live-manifest.py'),str(before),str(after)],check=True)
 result=json.loads(after.read_text());assert result['pipelines'][0]['stages'][-1]['type']=='org.osbuild.selinux'
 expected=json.loads(json.dumps(manifest['pipelines'][1]))
 expected['stages'][0]['options']['exclude_paths'].append('usr/lib/modules/.*/initramfs[.]img')
 assert result['pipelines'][1]==expected, 'Removing the duplicate initramfs must not change source, compression or boot layout'
 manifest['pipelines'][1]['stages']=[];before.write_text(json.dumps(manifest))
 invalid=subprocess.run(['python3',str(repo/'eng/label-live-manifest.py'),str(before),str(after)],capture_output=True,text=True)
 assert invalid.returncode!=0 and 'Expected exactly one' in invalid.stderr
print(json.dumps({'suite':'OnlineInstaller','result':'Passed','digestPinned':True,'sameUpdateChannel':True,'networkFallback':True,'unhealthyAppFallback':True,'noEmbeddedPayload':True,'liveBootTested':False}))
