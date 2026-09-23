#!/usr/bin/env python3
"""Online-source pinning and live-update failure recovery without disks or root."""
import importlib.machinery,importlib.util,json,pathlib,subprocess,tempfile,time,types
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
# The live Fedora base follows a major-version tag, resolved once per build.
base=load('base','os/bootc/resolve-base.py');base_calls=[]
containerfile=(repo/'os/bootc/Containerfile').read_text()
base_info={'Digest':'sha256:'+'c'*64,'Architecture':'amd64','Os':'linux','Labels':{'org.opencontainers.image.version':'44.20260923.0','ostree.linux':'fixture-kernel'}}
def inspect_base(args,**kwargs):
 base_calls.append(args);assert kwargs['timeout']==90
 return json.dumps(base_info)
receipt=base.resolve(containerfile,inspect_base)
assert base_calls==[['skopeo','inspect','--override-arch','amd64','docker://quay.io/fedora/fedora-bootc:44']]
assert receipt['resolvedReference']=='quay.io/fedora/fedora-bootc@sha256:'+'c'*64
assert receipt['kernel']=='fixture-kernel' and receipt['version']=='44.20260923.0'
assert json.loads((repo/'eng/toolchain-lock.json').read_text())['liveInstallerBase']['reference']==receipt['reference']
for update in [{'Digest':'sha256:bad'},{'Architecture':'arm64'},{'Os':'windows'},{'Labels':{'org.opencontainers.image.version':'45.0'}},{'Labels':{}}]:
 try:base.resolve(containerfile,lambda *a,**kw:json.dumps(base_info|update))
 except ValueError:pass
 else:raise AssertionError('Invalid Fedora base identity was accepted: '+str(update))
try:base.resolve(containerfile.replace(':44',':latest'),inspect_base)
except ValueError:pass
else:raise AssertionError('An unbounded latest tag was accepted')
with tempfile.TemporaryDirectory() as directory:
 root=pathlib.Path(directory);app.ROOT=root/'app';app.BUNDLED=root/'bundled';app.BUNDLED.mkdir();app.READY=root/'ready';app.APPROVED=root/'approved.ks';app.CMDLINE=root/'cmdline';app.CMDLINE.write_text('xur.installer=1')
 (app.BUNDLED/'bundle.json').write_text('{"id":"bundled"}')
 class Updater:
  SERVICES=['xur-control','xur-agent','xur-gateway']
  def atomic(self,path,data):path.write_text(json.dumps(data))
  def channel(self):return 'stable'
  def check(self,stage):raise OSError('Network unavailable')
  def read(self,path,default=None):return json.loads(path.read_text()) if path.exists() else default
  def healthy(self,identity,seconds):return identity=='bundled'
  def call(self,args,timeout):calls.append(args)
 def forbidden():raise AssertionError('Default boot must not initialize the online updater')
 app.updater=forbidden
 start=time.monotonic();app.prepare();assert time.monotonic()-start<1
 assert (app.ROOT/'current').resolve()==app.BUNDLED and not app.READY.exists()
 app.updater=lambda:Updater()
 app.verify();assert app.READY.exists()
 # A failed online check must not revoke health approval or touch running services.
 before_calls=list(calls);app.check()
 assert json.loads((app.ROOT/'check.json').read_text())['state']=='unavailable'
 assert app.READY.exists() and (app.ROOT/'current').resolve()==app.BUNDLED and calls==before_calls
 # Late connectivity discovers a signed release, but never activates it mid-setup.
 class Online(Updater):
  def check(self,stage):return {'id':'new','version':'2'}
 app.updater=lambda:Online();app.check()
 assert json.loads((app.ROOT/'check.json').read_text())['state']=='available'
 assert app.READY.exists() and (app.ROOT/'current').resolve()==app.BUNDLED and calls==before_calls
 class Current(Updater):
  def check(self,stage):return {'id':'bundled','version':'1'}
 app.updater=lambda:Current();app.check()
 assert json.loads((app.ROOT/'check.json').read_text())['state']=='current'
 # A hung network request is bounded independently of the healthy console.
 class Slow(Updater):
  def check(self,stage):time.sleep(5);raise AssertionError('Timeout did not interrupt the request')
 app.updater=lambda:Slow();app.CHECK_SECONDS=1;start=time.monotonic();app.check()
 assert time.monotonic()-start<3 and app.READY.exists()
 assert json.loads((app.ROOT/'check.json').read_text())['state']=='unavailable'
 # Stop checking after approval; explicit off also makes no online calls.
 app.updater=forbidden;app.APPROVED.touch();app.check();app.APPROVED.unlink()
 app.CMDLINE.write_text('xur.installer=1 xur.app-update=off');app.prepare();app.check()
 assert json.loads((app.ROOT/'check.json').read_text())['state']=='disabled'
 # Retain the explicitly requested pre-start refresh and offline fallback.
 app.CMDLINE.write_text('xur.installer=1 xur.app-update=on');app.updater=lambda:Updater();app.prepare()
 assert json.loads((app.ROOT/'check.json').read_text())['state']=='unavailable'
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
# Online readiness must never be a default prerequisite of the visible console.
prepare_unit=(repo/'os/installer/systemd/xur-installer-app-prepare.service').read_text()
assert 'network-online.target' not in prepare_unit and 'xur-network.service' not in prepare_unit
timer=(repo/'os/installer/systemd/xur-installer-app-check.timer').read_text()
assert 'OnUnitInactiveSec=60s' in timer
assert 'xur-installer-app-check.timer' in (repo/'os/installer/Containerfile').read_text()
print(json.dumps({'suite':'OnlineInstaller','result':'Passed','digestPinned':True,'sameUpdateChannel':True,'immediateBundledBoot':True,'networkFallback':True,'boundedBackgroundCheck':True,'lateConnection':True,'noBackgroundActivation':True,'approvalIndependentOfInternet':True,'unhealthyAppFallback':True,'noEmbeddedPayload':True,'liveBootTested':False}))
