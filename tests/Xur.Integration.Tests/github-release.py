#!/usr/bin/env python3
"""Verify publication inputs without network access, GitHub credentials or a release."""
import hashlib,importlib.util,json,pathlib,subprocess,sys,tempfile
repo=pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0,str(repo/'eng'))
from source_files import source_files
spec=importlib.util.spec_from_file_location('publish',repo/'eng/publish-github.py');publish=importlib.util.module_from_spec(spec);spec.loader.exec_module(publish)
def sha(path):
 with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
with tempfile.TemporaryDirectory() as directory:
 root=pathlib.Path(directory);publish.ROOT=root
 public=root/'.build/update-repository';public.mkdir(parents=True)
 output=root/'dist/updates/1.0';output.mkdir(parents=True)
 key=root/'os/bootc/application-update-key.pem';key.parent.mkdir(parents=True)
 private=root/'.build/key'
 subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(private)],check=True,capture_output=True)
 subprocess.run(['openssl','pkey','-in',str(private),'-pubout','-out',str(key)],check=True,capture_output=True)
 (root/'README.md').write_text('Fixture source\n')
 identity='a'*64;(public/'latest').write_text(identity)
 archive=public/(identity+'.tar.gz');archive.write_bytes(b'archive fixture')
 descriptor=public/(identity+'.json');descriptor.write_text(json.dumps({'version':'1.0','file':archive.name,'bytes':archive.stat().st_size,'sha256':sha(archive)}))
 subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(private),'-rawin','-in',str(descriptor),'-out',str(descriptor)+'.sig'],check=True,capture_output=True)
 source=output/'xur-source.tar.gz';source.write_bytes(b'source fixture')
 receipt={'sourceFiles':{str(p.relative_to(root)):sha(p) for p in source_files(root)},'sourceSha256':sha(source)}
 (output/'manifest.json').write_text(json.dumps(receipt))
 assert len(publish.assets('1.0'))==5
 for path in [source,archive,root/'README.md',pathlib.Path(str(descriptor)+'.sig')]:
  original=path.read_bytes();path.write_bytes(original+b'changed')
  try:publish.assets('1.0')
  except (ValueError,subprocess.CalledProcessError):pass
  else:raise AssertionError('Changed publication input accepted: '+str(path))
  path.write_bytes(original)
 try:publish.assets('../1.0')
 except ValueError:pass
 else:raise AssertionError('Invalid release version accepted')
print(json.dumps({'suite':'GitHubRelease','result':'Passed','signedAssets':True,'sourceMatches':True,'tamperingRejected':True,'published':False}))
