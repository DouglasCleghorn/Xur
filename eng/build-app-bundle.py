#!/usr/bin/env python3
"""Build an independently versioned Xur application archive (no OS image)."""
import argparse,hashlib,json,os,pathlib,subprocess,tarfile,urllib.request
repo=pathlib.Path(__file__).resolve().parents[1]
os.chdir(repo)
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--from-published',action='store_true',help='Package the existing ISO build output after verifying every bundle file')
args=p.parse_args()
lock=json.loads((repo/'eng/toolchain-lock.json').read_text())
download=repo/'.build/downloads/tailscale.tgz'
download.parent.mkdir(parents=True,exist_ok=True)
if not download.exists() or hashlib.sha256(download.read_bytes()).hexdigest()!=lock['tailscale']['sha256']:
    urllib.request.urlretrieve(lock['tailscale']['url'],download)
assert hashlib.sha256(download.read_bytes()).hexdigest()==lock['tailscale']['sha256']
if not args.from_published:subprocess.run(['bash','eng/publish.sh'],check=True)
bundle=repo/'.build/context/rootfs/usr/share/xur/app-bundle'
meta=json.loads((bundle/'bundle.json').read_text())
for name,digest in meta['files'].items():
    assert hashlib.sha256((bundle/name).read_bytes()).hexdigest()==digest,name
out=repo/'dist';out.mkdir(exist_ok=True)
archive=out/'xur-app-x86_64.tar.gz'
with tarfile.open(archive,'w:gz') as tar:
    tar.add(bundle,arcname='.')
digest=hashlib.sha256(archive.read_bytes()).hexdigest()
(out/'xur-app-x86_64.tar.gz.sha256').write_text(digest+'  '+archive.name+'\n')
print(json.dumps({'file':str(archive),'sha256':digest,'bundleId':meta['id'],'hostAbi':meta['hostAbi']}))
