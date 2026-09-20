#!/usr/bin/env python3
"""Collect the current Fedora build and local test receipts, without rebuilding."""
import hashlib,json,os,pathlib,shutil,subprocess,time
repo=pathlib.Path(__file__).resolve().parents[1];os.chdir(repo)
root=pathlib.Path(os.environ.get('XUR_BUILD_ROOT',pathlib.Path.home()/'.local/share/xur-build'))
vm=root/'vm';out=repo/'.build/evidence';media=out/'media';media.mkdir(parents=True,exist_ok=True)
options=['-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',f'UserKnownHostsFile={vm}/known_hosts','-i',str(vm/'builder_ed25519')]
ssh=['ssh',*options,'-p','22220','builder@127.0.0.1']
scp=['scp','-q',*options,'-P','22220']
def run(command):return subprocess.run(command,check=True,capture_output=True,text=True).stdout
def sha(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
iso=repo/'dist/xur-installer-x86_64.iso';digest=sha(iso)
assert run(ssh+['sha256sum xur-output/bootiso/install.iso']).split()[0]==digest,'Builder ISO differs from dist'
run(scp+['eng/collect-build-metadata.py','eng/inspect-media.sh','builder@127.0.0.1:.'])
inspection='release-inspect-'+str(time.time_ns())
run(ssh+[f'sudo bash inspect-media.sh xur-output/bootiso/install.iso {inspection}'])
run(ssh+[f'sudo python3 collect-build-metadata.py {inspection}'])
for name in ('images.json','host-rpm-packages.txt','installer-rpm-packages.txt','embedded-verification.json','boot-layout.txt','uefi-grub.cfg','bios-grub.cfg'):
    run(scp+[f'builder@127.0.0.1:{inspection}/{name}',str(media/name)])
run(scp+['builder@127.0.0.1:xur-output/xur-manifest.json',str(media/'osbuild-manifest.json')])
run(scp+['builder@127.0.0.1:xur-build/publish-receipt.json',str(media/'context-receipt.json')])
run(ssh+[f'sudo rm -rf -- /home/builder/{inspection}'])
shutil.copyfile(media/'embedded-verification.json',repo/'dist/xur-installer-x86_64.embedded.json')
for source,target in [('build-unit-tests.log','unit-tests.json'),('build-profile-tests.log','profile-process-tests.json'),('build-process-tests.json','control-process-tests.json'),('build-terminal-tests.json','terminal-ownership.json')]:
    lines=(repo/'.build'/source).read_text().splitlines()
    data=json.loads(next(line for line in reversed(lines) if line.startswith('{')))
    (out/target).write_text(json.dumps(data,indent=2)+'\n')
embedded=json.loads((media/'embedded-verification.json').read_text())
(out/'build-script.json').write_text(json.dumps({'result':'Passed','isoSha256':digest,'script':'eng/build-iso.sh','finalEmbeddedFilesVerified':len(embedded['verifiedFiles'])},indent=2)+'\n')
print(json.dumps({'isoSha256':digest,'embeddedFiles':len(embedded['verifiedFiles']),'evidence':str(out)}))
