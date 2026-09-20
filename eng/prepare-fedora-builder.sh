#!/usr/bin/env bash
# Execute only in the disposable Fedora VM, never on the Ubuntu host.
set -euo pipefail
test "$(id -u)" = 0
. /etc/os-release
test "$ID" = fedora
test "$(uname -m)" = x86_64
test "$(getenforce)" = Enforcing
python3 - "${1:?Pass toolchain-lock.json}" <<'PY'
import hashlib,json,pathlib,subprocess,sys,tarfile,urllib.request
entry=json.load(open(sys.argv[1]))['imageBuilderSource'];binary=pathlib.Path('/home/builder/image-builder')
if binary.exists():
 version=subprocess.check_output([str(binary),'version'],text=True)
 if 'version: '+entry['version'] in version:raise SystemExit(0)
subprocess.run(['dnf','install','-y','podman','golang','git','libvirt-devel','make','osbuild','osbuild-depsolve-dnf','skopeo','jq','xorriso','squashfs-tools'],check=True)
root=pathlib.Path('/home/builder/.xur-image-builder-'+entry['version']);root.mkdir(exist_ok=True)
archive=root/'source.tar.gz';urllib.request.urlretrieve(entry['url'],archive)
assert hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()==entry['sha256'],'Image Builder source checksum mismatch'
source=root/'source';source.mkdir(exist_ok=True)
with tarfile.open(archive) as tar:tar.extractall(source,filter='data')
modules=list(source.rglob('go.mod'));modules=[p for p in modules if 'vendor' not in p.parts];assert len(modules)==1
subprocess.run(['go','build','-mod=vendor','-ldflags=-X main.version='+entry['version'],'-o',str(binary)+'.new','./cmd/image-builder'],cwd=modules[0].parent,check=True)
pathlib.Path(str(binary)+'.new').replace(binary)
subprocess.run([str(binary),'version'],check=True)
PY
