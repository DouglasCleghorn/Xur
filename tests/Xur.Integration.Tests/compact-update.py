#!/usr/bin/env python3
"""Exercise self-contained signed descriptors over HTTP without root activation."""
import functools,hashlib,http.server,importlib.machinery,importlib.util,json,pathlib,subprocess,tempfile,threading,urllib.error
repo=pathlib.Path(__file__).resolve().parents[2]
def load(name,path):
 loader=importlib.machinery.SourceFileLoader(name,str(path));spec=importlib.util.spec_from_loader(name,loader);module=importlib.util.module_from_spec(spec);loader.exec_module(module);return module
publisher=load('compact_publisher',repo/'eng/update-repository.py');verifier=load('compact_verifier',repo/'eng/verify-release.py')
(repo/'.build/evidence').mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=repo/'.build/evidence') as directory:
 root=pathlib.Path(directory);public=root/'public';public.mkdir();public_key=root/'key.pub'
 key=root/'key';subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True,capture_output=True)
 public_key.write_bytes(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout']))
 identity='a'*64;payload=public/(identity+'.tar.gz');payload.write_bytes(b'payload'*200000);original_payload=payload.read_bytes()
 entry=dict(schema=1,hostAbi=1,dataSchema=1,id=identity,version='1.0',sequence=10,channel='development',file=payload.name,bytes=payload.stat().st_size,sha256=hashlib.file_digest(payload.open('rb'),'sha256').hexdigest())
 iso=root/'fixture.iso';iso.write_bytes(b'iso')
 descriptor=publisher.compact(public,entry,key,{'iso':{'sha256':hashlib.sha256(b'iso').hexdigest(),'bytes':3}});original=descriptor.read_bytes();(public/'current').write_text(identity)
 assert verifier.verify(descriptor,public_key,payload,iso)['version']=='1.0'
 iso.write_bytes(b'bad')
 try:verifier.verify(descriptor,public_key,payload,iso)
 except ValueError:pass
 else:raise AssertionError('Corrupt ISO accepted')
 assert descriptor.stat().st_size<2048
print(json.dumps({'suite':'CompactPublication','result':'Passed','checkBytesUnder':2048,'isoVerification':True}))
import os,shutil
sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release','--','--updates'],cwd=repo,check=True)
