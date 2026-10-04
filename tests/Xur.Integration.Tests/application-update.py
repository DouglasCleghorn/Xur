#!/usr/bin/env python3
"""Compatibility entry point for the C# startup and signed-updater fixture suite."""
import os,pathlib,shutil,subprocess
repo=pathlib.Path(__file__).resolve().parents[2]
sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release'],cwd=repo,check=True)

import importlib.util,json,tempfile,hashlib
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
