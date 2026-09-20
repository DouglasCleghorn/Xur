#!/usr/bin/env python3
"""Build the virtual monitor helper in the existing disposable Fedora builder."""
import hashlib,json,os,pathlib,subprocess,shutil
repo=pathlib.Path(__file__).resolve().parents[1];source=repo/'tools/Xur.VirtualDisplay';output=repo/'.build/virtual-display-runtime'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
inputs={p.name:sha(p) for p in sorted(source.iterdir()) if p.is_file()}
receipt=output/'build-receipt.json'
if receipt.exists():
 r=json.loads(receipt.read_text())
 if r['inputs']==inputs and (output/'xur-virtual-output').exists() and sha(output/'xur-virtual-output')==r['binary']:
  print('Virtual monitor: verified build cache');raise SystemExit
vm=pathlib.Path(os.environ.get('XUR_BUILD_ROOT',pathlib.Path.home()/'.local/share/xur-build'))/'vm';options=['-o','BatchMode=yes','-o','UserKnownHostsFile='+str(vm/'known_hosts'),'-i',str(vm/'builder_ed25519')]
ssh=['ssh',*options,'-p','22220','builder@127.0.0.1'];scp=['scp','-q',*options,'-P','22220']
remote='xur-virtual-display-'+hashlib.sha256(json.dumps(inputs,sort_keys=True).encode()).hexdigest()[:16]
subprocess.run([*ssh,'mkdir -p '+remote],check=True)
subprocess.run([*scp,str(source/'client.c'),str(source/'screencast.xml'),'builder@127.0.0.1:'+remote+'/'],check=True)
subprocess.run([*ssh,f'''set -eu
sudo dnf install -y gcc wayland-devel systemd-devel > {remote}/dependencies.log 2>&1
cd {remote}
wayland-scanner client-header screencast.xml screencast-client.h
wayland-scanner private-code screencast.xml screencast-code.c
cc -O2 -Wall -Wextra -Werror client.c screencast-code.c -o xur-virtual-output $(pkg-config --cflags --libs wayland-client libsystemd)
'''],check=True)
output.mkdir(parents=True,exist_ok=True)
subprocess.run([*scp,'builder@127.0.0.1:'+remote+'/xur-virtual-output',str(output/'xur-virtual-output')],check=True)
(output/'xur-virtual-output').chmod(0o755)
shutil.copy2(source/'COPYING',output/'COPYING')
shutil.copy2(source/'README.md',output/'README.md')
receipt.write_text(json.dumps({'inputs':inputs,'binary':sha(output/'xur-virtual-output')},indent=2)+'\n')
print('Built virtual monitor helper')
