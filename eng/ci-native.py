#!/usr/bin/env python3
"""Build Fedora native helpers on hosted CI, using the required Docker mirror."""
import hashlib,json,os,pathlib,re,shutil,subprocess,tarfile,tempfile,urllib.request
repo=pathlib.Path(__file__).resolve().parents[1]
def sha(p):return hashlib.file_digest(p.open('rb'),'sha256').hexdigest()
def build():
 console=repo/'tools/Xur.Console';virtual=repo/'tools/Xur.VirtualDisplay';switcher=repo/'tools/Xur.ProfileSwitcher'
 image=re.search(r"^FROM (\S+) AS native-builder$",(repo/"eng/Containerfile").read_text(),re.M).group(1)
 lock=json.loads((console/'upstream-lock.json').read_text())['kmscon']
 (repo/'.build').mkdir(exist_ok=True)
 with tempfile.TemporaryDirectory(dir=repo/'.build') as temp:
  work=pathlib.Path(temp);c=work/'console';v=work/'virtual';s=work/'switcher';c.mkdir();v.mkdir();shutil.copytree(switcher,s)
  archive=c/'kmscon.tar.gz';urllib.request.urlretrieve(lock['url'],archive)
  if sha(archive)!=lock['sha256']:raise ValueError('kmscon checksum mismatch')
  for name in ['build.sh','patch.py','client.c','test-power.c']:shutil.copy2(console/name,c/name)
  for name in ['client.c','screencast.xml']:shutil.copy2(virtual/name,v/name)
  script='''set -euo pipefail
  # Fedora's container defaults omit docs, including COPYING files marked %doc.
  # Retain those original notices for the bundled native runtime dependencies.
  dnf --setopt=tsflags= install -y gcc gcc-c++ cmake qt6-qtbase-devel qt6-qtwayland kf6-kglobalaccel-devel libevdev-devel libinput-devel meson ninja-build ncurses libdrm-devel libxkbcommon-devel systemd-devel zlib-devel libtsm-devel libcurl-devel wayland-devel python3 tar gzip
  cd /work/console
  mkdir kmscon
  tar -xzf kmscon.tar.gz --strip-components=1 -C kmscon
  bash build.sh "$PWD"
  cd /work/virtual
  wayland-scanner client-header screencast.xml screencast-client.h
  wayland-scanner private-code screencast.xml screencast-code.c
  cc -O2 -Wall -Wextra -Werror client.c screencast-code.c -o xur-virtual-output $(pkg-config --cflags --libs wayland-client libsystemd)
  bash /work/switcher/build.sh /work/switcher
  '''
  try:subprocess.run(['docker','run','--rm','--volume',str(work.resolve())+':/work',image,'bash','-c',script],check=True)
  finally:subprocess.run(['sudo','chown','-R',str(os.getuid())+':'+str(os.getgid()),str(work)],check=True)
  out=repo/'.build/console-runtime';shutil.rmtree(out,ignore_errors=True);shutil.copytree(c/'output',out)
  inputs={p.name:sha(p) for p in sorted(console.iterdir()) if p.is_file()}
  files={str(p.relative_to(out)):sha(p) for p in sorted(out.rglob('*')) if p.is_file()}
  (out/'build-receipt.json').write_text(json.dumps({'inputs':inputs,'upstream':lock,'files':files},indent=2)+'\n')
  out=repo/'.build/virtual-display-runtime';shutil.rmtree(out,ignore_errors=True);out.mkdir()
  shutil.copy2(v/'xur-virtual-output',out/'xur-virtual-output')
  for name in ['COPYING','README.md']:shutil.copy2(virtual/name,out/name)
  (out/'build-receipt.json').write_text(json.dumps({'inputs':{p.name:sha(p) for p in sorted(virtual.iterdir()) if p.is_file()},'binary':sha(out/'xur-virtual-output')},indent=2)+'\n')
  out=repo/'.build/profile-switcher-runtime';shutil.rmtree(out,ignore_errors=True);shutil.copytree(s/'output',out)
  font=repo/'src/Xur.Control/wwwroot/fonts'
  for name in ('IBMPlexSans.ttf','OFL.txt'):shutil.copy2(font/name,out/'fonts'/name)
  shutil.copy2(repo/'LICENSE',out/'LICENSE')
  inputs={p.name:sha(p) for p in sorted(switcher.iterdir()) if p.is_file()}
  inputs.update({name:sha(font/name) for name in ('IBMPlexSans.ttf','OFL.txt')})
  files={str(p.relative_to(out)):sha(p) for p in sorted(out.rglob('*')) if p.is_file()}
  (out/'build-receipt.json').write_text(json.dumps({'inputs':inputs,'files':files},indent=2)+'\n')
if __name__=='__main__':build()
