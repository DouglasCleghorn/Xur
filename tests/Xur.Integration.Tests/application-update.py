#!/usr/bin/env python3
"""Signed metadata and hostile archive checks, without root operations."""
import hashlib,importlib.machinery,importlib.util,io,json,pathlib,sqlite3,subprocess,tarfile,tempfile
repo=pathlib.Path(__file__).resolve().parents[2]
loader=importlib.machinery.SourceFileLoader('updater',str(repo/'os/bootc/app-update'));spec=importlib.util.spec_from_loader(loader.name,loader);u=importlib.util.module_from_spec(spec);loader.exec_module(u)
assert u.normalized('192.168.0.134')=='http://192.168.0.134:8088'
assert u.normalized('test.local:9090')=='http://test.local:9090'
assert u.normalized('http://[fd00::1]:8088')=='http://[fd00::1]:8088'
for bad in ['file:///etc/passwd','http://user:pass@host','http://host/path','http://host?key=value']:
 try:u.normalized(bad)
 except ValueError:pass
 else:raise AssertionError(bad)
with tempfile.TemporaryDirectory() as t:
 root=pathlib.Path(t);u.ROOT=root;u.CONFIG=root/'config';u.KEY=repo/'os/bootc/application-update-key.pem'
 for name,kind in [('../escape','file'),('/escape','file'),('symlink','link'),('device','device')]:
  archive=root/'hostile.tgz'
  with tarfile.open(archive,'w:gz') as tar:
   m=tarfile.TarInfo(name)
   if kind=='link':m.type=tarfile.SYMTYPE;m.linkname='/etc'
   if kind=='device':m.type=tarfile.CHRTYPE
   tar.addfile(m)
  try:u.unpack(archive,root/'extracted',{'id':'a'*64})
  except ValueError:pass
  else:raise AssertionError(name)
 descriptor=root/'release';descriptor.write_text('tampered');signature=root/'signature';signature.write_bytes(b'x'*64)
 result=subprocess.run(['openssl','pkeyutl','-verify','-pubin','-inkey',str(u.KEY),'-rawin','-in',str(descriptor),'-sigfile',str(signature)],capture_output=True)
 assert result.returncode!=0
with tempfile.TemporaryDirectory() as t:
 state=pathlib.Path(t);u.ROOT=state/'app';u.ROOT.mkdir();entry={'id':'a'*64}
 target=u.ROOT/'releases'/entry['id']/'host';target.mkdir(parents=True)
 db=sqlite3.connect(state/'profiles.db');db.execute('CREATE TABLE documents(kind TEXT,id TEXT,json TEXT)')
 def save(value,kind='profile'):
  db.execute('DELETE FROM documents');db.execute('INSERT INTO documents VALUES (?,?,?)',(kind,'current',json.dumps(value)));db.commit()
 def reject():
  try:u.compatible(entry)
  except ValueError as e:assert 'workstation users' in str(e)
  else:raise AssertionError('Older version accepted user-dependent state')
 legacy={'Recipe':{},'User':None};save({'Workloads':[legacy]});u.compatible(entry)
 for user in [{'Username':'alex','Uid':1001,'Temporary':False},{'Username':'','Uid':0,'Temporary':True}]:
  workload={'Recipe':{},'User':user};save({'Workloads':[workload]});reject()
  (target/'application-features.json').write_text('["station-users-v1"]');u.compatible(entry)
  (target/'application-features.json').unlink()
 save({'Stage':'Failed','Plan':{'Target':{'Workloads':[workload]}}},'journal');reject()
 save({'Stage':'Complete','Plan':{'Target':{'Workloads':[workload]}}},'journal');u.compatible(entry)
 receipts=state/'workloads';receipts.mkdir();(receipts/'station.json').write_text(json.dumps(workload));reject()
 # activate must reject before maintenance, stopping services, or changing links.
 u.current=lambda:{'id':'b'*64};u.progress=lambda *args:(_ for _ in ()).throw(AssertionError('Mutated before compatibility check'))
 try:u.activate(entry)
 except ValueError:pass
 else:raise AssertionError('Incompatible activation accepted')
 db.close()
 (target/'application-features.json').write_text('["station-users-v1"]')
 (state/'manager-account.json').write_text('{}')
 try:u.compatible(entry)
 except ValueError as error:assert 'username and password' in str(error)
 else:raise AssertionError('Password account could roll back to token-only version')
 (target/'application-features.json').write_text('["station-users-v1","manager-account-v1"]')
 u.compatible(entry)
 selected=state/'catalog-selected';selected.mkdir();(selected/'custom.json').write_text('{"kind":"Container"}')
 try:u.compatible(entry)
 except ValueError as error:assert 'containers' in str(error)
 else:raise AssertionError('Container catalog could roll back to incompatible version')
 (target/'application-features.json').write_text('["station-users-v1","manager-account-v1","container-workloads-v1"]');u.compatible(entry)

 (state/'station-seats.json').write_text('[]')
 try:u.compatible(entry)
 except ValueError as error:assert 'multiseat' in str(error)
 else:raise AssertionError('Seat-managed installation could roll back to shared input runtime')
 (target/'application-features.json').write_text('["station-users-v1","manager-account-v1","container-workloads-v1","multiseat-v1"]');u.compatible(entry)

print(json.dumps({'suite':'ApplicationUpdateBoundaries' ,'result':'Passed','serverAddressValidation':True,'archiveTraversalLinksDevicesRejected':True,'invalidSignatureRejected':True,'workstationUserRollbackCompatibility':True,'incompatibleActivationDoesNotMutate':True,'managerAccountCompatibility':True,'containerCompatibility':True}))

# Development opt-in never bypasses the production signing key or rollback guard.
with tempfile.TemporaryDirectory() as t:
 root=pathlib.Path(t);u.ROOT=root;u.CONFIG=root/'config'
 assert u.source()==u.PUBLIC
 try:u.configure('127.0.0.1:8088')
 except ValueError:pass
 else:raise AssertionError('Local source accepted without opt-in')
 u.development(True);u.configure('127.0.0.1:8088');assert u.source()=='http://127.0.0.1:8088'
 (root/'available.json').write_text('{}');u.development(False)
 assert u.source()==u.PUBLIC and not (root/'available.json').exists()
 u.development(True);assert u.source()=='http://127.0.0.1:8088'
 for url in ['http://github.com/DouglasCleghorn/Xur/releases/download/v1/latest','https://evil.example/file','https://github.com/other/repo/releases/download/v1/file','https://user:pass@release-assets.githubusercontent.com/file','https://release-assets.githubusercontent.com:444/file']:
  assert not u.github_asset(url),url
 assert u.github_asset('https://github.com/DouglasCleghorn/Xur/releases/download/v1/latest')
 assert u.github_asset('https://release-assets.githubusercontent.com/release-asset?signature=fixture')
 # Sign real fixture metadata with an ephemeral test key. Repoint latest during
 # the check: descriptor/signature/payload must remain on the originally resolved tag.
 key=root/'key';u.KEY=root/'pub';subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True,capture_output=True)
 u.KEY.write_bytes(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout']))
 identity='a'*64
 entry=dict(schema=1,hostAbi=1,dataSchema=1,id=identity,file=identity+'.tar.gz',version='1.2',sequence=10,bytes=20,sha256='b'*64)
 descriptor=root/'fixture.json';signature=root/'fixture.sig'
 def sign():
  descriptor.write_text(json.dumps(entry));subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(key),'-rawin','-in',str(descriptor),'-out',str(signature)],check=True,capture_output=True)
 sign();u.development(False);urls=[]
 def fetch(url,path,limit):
  urls.append(url)
  if url==u.PUBLIC+'/latest':path.write_text(identity);return u.GITHUB+'/download/v1.2'
  if url==u.GITHUB+'/download/v1.2/'+identity+'.json':path.write_bytes(descriptor.read_bytes());return
  if url==u.GITHUB+'/download/v1.2/'+identity+'.json.sig':path.write_bytes(signature.read_bytes());return
  raise AssertionError('Metadata escaped pinned release: '+url)
 original_fetch=u.fetch;u.fetch=fetch
 stage=root/'stage';stage.mkdir();assert u.check(stage)==entry
 assert (stage/'source').read_text()==u.GITHUB+'/download/v1.2'
 descriptor.write_text(descriptor.read_text()+' ')
 try:u.check(stage)
 except subprocess.CalledProcessError:pass
 else:raise AssertionError('Tampered signed metadata accepted')
 sign();(root/'highest-sequence.json').write_text('11')
 try:u.check(stage)
 except ValueError as e:assert 'older release' in str(e)
 else:raise AssertionError('Downgrade accepted')
 # Each signed channel has its own replay floor; selecting stable after a newer
 # nightly remains possible without accepting older metadata in either channel.
 u.select_channel('nightly');assert u.source()==u.NIGHTLY
 entry['channel']='nightly';entry['sequence']=20;sign()
 def channel_fetch(url,path,limit):
  if url==u.NIGHTLY+'/latest':path.write_text('nightly-1.3');return
  if url==u.GITHUB+'/download/nightly-1.3/latest':path.write_text(identity);return
  if url.startswith(u.GITHUB+'/download/nightly-1.3/'):
   path.write_bytes(signature.read_bytes() if url.endswith('.sig') else descriptor.read_bytes());return
  return fetch(url,path,limit)
 u.fetch=channel_fetch;assert u.check(stage)==entry
 assert (stage/'source').read_text()==u.GITHUB+'/download/nightly-1.3'
 (root/'channel-sequences.json').write_text(json.dumps({'nightly':20,'stable':5}))
 u.select_channel('stable');entry['channel']='stable';entry['sequence']=6;sign()
 assert u.check(stage)==entry # Older than nightly and legacy global floor.
 entry['sequence']=4;sign()
 try:u.check(stage)
 except ValueError as e:assert 'older release' in str(e)
 else:raise AssertionError('Same-channel replay accepted')
 entry['sequence']=21;entry['channel']='nightly';sign()
 try:u.check(stage)
 except ValueError as e:assert 'selected channel' in str(e)
 else:raise AssertionError('Wrong signed channel accepted')
 u.fetch=original_fetch
print(json.dumps({'suite':'UpdateSources','result':'Passed','localOptIn':True,'pinnedGitHubRelease':True,'signedMetadata':True,'rollbackGuard':True}))

# Contributor keys are explicit, confined to local sources, and independently replay-protected.
with tempfile.TemporaryDirectory() as t:
 root=pathlib.Path(t);u.ROOT=root;u.CONFIG=root/'config';u.KEY=root/'official.pem'
 def make_key(name):
  private=root/name
  subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(private)],check=True,capture_output=True)
  return private,subprocess.check_output(['openssl','pkey','-in',str(private),'-pubout']).decode()
 official,official_public=make_key('official');contributor,custom_public=make_key('contributor');u.KEY.write_text(official_public)
 u.select_channel('local','192.0.2.10:8088',custom_public)
 assert u.channel()=='local' and u.trust_key()==custom_public and u.KEY.read_text()==official_public
 original_config=u.CONFIG.read_bytes()
 for bad in [contributor.read_text(),'',custom_public+'junk',custom_public+custom_public,'-----BEGIN PUBLIC KEY-----\ninvalid\n-----END PUBLIC KEY-----']:
  try:u.select_channel('local','192.0.2.10:8088',bad)
  except ValueError:pass
  else:raise AssertionError('Invalid public key accepted')
  assert u.CONFIG.read_bytes()==original_config,'Invalid settings changed the active source'
 rsa=root/'rsa';subprocess.run(['openssl','genpkey','-algorithm','RSA','-pkeyopt','rsa_keygen_bits:2048','-out',str(rsa)],check=True,capture_output=True)
 try:u.public_key(subprocess.check_output(['openssl','pkey','-in',str(rsa),'-pubout']).decode())
 except ValueError:pass
 else:raise AssertionError('Unsupported key accepted')
 identity='a'*64;entry=dict(schema=1,hostAbi=1,dataSchema=1,id=identity,file=identity+'.tar.gz',version='1',channel='development',sequence=1,bytes=20,sha256='b'*64)
 descriptor=root/'signed.json';signature=root/'signed.sig';stage=root/'stage';stage.mkdir()
 def sign_with(private):
  descriptor.write_text(json.dumps(entry));subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(private),'-rawin','-in',str(descriptor),'-out',str(signature)],check=True,capture_output=True)
 def fetch_fixture(url,path,limit):
  if url.endswith('/latest'):path.write_text(identity);return u.GITHUB+'/download/v1' if url.startswith(u.PUBLIC) else None
  path.write_bytes(signature.read_bytes() if url.endswith('.sig') else descriptor.read_bytes())
 u.fetch=fetch_fixture;sign_with(contributor);assert u.check(stage)==entry
 scope=u.sequence_scope();u.remember_sequence(entry,scope)
 assert not (root/'highest-sequence.json').exists()
 entry['sequence']=0;sign_with(contributor)
 try:u.check(stage)
 except ValueError:pass
 else:raise AssertionError('Local replay accepted')
 u.select_channel('local','192.0.2.11:8088',custom_public);assert u.sequence_scope()!=scope
 assert u.check(stage)==entry
 u.select_channel('stable');assert u.source()==u.PUBLIC and u.trust_key()==official_public
 assert u.read(u.CONFIG)['publicKey']==custom_public and not (root/'available.json').exists()
 entry['channel']='stable';entry['sequence']=2;sign_with(contributor)
 try:u.check(stage)
 except subprocess.CalledProcessError:pass
 else:raise AssertionError('Contributor key accepted for official release')
 sign_with(official);assert u.check(stage)==entry
 u.remember_sequence(entry,'stable');assert u.read(root/'channel-sequences.json')['stable']==2
 u.select_channel('local','192.0.2.10:8088',custom_public)
 try:u.check(stage)
 except subprocess.CalledProcessError:pass
 else:raise AssertionError('Local key selection ignored')
 u.fetch=original_fetch
print(json.dumps({'suite':'ContributorUpdateTrust','result':'Passed','independentKeys':True,'privateAndMalformedKeysRejected':True,'localReplayProtection':True,'officialTrustPreserved':True}))

# The contributor publishing path signs with their key without editing the tracked key.
with tempfile.TemporaryDirectory() as t:
 import os
 root=pathlib.Path(t);spec=importlib.util.spec_from_file_location('repository',repo/'eng/update-repository.py');publisher=importlib.util.module_from_spec(spec);spec.loader.exec_module(publisher)
 publisher.ROOT=root;publisher.PUBLIC=root/'public';publisher.PUB=root/'official.pem';publisher.PUB.write_bytes((repo/'os/bootc/application-update-key.pem').read_bytes());original=publisher.PUB.read_bytes()
 bundle=root/'.build/context/rootfs/usr/share/xur/app-bundle';bundle.mkdir(parents=True);(bundle/'fixture').write_bytes(b'fixture')
 files={'fixture':hashlib.sha256(b'fixture').hexdigest()};identity=hashlib.sha256(json.dumps(files,sort_keys=True).encode()).hexdigest();(bundle/'bundle.json').write_text(json.dumps({'id':identity,'files':files}))
 previous=os.environ.get('XUR_LOCAL_SIGNING_KEY');os.environ['XUR_LOCAL_SIGNING_KEY']=str(root/'private/contributor.pem')
 try:
  publisher.publish('1.0');assert publisher.PUB.read_bytes()==original
  key=pathlib.Path(os.environ['XUR_LOCAL_SIGNING_KEY']);assert key.stat().st_mode&0o777==0o600
  exported=publisher.PUBLIC/'application-update-key.pem';assert exported.read_bytes()!=original
  subprocess.run(['openssl','pkeyutl','-verify','-pubin','-inkey',str(exported),'-rawin','-in',str(publisher.PUBLIC/(identity+'.json')),'-sigfile',str(publisher.PUBLIC/(identity+'.json.sig'))],check=True,capture_output=True)
  try:publisher.publish('1.0','nightly')
  except ValueError:pass
  else:raise AssertionError('Contributor key used for public release')
 finally:
  if previous is None:os.environ.pop('XUR_LOCAL_SIGNING_KEY',None)
  else:os.environ['XUR_LOCAL_SIGNING_KEY']=previous
print(json.dumps({'suite':'ContributorPublication','result':'Passed','customSignatureVerified':True,'officialKeyUnchanged':True,'officialPublicationRejected':True}))
