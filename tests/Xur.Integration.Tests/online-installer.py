#!/usr/bin/env python3
"""Stable-channel installation and live-update failure recovery without disks or root."""
import importlib.machinery,importlib.util,json,os,pathlib,shlex,subprocess,tempfile,time,types
repo=pathlib.Path(__file__).resolve().parents[2]
def load(name,path):
 loader=importlib.machinery.SourceFileLoader(name,str(repo/path));spec=importlib.util.spec_from_loader(name,loader);m=importlib.util.module_from_spec(spec);loader.exec_module(m);return m
app=load('live','os/installer/app-bootstrap');updater=load('os_update','os/bootc/os-update')
ks=(repo/'os/installer/install-template.ks').read_text();calls=[]
bootc=shlex.split(next(line for line in ks.splitlines() if line.startswith('bootc ')))
assert bootc==['bootc','--source-imgref','registry:'+updater.CHANNEL,'--target-imgref',updater.CHANNEL]
assert updater.CHANNEL=='ghcr.io/ublue-os/bazzite-nvidia-open:stable'
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
 # The installed update configuration no longer needs a resolver receipt.
 target=root/'installed-config';(target/'etc/xur').mkdir(parents=True)
 manager=(repo/'os/installer/install-manager').read_text()
 config=manager[manager.index("printf '%s\\n' '{\"channel\":"):manager.index('python3 - "$target"')]
 subprocess.run(['bash','-eu','-c','umask 077\n'+config],env={**os.environ,'target':str(target)},check=True)
 upstream=target/'etc/xur/upstream.json'
 assert updater.read(upstream)['channel']==updater.CHANNEL and upstream.stat().st_mode&0o777==0o644
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
 manifest['pipelines'].append({'name':'efiboot-tree','stages':[{'type':'org.osbuild.grub2.iso'}]})
 manifest['pipelines'][1]['stages'].append({'type':'org.osbuild.grub2.iso.legacy'})
 before.write_text(json.dumps(manifest))
 subprocess.run(['python3',str(repo/'eng/label-live-manifest.py'),str(before),str(after)],check=True)
 result=json.loads(after.read_text());assert result['pipelines'][0]['stages'][-1]['type']=='org.osbuild.selinux'
 expected=json.loads(json.dumps(manifest['pipelines'][1]))
 expected['stages'][0]['options']['exclude_paths'].append('usr/lib/modules/.*/initramfs[.]img')
 actual=json.loads(json.dumps(result['pipelines'][1]));menu=actual['stages'].pop()
 assert actual==expected, 'Menu configuration and duplicate initramfs removal must preserve compression and boot layout'
 assert menu['type']=='org.osbuild.copy' and menu['options']['paths'][0]['to']=='tree:///boot/grub2/grub.cfg'
 assert result['pipelines'][2]['stages'][1]['options']['paths'][0]['to']=='tree:///EFI/BOOT/grub.cfg'
 manifest['pipelines'][1]['stages']=[];before.write_text(json.dumps(manifest))
 invalid=subprocess.run(['python3',str(repo/'eng/label-live-manifest.py'),str(before),str(after)],capture_output=True,text=True)
 assert invalid.returncode!=0 and 'Expected exactly one' in invalid.stderr
 # Exercise the real post-install completion block against a simulated boot mount.
 # These commands model mount state; marker writes still use the real temporary files.
 target=root/'installed';(target/'boot/grub2').mkdir(parents=True);(target/'var/lib/xur').mkdir(parents=True)
 marker=ks.split('# Publish only after',1)[1].split('%end',1)[0]
 marker='# Publish only after'+marker
 tools=root/'mount-tools';tools.mkdir();state=root/'boot-mount.json'
 command='''#!/usr/bin/env python3
import json,os,pathlib,sys
path=pathlib.Path(os.environ['XUR_BOOT_FIXTURE']);state=json.loads(path.read_text());name=pathlib.Path(sys.argv[0]).name
if name=='mountpoint':sys.exit(0 if state['mounted'] else 1)
if name=='findmnt':print('ro,relatime' if state['readOnly'] else 'rw,relatime');sys.exit(0)
assert name=='mount' and sys.argv[1]=='-o' and sys.argv[3]==state['boot']
mode=sys.argv[2];assert mode in ('remount,rw','remount,ro');state['remounts'].append(mode)
if mode=='remount,rw' and state['failRemount']:path.write_text(json.dumps(state));sys.exit(32)
state['readOnly']=mode=='remount,ro';pathlib.Path(state['boot']).chmod(0o555 if state['readOnly'] else 0o755);path.write_text(json.dumps(state))
'''
 for name in ('mountpoint','findmnt','mount'):
  file=tools/name;file.write_text(command);file.chmod(0o700)
 marker_cases=[]
 for case in ('missing-grub','unmounted-boot','writable-boot','readonly-boot','remount-failure','marker-write-failure'):
  boot=target/'boot';boot.chmod(0o755)
  if (boot/'xur').is_dir():
   (boot/'xur/installed').unlink();(boot/'xur').rmdir()
  elif (boot/'xur').exists():(boot/'xur').unlink()
  (target/'var/lib/xur/installed').unlink(missing_ok=True)
  grub=boot/'grub2/grub.cfg';grub.unlink(missing_ok=True)
  if case!='missing-grub':grub.write_text('set default=0\n')
  readonly=case in ('readonly-boot','remount-failure','marker-write-failure')
  if case=='marker-write-failure':(boot/'xur').write_text('Not a directory')
  boot.chmod(0o555 if readonly else 0o755)
  state.write_text(json.dumps({'boot':str(boot),'mounted':case!='unmounted-boot','readOnly':readonly,'failRemount':case=='remount-failure','remounts':[]}))
  result=subprocess.run(['bash','-eu','-c',marker],env={**os.environ,'target':str(target),'XUR_BOOT_FIXTURE':str(state),'PATH':str(tools)+os.pathsep+os.environ['PATH']},capture_output=True)
  observed=json.loads(state.read_text());success=case in ('writable-boot','readonly-boot')
  assert (result.returncode==0)==success,(case,result.stderr)
  assert (boot/'xur/installed').is_file()==success and (target/'var/lib/xur/installed').is_file()==success,case
  assert observed['readOnly']==readonly,(case,'Original boot protection was not restored')
  expected=['remount,rw','remount,ro'] if case in ('readonly-boot','marker-write-failure') else ['remount,rw'] if case=='remount-failure' else []
  assert observed['remounts']==expected,(case,observed['remounts'])
  marker_cases.append(case)
 boot.chmod(0o755)
# Online readiness must never be a default prerequisite of the visible console.
prepare_unit=(repo/'os/installer/systemd/xur-installer-app-prepare.service').read_text()
assert 'network-online.target' not in prepare_unit and 'xur-network.service' not in prepare_unit
timer=(repo/'os/installer/systemd/xur-installer-app-check.timer').read_text()
assert 'OnUnitInactiveSec=60s' in timer
assert 'xur-installer-app-check.timer' in (repo/'os/installer/Containerfile').read_text()
print(json.dumps({'suite':'OnlineInstaller','result':'Passed','stableInstallSource':True,'sameUpdateChannel':True,'immediateBundledBoot':True,'networkFallback':True,'boundedBackgroundCheck':True,'lateConnection':True,'noBackgroundActivation':True,'approvalIndependentOfInternet':True,'unhealthyAppFallback':True,'noEmbeddedPayload':True,'bootCompletionCases':marker_cases,'liveBootTested':False}))
