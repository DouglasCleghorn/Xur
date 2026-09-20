#!/usr/bin/env python3
"""Build and inspect a development ISO using the isolated Fedora toolchain.
Never selects a physical installation disk or changes host security policy.
"""
import argparse,fcntl,hashlib,json,os,pathlib,shutil,subprocess,tarfile,time,urllib.request
repo=pathlib.Path(__file__).resolve().parents[1];os.chdir(repo)
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--check',action='store_true',help='Check local build prerequisites without changing anything');p.add_argument('--output',default='dist/xur-installer-x86_64.iso',help='Output ISO beneath dist/');p.add_argument('--inspect-existing',action='store_true',help='Inspect and retrieve the completed Fedora build; requires an identical build context');args=p.parse_args()
root=pathlib.Path(os.environ.get('XUR_BUILD_ROOT',pathlib.Path.home()/'.local/share/xur-build'));vm=root/'vm';cache=pathlib.Path(os.environ.get('XUR_BUILD_CACHE',pathlib.Path.home()/'.cache/xur-build'))
lock=json.loads(pathlib.Path('eng/toolchain-lock.json').read_text());out=pathlib.Path(args.output).resolve()
if not out.is_relative_to(repo/'dist') or out.suffix!='.iso':raise SystemExit('Output must be an .iso file beneath this checkout\'s dist/')
required=[root/'qemu/usr/bin/qemu-system-x86_64',root/'qemu/usr/bin/genisoimage',root/'qemu/usr/share/OVMF/OVMF_CODE_4M.fd',root/'qemu/usr/share/OVMF/OVMF_VARS_4M.fd']
missing=[str(path) for path in required if not path.is_file()]+[tool for tool in ['ssh','scp','tar','ip'] if shutil.which(tool) is None]
if not os.access('/dev/kvm',os.R_OK|os.W_OK):missing.append('read/write access to /dev/kvm')
if missing:raise SystemExit('Missing build prerequisites (see docs/development/build.md):\n  '+'\n  '.join(missing))
if args.check:print('Local prerequisites passed; the build verifies pinned downloads and Fedora tools before use.');raise SystemExit(0)
pathlib.Path('.build').mkdir(exist_ok=True)
build_lock=pathlib.Path('.build/iso.lock').open('w')
try:fcntl.flock(build_lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
except BlockingIOError:raise SystemExit('Another ISO build is running in this checkout')
cache.mkdir(parents=True,exist_ok=True);pathlib.Path('.build/downloads').mkdir(parents=True,exist_ok=True)
def sha(path,algorithm='sha256'):
 with path.open('rb') as f:return hashlib.file_digest(f,algorithm).hexdigest()
def fetch(entry,path,algorithm='sha256'):
 if path.exists() and sha(path,algorithm)==entry[algorithm]:return
 tmp=path.with_suffix(path.suffix+'.download');urllib.request.urlretrieve(entry['url'],tmp)
 if sha(tmp,algorithm)!=entry[algorithm]:tmp.unlink();raise SystemExit('Download checksum mismatch: '+str(path))
 tmp.replace(path)
fetch(lock['tailscale'],pathlib.Path('.build/downloads/tailscale.tgz'))
fetch(lock['builderCloudImage'],cache/'fedora-44.qcow2')
sdk=pathlib.Path(os.environ.get('XUR_DOTNET',root/'dotnet/dotnet'))
if not sdk.is_file():
 archive=cache/'dotnet-sdk.tar.gz';fetch(lock['dotnetSdk'],archive,'sha512');sdk.parent.mkdir(parents=True,exist_ok=True)
 with tarfile.open(archive) as tar:tar.extractall(sdk.parent,filter='data')
if subprocess.check_output([str(sdk),'--version'],text=True).strip()!=lock['dotnetSdk']['version']:raise SystemExit('The SDK does not match the pinned version')
os.environ['XUR_DOTNET']=str(sdk)
ssh=['ssh','-o','BatchMode=yes','-o','ConnectTimeout=5','-o','StrictHostKeyChecking=accept-new','-o',f'UserKnownHostsFile={vm}/known_hosts','-i',str(vm/'builder_ed25519'),'-p','22220','builder@127.0.0.1']
scp=['scp','-q','-o','BatchMode=yes','-o','StrictHostKeyChecking=accept-new','-o',f'UserKnownHostsFile={vm}/known_hosts','-i',str(vm/'builder_ed25519'),'-P','22220']
def run(command,**kw):return subprocess.run(command,check=True,**kw)
if subprocess.run(ssh+['true'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode:
 run(['python3','eng/start-builder.py'])
 for _ in range(90):
  if subprocess.run(ssh+['true'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0:break
  time.sleep(2)
 else:raise SystemExit('Fedora builder SSH did not become ready')
if not args.inspect_existing:
 # Cloud-init installs build packages on the first boot; existing builders return immediately.
 run(ssh+['sudo cloud-init status --wait || test "$(cloud-init status --format json | python3 -c \'import json,sys; print(json.load(sys.stdin).get("status", ""))\')" = done'])
 run(scp+['eng/prepare-fedora-builder.sh','eng/toolchain-lock.json','builder@127.0.0.1:.'])
 run(ssh+['sudo bash prepare-fedora-builder.sh toolchain-lock.json'])
 # Use the already pinned Image Builder. Fresh builders can prepare it with prepare-fedora-builder.sh.
 print('Publishing and checking the running control process...',flush=True)
 run(['bash','eng/publish.sh'],stdout=pathlib.Path('.build/build-publish.log').open('w'),stderr=subprocess.STDOUT)
 run(['python3','tests/Xur.Integration.Tests/bootstrap.py'],stdout=pathlib.Path('.build/build-process-tests.json').open('w'))
 run([str(sdk),'run','--project','tests/Xur.Unit.Tests','-c','Release'],stdout=pathlib.Path('.build/build-unit-tests.log').open('w'))
 run([str(sdk),'run','--project','tests/Xur.Profile.Tests','-c','Release'],stdout=pathlib.Path('.build/build-profile-tests.log').open('w'))
 run(['python3','tests/Xur.Integration.Tests/terminal.py'],stdout=pathlib.Path('.build/build-terminal-tests.json').open('w'))
 run(['tar','-cf','.build/context.tar','-C','.build/context','.'])
 run(scp+['.build/context.tar','eng/build-in-fedora.sh','eng/label-live-manifest.py','eng/context-receipt.py','eng/inspect-media.sh','builder@127.0.0.1:.'])
 print('Building host, live environment and ISO in Fedora; log: builder:iso-build-script.log',flush=True)
 run(ssh+['while pgrep -f "^bash build-in-fedora.sh /home/builder/xur-build$" >/dev/null; do sleep 2; done; set -eu; rm -rf /home/builder/xur-build; mkdir /home/builder/xur-build; tar -xf context.tar -C xur-build; sudo bash build-in-fedora.sh /home/builder/xur-build > iso-build-script.log 2>&1'])
else:
 run(['python3','eng/context-receipt.py','verify','.build/context'])
 local=json.loads(pathlib.Path('.build/context/publish-receipt.json').read_text())
 remote=json.loads(subprocess.check_output(ssh+['cat xur-build/publish-receipt.json'],text=True))
 if local!=remote:raise SystemExit('Existing builder context differs from local publication')
run(scp+['eng/inspect-media.sh','builder@127.0.0.1:.'])
inspection='inspect-script-'+str(time.time_ns())
run(ssh+[f'sudo bash inspect-media.sh xur-output/bootiso/install.iso {inspection}'])
out.parent.mkdir(parents=True,exist_ok=True);temp=out.with_suffix('.iso.partial')
run(scp+['builder@127.0.0.1:/home/builder/xur-output/bootiso/install.iso',str(temp)])
receipt=out.with_suffix('.embedded.json');run(scp+[f'builder@127.0.0.1:/home/builder/{inspection}/embedded-verification.json',str(receipt)])
run(ssh+[f'sudo rm -rf -- /home/builder/{inspection}'])
remote_sha=subprocess.check_output(ssh+['sha256sum xur-output/bootiso/install.iso'],text=True).split()[0]
if sha(temp)!=remote_sha:raise SystemExit('ISO transfer checksum mismatch')
temp.replace(out);out.with_suffix('.iso.sha256').write_text(f'{remote_sha}  {out.name}\n')
print(json.dumps({'iso':str(out),'sha256':remote_sha,'embeddedVerification':str(receipt),'verification':'Build complete; run the installer media tests next'}))
