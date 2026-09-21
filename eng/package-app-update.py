#!/usr/bin/env python3
"""Package source/evidence and promote an already tested, signed staging bundle."""
import argparse,hashlib,json,os,pathlib,re,shutil,subprocess,tarfile
repo=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--version',required=True);p.add_argument('--skip-vm-checks',action='store_true',help='Explicit build-only release; record skipped validation');a=p.parse_args();assert re.fullmatch(r'[0-9.]+',a.version)
stage=repo/'.build/update-staging';public=repo/'.build/update-repository';identity=(stage/'latest').read_text().strip();assert re.fullmatch('[a-f0-9]{64}',identity)
entry=json.loads((stage/(identity+'.json')).read_text());assert entry['version']==a.version and entry['id']==identity and entry['file']==identity+'.tar.gz'
trusted=repo/'os/bootc/application-update-key.pem'
if custom:=os.environ.get('XUR_LOCAL_SIGNING_KEY'):
 assert entry.get('channel')=='development','Custom keys cannot publish official releases'
 expected=subprocess.check_output(['openssl','pkey','-in',custom,'-pubout'])
 trusted=stage/'application-update-key.pem'
 assert trusted.read_bytes()==expected,'Staged key differs from the selected contributor key'
subprocess.run(['openssl','pkeyutl','-verify','-pubin','-inkey',str(trusted),'-rawin','-in',str(stage/(identity+'.json')),'-sigfile',str(stage/(identity+'.json.sig'))],check=True,stdout=subprocess.DEVNULL)
def sha(path):
 with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
assert sha(stage/entry['file'])==entry['sha256']
verified=json.loads(subprocess.check_output(['python3',str(repo/'eng/verify-release.py'),str(stage/(identity+'.update.json')),'--key',str(trusted),'--archive',str(stage/entry['file'])],text=True))
assert verified=={**entry,'schema':2},'Compact and legacy descriptors differ'
evidence=repo/'.build/evidence/updates'/a.version
if a.skip_vm_checks:
 evidence.mkdir(parents=True,exist_ok=True)
 assert not any(evidence.iterdir()),'Build-only evidence directory must be fresh'
 (evidence/'validation.json').write_text(json.dumps({'result':'Skipped','bundleId':identity,'mode':'build-only','reason':'Explicit operator request to build and publish without tests'},indent=2)+'\n')
else:
 receipt=json.loads((evidence/'signed-update.json').read_text());assert receipt['result']=='Passed' and receipt['bundleId']==identity
 for name in ('model-persistence.json','diagnostics-download.json'):
  test=json.loads((evidence/name).read_text());assert test['result']=='Passed' and test['bundleId']==identity,name
subprocess.run(['python3',str(repo/'eng/context-receipt.py'),'verify',str(repo/'.build/context')],check=True)
bundle=repo/'.build/context/rootfs/usr/share/xur/app-bundle';assert json.loads((bundle/'bundle.json').read_text())['id']==identity
from source_files import source_files
files=source_files(repo)
secrets=set()
for f in (repo/'.build/vms').glob('*/account.private.json'):secrets.add(json.loads(f.read_text())['password'].encode())
for f in (repo/'.build/vms').glob('*/console.private.log'):
 secrets.update(re.findall(rb'(?:One-time|Access) code: ([0-9A-HJKMNP-TV-Z]{3}-?[0-9A-HJKMNP-TV-Z]{3})',f.read_bytes()))
 secrets.update(re.findall(rb'https://login\.tailscale\.com/[A-Za-z0-9/_-]+',f.read_bytes()))
secrets.update(s.replace(b'-',b'') for s in list(secrets) if re.fullmatch(rb'[0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3}',s))
for f in files+[f for f in bundle.rglob('*') if f.is_file()]:
 data=f.read_bytes();assert not any(s in data for s in secrets),f
 # This exact upstream GnuTLS library embeds five public crypto self-test keys,
 # verified against gnutls/gnutls tag 3.7.3 lib/crypto-selftests-pk.c.
 public_fixture=f.is_relative_to(bundle/'agent/streaming/usr/lib') and hashlib.sha256(data).hexdigest()=='90b9926b88dbf2c82df1b7ee1d1ed4735b4ce2c2648b06a0bfff72761c1d7ea2'
 assert public_fixture or not re.search(rb'-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/=]{32}',data),f
 assert not re.search(rb'(?:xur_[a-f0-9]{32}_[a-f0-9]{64}|hf_[A-Za-z0-9]{20,}|tskey-(?:auth|api)-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})',data),f
out=repo/'dist/updates'/a.version;out.mkdir(parents=True,exist_ok=False)
source=out/'xur-source.tar.gz'
with tarfile.open(source,'w:gz') as tar:
 for f in sorted(files):tar.add(f,arcname='Xur/'+str(f.relative_to(repo)),recursive=False)
manifest={'schema':1,'version':a.version,'application':entry,'sourceSha256':sha(source),'sourceFiles':{str(f.relative_to(repo)):sha(f) for f in sorted(files)},'privateArtifactScan':'Passed','isoRebuilt':False,'validation':'Skipped (build-only)' if a.skip_vm_checks else 'Passed'}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n');shutil.copytree(evidence,out/'evidence',dirs_exist_ok=True)
(out/'SHA256SUMS').write_text(''.join(sha(f)+'  '+f.name+'\n' for f in [source,out/'manifest.json']))
# Publish the exact staged archive and signature, preserving validation provenance.
public.mkdir(parents=True,exist_ok=True)
for name in [entry['file'],identity+'.json',identity+'.json.sig',identity+'.update.json','application-update-key.pem']:
 temporary=public/(name+'.tmp');shutil.copyfile(stage/name,temporary);temporary.replace(public/name)
latest=public/'latest.tmp';latest.write_text(identity);latest.replace(public/'latest')
current=public/'current.tmp';current.write_text(identity);current.replace(public/'current')
print(json.dumps({'result':'Published','version':a.version,'bundle':identity,'source':str(source),'privateArtifactScan':'Passed'}))
