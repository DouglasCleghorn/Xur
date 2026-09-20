#!/usr/bin/env python3
"""Bind inspected installer media to a commit, then prepare signed release assets.

Does not claim boot/installation tests. Large ISOs are split below GitHub's 2 GiB
per-asset limit; the signed descriptor records every part and the assembled hash.
"""
import argparse,hashlib,json,pathlib,re,shutil,subprocess
ROOT=pathlib.Path(__file__).resolve().parents[1]
ISO='xur-installer-x86_64.iso'
PART_SIZE=1900*1024**2
REQUIRED=('safeKickstartTemplate','uefiAndBiosLayout','enforcementNotDisabled','fat32Compatible','onlineInstaller')
def sha(path):
 with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def identity(commit,channel):
 if not re.fullmatch('[a-f0-9]{40}',commit) or channel not in ('nightly','stable'):raise ValueError('Invalid installer build identity')
def inspected(path):
 report=json.loads(path.read_text())
 if not all(report.get(k) is True for k in REQUIRED) or len(report.get('verifiedFiles',{}))<100:raise ValueError('Installer embedded inspection did not pass')
 return report
def candidate(commit,channel):
 identity(commit,channel);dist=ROOT/'dist';iso=dist/ISO;inspection=dist/'xur-installer-x86_64.embedded.json';inspected(inspection)
 receipt={'schema':1,'commit':commit,'channel':channel,'iso':{'file':ISO,'bytes':iso.stat().st_size,'sha256':sha(iso)},'inspectionSha256':sha(inspection),'checks':'Embedded files, boot layout, signature policy and online-only payload inspected','installationTest':'Not run','physicalHardwareTest':'Not run'}
 (dist/'installer-build.json').write_text(json.dumps(receipt,indent=2)+'\n')
def assets(artifact,output,commit,channel,key):
 identity(commit,channel)
 receipt=json.loads((artifact/'installer-build.json').read_text());iso=artifact/ISO;inspection=artifact/'xur-installer-x86_64.embedded.json'
 if receipt.get('schema')!=1 or receipt.get('commit')!=commit or receipt.get('channel')!=channel:raise ValueError('Installer candidate identity mismatch')
 if receipt['iso']!={'file':ISO,'bytes':iso.stat().st_size,'sha256':sha(iso)} or receipt['inspectionSha256']!=sha(inspection):raise ValueError('Installer candidate hash mismatch')
 inspected(inspection);output.mkdir(parents=True,exist_ok=True);parts=[];files=[]
 if iso.stat().st_size<=PART_SIZE:
  target=output/ISO;shutil.copyfile(iso,target);files.append(target)
 else:
  with iso.open('rb') as source:
   number=1
   while True:
    chunk=source.read(min(1024*1024,PART_SIZE))
    if not chunk:break
    target=output/(ISO+'.part'+str(number).zfill(3));number+=1
    with target.open('wb') as dest:
     dest.write(chunk);remaining=PART_SIZE-len(chunk)
     while remaining>0:
      chunk=source.read(min(1024*1024,remaining))
      if not chunk:break
      dest.write(chunk);remaining-=len(chunk)
    files.append(target)
 for path in files:parts.append({'file':path.name,'bytes':path.stat().st_size,'sha256':sha(path)})
 descriptor=output/'installer.json';descriptor.write_text(json.dumps({**receipt,'parts':parts},indent=2)+'\n')
 signature=output/'installer.json.sig'
 subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(key),'-rawin','-in',str(descriptor),'-out',str(signature)],check=True)
 checksums=output/'installer-SHA256SUMS';checksums.write_text(receipt['iso']['sha256']+'  '+ISO+'\n'+(''.join(p['sha256']+'  '+p['file']+'\n' for p in parts) if len(parts)>1 else ''))
 for name in ['installer-build.json','xur-installer-x86_64.embedded.json']:shutil.copyfile(artifact/name,output/name)
 guide=output/'INSTALL.md';shutil.copyfile(ROOT/'docs/usage/install.md',guide)
 return files+[descriptor,signature,checksums,output/'installer-build.json',output/'xur-installer-x86_64.embedded.json',guide]
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('action',choices=['candidate']);p.add_argument('--commit',required=True);p.add_argument('--channel',required=True);a=p.parse_args();candidate(a.commit,a.channel)
