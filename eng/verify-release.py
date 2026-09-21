#!/usr/bin/env python3
"""Verify signed release JSON and optionally the matching app archive and ISO."""
import argparse,base64,hashlib,json,pathlib,subprocess,tempfile
ROOT=pathlib.Path(__file__).resolve().parents[1]
def verify(descriptor,key,archive=None,iso=None):
 if descriptor.stat().st_size>65536:raise ValueError('Descriptor exceeds size limit')
 envelope=json.loads(descriptor.read_text())
 if envelope.get('schema')!=2:raise ValueError('Unsupported descriptor schema')
 entry=envelope['release']
 with tempfile.TemporaryDirectory() as directory:
  stage=pathlib.Path(directory)
  (stage/'metadata').write_text(json.dumps(entry,sort_keys=True,separators=(',',':')))
  signature=base64.b64decode(envelope['signature'],validate=True)
  if len(signature)!=64:raise ValueError('Invalid signature length')
  (stage/'signature').write_bytes(signature)
  subprocess.run(['openssl','pkeyutl','-verify','-pubin','-inkey',str(key),'-rawin','-in',str(stage/'metadata'),'-sigfile',str(stage/'signature')],check=True,capture_output=True)
 if entry.get('schema')!=2:raise ValueError('Unsupported release schema')
 for path,expected in [(archive,entry),(iso,entry.get('installer',{}).get('iso',{}))]:
  if path is not None:
   with path.open('rb') as stream:
    if path.stat().st_size!=expected['bytes'] or hashlib.file_digest(stream,'sha256').hexdigest()!=expected['sha256']:raise ValueError('Hash or length mismatch: '+str(path))
 return entry
if __name__=='__main__':
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('descriptor',type=pathlib.Path);parser.add_argument('--archive',type=pathlib.Path);parser.add_argument('--iso',type=pathlib.Path);parser.add_argument('--key',type=pathlib.Path,default=ROOT/'os/bootc/application-update-key.pem');args=parser.parse_args()
 print(json.dumps(verify(args.descriptor,args.key,args.archive,args.iso),indent=2))
