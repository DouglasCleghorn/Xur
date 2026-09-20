#!/usr/bin/env python3
"""Package the ISO with scoped file-copy installation and current app evidence.

Unlike the broad initial release packager, this does not relabel older media
tests as results for a new ISO. Requires real file-copy install/reboot evidence.
"""
import hashlib,json,pathlib,re,shutil,tarfile
repo=pathlib.Path(__file__).resolve().parents[1];dist=repo/'dist'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
iso=dist/'xur-installer-x86_64.iso';digest=sha(iso)
receipt=json.loads((repo/'.build/evidence/file-copy-install.json').read_text())
assert receipt['result']=='Passed' and receipt['media']['isoSha256']==digest
assert all(receipt[k] for k in ('fat32ScannedReadOnly','usbParentProtected','realAnacondaInstall','offlinePayloadInstall','installedBootWithUsbAttached','sameCookieAcrossInstallAndReboot','passwordAccountHandedOff','nonTargetDiskUnchanged','usbUnchanged'))
embedded=json.loads((dist/'xur-installer-x86_64.embedded.json').read_text())
assert embedded['fat32Compatible']
bundle=repo/'.build/context/rootfs/usr/share/xur/app-bundle'
meta=json.loads((bundle/'bundle.json').read_text())
assert embedded['verifiedFiles']['usr/share/xur/app-bundle/bundle.json']==sha(bundle/'bundle.json')
for name,expected in meta['files'].items():assert sha(bundle/name)==expected,name
with tarfile.open(dist/'xur-app-x86_64.tar.gz') as tar:
    assert json.load(tar.extractfile('./bundle.json'))==meta
    for name,expected in meta['files'].items():assert hashlib.file_digest(tar.extractfile('./'+name),'sha256').hexdigest()==expected,name
app_evidence=[]
for name in ('custom-containers.json','profile-actions.json','account-reboot.json','manager-account.json'):
    path=repo/'.build/evidence/updates'/name;data=json.loads(path.read_text())
    assert data['result']=='Passed',name
    app_evidence.append({'file':str(path.relative_to(repo)),'bundle':data['bundle'],'currentBundle':data['bundle']==meta['id']})
current_evidence=[]
for name in ('unit-tests.json','profile-process-tests.json','control-process-tests.json','terminal-ownership.json'):
    path=repo/'.build/evidence'/name;data=json.loads(path.read_text())
    if name=='terminal-ownership.json':
        assert data['daemonHasNoControllingTerminalAfterReadAndWriteOpens'] and data['inputEchoAndCanonicalModeDisabledOnRealTerminal'],name
    else:assert data.get('passed') or data.get('result')=='Passed',name
    current_evidence.append(str(path.relative_to(repo)))
hybrid=[]
for name in ('fat32-cd','fat32-dd'):
    path=repo/'.build/evidence'/f'live-{name}.json';data=json.loads(path.read_text())
    assert data['media']['isoSha256']==digest and data['passed'], name
    hybrid.append(data)
raw=json.loads((repo/'.build/evidence/raw-hybrid-boot.json').read_text())
assert raw['media']['isoSha256']==digest and raw['rawUsbUnchanged'] and raw['bootParentProtected']
from source_files import source_files
files=source_files(repo)
secrets=set()
for path in (repo/'.build/vms').glob('*/console.private.log'):
    data=path.read_bytes()
    secrets.update(re.findall(rb'(?:One-time|Access) code: ([0-9A-HJKMNP-TV-Z]{3}-?[0-9A-HJKMNP-TV-Z]{3})',data))
    secrets.update(re.findall(rb'https://login\.tailscale\.com/[A-Za-z0-9/_-]+',data))
for path in (repo/'.build/vms').glob('*/session.private.cookies'):
    secrets.update(re.findall(rb'xur\.session="?([A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)',path.read_bytes()))
for path in (repo/'.build/vms').glob('*/account.private.json'):
    secrets.add(json.loads(path.read_text())['password'].encode())
secrets.update(s.replace(b'-',b'') for s in list(secrets) if re.fullmatch(rb'[0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3}',s))
credentials=re.compile(rb'-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----|hf_[A-Za-z0-9]{20,}|tskey-(?:auth|api)-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}')
for path in files+[p for p in bundle.rglob('*') if p.is_file()]:
    data=path.read_bytes()
    # Sources may contain the literal scan expression, but never key material.
    assert not re.search(rb'-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/=]{32}',data),str(path)
    assert not any(s in data for s in secrets),str(path)
    assert not re.search(rb'(?:hf_[A-Za-z0-9]{20,}|tskey-(?:auth|api)-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})',data),str(path)
shutil.copyfile(repo/'docs/usage/install.md',dist/'INSTALL.md')
source=dist/'xur-source.tar.gz'
with tarfile.open(source,'w:gz') as tar:
    for path in sorted(files):tar.add(path,arcname='Xur/'+str(path.relative_to(repo)),recursive=False)
acceptance={'schema':1,'scope':'FAT32 file-copy installation, hybrid boot regression and current application checks','isoSha256':digest,'fileCopy':receipt,'applicationBundle':meta['id'],'applicationEvidence':app_evidence,'currentApplicationChecks':current_evidence,'applicationChanges':json.loads((repo/'.build/evidence/fat32-app-diff.json').read_text()),'hybridBoot':hybrid,'rawHybridPreservation':raw,'limits':['Windows Rufus UI and Secure Boot were not executed.','Older receipts retain their original ISO identities; they are not new ISO tests.','See docs/development/remaining-work.md for outstanding product behavior.']}
(dist/'acceptance.json').write_text(json.dumps(acceptance,indent=2)+'\n')
manifest={'schema':1,'architecture':'x86_64','iso':{'file':iso.name,'sha256':digest,'bytes':iso.stat().st_size},'source':{'file':source.name,'sha256':sha(source)},'applicationBundle':{'id':meta['id'],'file':'xur-app-x86_64.tar.gz','sha256':sha(dist/'xur-app-x86_64.tar.gz')},'sourceFiles':{str(p.relative_to(repo)):sha(p) for p in sorted(files)},'images':json.loads((repo/'.build/evidence/media/images.json').read_text()),'toolchain':json.loads((repo/'eng/toolchain-lock.json').read_text()),'embeddedVerificationSha256':sha(dist/'xur-installer-x86_64.embedded.json'),'acceptanceSha256':sha(dist/'acceptance.json'),'privateArtifactScan':{'result':'Passed','knownSecretsChecked':len(secrets),'expandedBundleChecked':True}}
(dist/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
outputs=[iso,source,dist/'INSTALL.md',dist/'acceptance.json',dist/'build-manifest.json',dist/'xur-app-x86_64.tar.gz',dist/'xur-installer-x86_64.embedded.json']
(dist/'SHA256SUMS').write_text(''.join(f'{sha(p)}  {p.name}\n' for p in outputs))
print(json.dumps({'result':'Passed','isoSha256':digest,'bundle':meta['id'],'source':str(source)}))
