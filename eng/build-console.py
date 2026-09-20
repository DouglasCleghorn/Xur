#!/usr/bin/env python3
"""Build the small DRM console in the disposable Fedora builder; cache exact inputs."""
import hashlib,json,os,pathlib,shutil,subprocess,tarfile,tempfile,urllib.request
repo=pathlib.Path(__file__).resolve().parents[1];source=repo/'tools/Xur.Console';output=repo/'.build/console-runtime'
def digest(p):return hashlib.file_digest(p.open('rb'),'sha256').hexdigest()
inputs={p.name:digest(p) for p in sorted(source.iterdir()) if p.is_file()}
receipt=output/'build-receipt.json'
if receipt.exists():
 r=json.loads(receipt.read_text())
 if r['inputs']==inputs and all((output/p).is_file() and digest(output/p)==h for p,h in r['files'].items()):
  print('Display console: using verified build cache');raise SystemExit
lock=json.loads((source/'upstream-lock.json').read_text())['kmscon'];archive=repo/'.build/console-upstream/kmscon.tar.gz'
archive.parent.mkdir(parents=True,exist_ok=True)
if not archive.exists():
 with urllib.request.urlopen(lock['url']) as response,archive.open('wb') as f:shutil.copyfileobj(response,f)
assert digest(archive)==lock['sha256'],'kmscon source checksum mismatch'
vm=pathlib.Path(os.environ.get('XUR_BUILD_ROOT',pathlib.Path.home()/'.local/share/xur-build'))/'vm'
options=['-o','BatchMode=yes','-o','UserKnownHostsFile='+str(vm/'known_hosts'),'-i',str(vm/'builder_ed25519')]
ssh=['ssh',*options,'-p','22220','builder@127.0.0.1'];scp=['scp','-q',*options,'-P','22220']
key=hashlib.sha256(json.dumps(inputs,sort_keys=True).encode()).hexdigest()[:16];remote='xur-console-'+key
subprocess.run([*ssh,'mkdir -p '+remote],check=True)
subprocess.run([*scp,*map(str,[source/'build.sh',source/'patch.py',source/'client.c',archive]),'builder@127.0.0.1:'+remote+'/'],check=True)
command=f'''set -eu
sudo dnf install -y gcc meson ninja-build libdrm-devel libxkbcommon-devel systemd-devel zlib-devel libtsm-devel libcurl-devel > {remote}/dependencies.log 2>&1
cd {remote}
rm -rf kmscon output
mkdir kmscon
tar -xzf kmscon.tar.gz --strip-components=1 -C kmscon
bash build.sh "$PWD" > build.log 2>&1
'''
subprocess.run([*ssh,command],check=True)
with tempfile.TemporaryDirectory(dir=repo/'.build') as temp:
 local=pathlib.Path(temp)/'runtime.tgz'
 subprocess.run([*scp,'builder@127.0.0.1:'+remote+'/console-runtime.tar.gz',str(local)],check=True)
 stage=pathlib.Path(temp)/'runtime';stage.mkdir()
 with tarfile.open(local) as tar:tar.extractall(stage,filter='data')
 files={str(p.relative_to(stage)):digest(p) for p in sorted(stage.rglob('*')) if p.is_file()}
 (stage/'build-receipt.json').write_text(json.dumps({'inputs':inputs,'upstream':lock,'files':files},indent=2)+'\n')
 if output.exists():shutil.rmtree(output)
 shutil.move(stage,output)
print('Built display console '+key)
