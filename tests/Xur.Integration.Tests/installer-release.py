#!/usr/bin/env python3
"""Exercise installer receipt verification, signed splitting, and rejection before upload."""
import hashlib,importlib.util,json,pathlib,subprocess,tempfile
repo=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('installer',repo/'eng/ci-installer.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
with tempfile.TemporaryDirectory() as t:
 root=pathlib.Path(t);m.ROOT=root;dist=root/'dist';dist.mkdir();docs=root/'docs/usage';docs.mkdir(parents=True);(docs/'install.md').write_text('Fixture instructions')
 iso=dist/m.ISO;iso.write_bytes(b'xur-fixture-iso'*100)
 report=dist/'xur-installer-x86_64.embedded.json';report.write_text(json.dumps({**{k:True for k in m.REQUIRED},'verifiedFiles':{str(n):'hash' for n in range(101)}}))
 commit='a'*40;m.candidate(commit,'nightly');receipt=dist/'installer-build.json';original=receipt.read_bytes()
 key=root/'key';pub=root/'pub';subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True,capture_output=True);pub.write_bytes(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout']))
 for limit in [2000,512]:
  m.PART_SIZE=limit;out=root/str(limit);files=m.assets(dist,out,commit,'nightly',key)
  meta=json.loads((out/'installer.json').read_text());assembled=b''.join((out/p['file']).read_bytes() for p in meta['parts'])
  assert assembled==iso.read_bytes() and all(p['bytes']<=limit for p in meta['parts'])
  assert all(hashlib.sha256((out/p['file']).read_bytes()).hexdigest()==p['sha256'] for p in meta['parts'])
  assert meta['installationTest']=='Not run'
  subprocess.run(['openssl','pkeyutl','-verify','-pubin','-inkey',str(pub),'-rawin','-in',str(out/'installer.json'),'-sigfile',str(out/'installer.json.sig')],check=True,capture_output=True)
 def reject():
  try:m.assets(dist,root/'rejected',commit,'nightly',key)
  except ValueError:pass
  else:raise AssertionError('Invalid installer candidate accepted')
 for field,value in [('commit','b'*40),('channel','stable')]:
  data=json.loads(original);data[field]=value;receipt.write_text(json.dumps(data));reject();receipt.write_bytes(original)
 data=iso.read_bytes();iso.write_bytes(data+b'tampered');reject();iso.write_bytes(data)
 data=report.read_bytes();report.write_bytes(data+b' ');reject();report.write_bytes(data)
print(json.dumps({'suite':'InstallerRelease','result':'Passed','signedDescriptor':True,'splitAndReassembled':True,'tamperingRejected':True,'actualIsoBuild':'Not run'}))
