#!/usr/bin/env python3
"""Sign a verified CI bundle and publish it only inside an approved environment."""
import argparse,hashlib,importlib.util,json,os,pathlib,re,subprocess,tarfile,tempfile
ROOT=pathlib.Path(__file__).resolve().parents[1];REPO='DouglasCleghorn/Xur'
def run(*args):return subprocess.check_output(args,cwd=ROOT,text=True).strip()
def publish(channel,version,commit):
 if channel not in ('nightly','stable') or not re.fullmatch(r'[0-9]+(?:\.[0-9]+)+',version) or not re.fullmatch('[a-f0-9]{40}',commit):raise ValueError('Invalid publication parameters')
 branch='main' if channel=='nightly' else 'release'
 # Approvals can arrive out of order. A superseded commit must not move the channel backwards.
 if run('git','ls-remote','origin','refs/heads/'+branch).split()[0]!=commit:raise ValueError('This build is superseded; approve the latest branch build instead')
 artifact=ROOT/'.build/ci-artifact';receipt=json.loads((artifact/'build.json').read_text())
 archive=artifact/'xur-app-x86_64.tar.gz'
 if receipt['commit']!=commit or hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()!=receipt['sha256']:raise ValueError('CI artifact identity mismatch')
 bundle=ROOT/'.build/context/rootfs/usr/share/xur/app-bundle';bundle.mkdir(parents=True,exist_ok=True)
 with tarfile.open(archive) as tar:tar.extractall(bundle,filter='data')
 spec=importlib.util.spec_from_file_location('repository',ROOT/'eng/update-repository.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);module.publish(version,channel)
 public=module.PUBLIC;identity=(public/'latest').read_text().strip()
 source=public/'xur-source.tar.gz';run('git','archive','--format=tar.gz','--output='+str(source),commit)
 tag=('nightly-' if channel=='nightly' else 'v')+version
 files=[public/'latest',public/(identity+'.json'),public/(identity+'.json.sig'),public/(identity+'.tar.gz'),source,artifact/'build.json']
 run('gh','release','create',tag,'--repo',REPO,'--target',commit,'--draft','--title','Xur '+channel+' '+version,'--notes','Approved '+channel+' build from '+commit+'. Signed application bundle; no OS image. Automated checks passed; physical GPU validation is separate.',*map(str,files))
 run('gh','release','edit',tag,'--repo',REPO,'--draft=false','--prerelease='+str(channel=='nightly').lower(),'--latest='+str(channel=='stable').lower())
 if channel=='nightly':
  # The small channel pointer refers to an immutable per-commit release. In-flight
  # downloads keep using that release even after this pointer changes.
  try:run('gh','release','view','nightly','--repo',REPO)
  except subprocess.CalledProcessError:run('gh','release','create','nightly','--repo',REPO,'--target',commit,'--prerelease','--latest=false','--title','Nightly channel','--notes','Latest approved main-branch build. The pointer references a separate versioned release.')
  with tempfile.TemporaryDirectory() as temp:
   pointer=pathlib.Path(temp)/'latest';pointer.write_text(tag+'\n');run('gh','release','upload','nightly',str(pointer),'--repo',REPO,'--clobber')
 print(json.dumps({'published':tag,'channel':channel,'commit':commit}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--channel',required=True);p.add_argument('--version',required=True);p.add_argument('--commit',required=True);a=p.parse_args();publish(a.channel,a.version,a.commit)
