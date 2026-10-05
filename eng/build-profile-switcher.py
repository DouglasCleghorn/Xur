#!/usr/bin/env python3
"""Compile the desktop profile switcher in the disposable Fedora builder."""
import hashlib,json,os,pathlib,shutil,subprocess,tarfile,tempfile
repo=pathlib.Path(__file__).resolve().parents[1]
source=repo/'tools/Xur.ProfileSwitcher';output=repo/'.build/profile-switcher-runtime'
def sha(path):return hashlib.file_digest(path.open('rb'),'sha256').hexdigest()
inputs={p.name:sha(p) for p in sorted(source.iterdir()) if p.is_file()}
font=repo/'src/Xur.Control/wwwroot/fonts'
inputs.update({name:sha(font/name) for name in ('IBMPlexSans.ttf','OFL.txt')})
receipt=output/'build-receipt.json'
if receipt.exists():
    prior=json.loads(receipt.read_text())
    if prior['inputs']==inputs and all((output/p).is_file() and sha(output/p)==h for p,h in prior['files'].items()):
        print('Profile switcher: verified build cache');raise SystemExit
vm=pathlib.Path(os.environ.get('XUR_BUILD_ROOT',pathlib.Path.home()/'.local/share/xur-build'))/'vm'
options=['-o','BatchMode=yes','-o','UserKnownHostsFile='+str(vm/'known_hosts'),'-i',str(vm/'builder_ed25519')]
ssh=['ssh',*options,'-p','22220','builder@127.0.0.1'];scp=['scp','-q',*options,'-P','22220']
remote='xur-profile-switcher-'+hashlib.sha256(json.dumps(inputs,sort_keys=True).encode()).hexdigest()[:16]
subprocess.run([*ssh,'mkdir -p '+remote],check=True)
subprocess.run([*scp,*map(str,source.iterdir()),'builder@127.0.0.1:'+remote+'/'],check=True)
subprocess.run([*ssh,f'''set -eu
sudo dnf install -y gcc-c++ cmake qt6-qtbase-devel qt6-qtwayland kf6-kglobalaccel-devel libevdev-devel libdrm-devel libinput-devel zlib-devel systemd-devel dbus-daemon > {remote}/dependencies.log 2>&1
bash {remote}/build.sh "$PWD/{remote}" > {remote}/build.log 2>&1
'''],check=True)
with tempfile.TemporaryDirectory(dir=repo/'.build') as temp:
    archive=pathlib.Path(temp)/'runtime.tar.gz'
    subprocess.run([*scp,'builder@127.0.0.1:'+remote+'/profile-switcher-runtime.tar.gz',str(archive)],check=True)
    stage=pathlib.Path(temp)/'runtime';stage.mkdir()
    with tarfile.open(archive) as tar:tar.extractall(stage,filter='data')
    for name in ('IBMPlexSans.ttf','OFL.txt'):shutil.copy2(font/name,stage/'fonts'/name)
    shutil.copy2(repo/'LICENSE',stage/'LICENSE')
    files={str(p.relative_to(stage)):sha(p) for p in sorted(stage.rglob('*')) if p.is_file()}
    (stage/'build-receipt.json').write_text(json.dumps({'inputs':inputs,'files':files},indent=2)+'\n')
    if output.exists():shutil.rmtree(output)
    shutil.move(stage,output)
print('Built desktop profile switcher')
