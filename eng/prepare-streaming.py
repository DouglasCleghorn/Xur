#!/usr/bin/env python3
"""Verify and extract the pinned upstream Sunshine bundle; never install host packages."""
import hashlib,json,pathlib,shutil,subprocess,urllib.request
repo=pathlib.Path(__file__).resolve().parents[1]
lock=json.loads((repo/'tools/Xur.Streaming/upstream-lock.json').read_text())['sunshine']
root=repo/'.build/upstream-streaming';root.mkdir(exist_ok=True,parents=True)
archive=root/'Sunshine.AppImage'
if not archive.exists():
 with urllib.request.urlopen(lock['url']) as r,archive.open('wb') as f:shutil.copyfileobj(r,f)
assert hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()==lock['sha256'],'Sunshine checksum mismatch'
archive.chmod(0o755)
extracted=root/'squashfs-root'
if extracted.exists():shutil.rmtree(extracted)
subprocess.run([str(archive),'--appimage-extract'],cwd=root,stdout=subprocess.DEVNULL,check=True)
output=repo/'.build/streaming-runtime'
if output.exists():shutil.rmtree(output)
shutil.move(extracted,output)
shutil.copy(repo/'tools/Xur.Streaming/upstream-lock.json',output/'upstream-lock.json')
shutil.copy(repo/'tools/Xur.Streaming/LICENSE',output/'LICENSE-Sunshine')
(output/'usr/lib').mkdir(exist_ok=True,parents=True)
subprocess.run(['cc','-shared','-fPIC','-O2','-Wall','-Wextra','-Werror',str(repo/'tools/Xur.Input/seat-input.c'),'-ldl','-o',str(output/'usr/lib/libxur-seat-input.so')],check=True)
print('Sunshine: verified '+lock['version'])
