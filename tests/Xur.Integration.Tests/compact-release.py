#!/usr/bin/env python3
"""Verify the migration is published once, then legacy discovery stays frozen."""
import importlib.machinery,contextlib,hashlib,importlib.util,json,os,pathlib,shutil,subprocess,tarfile,tempfile
from unittest.mock import patch
repo=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('release',repo/'eng/ci-release.py');release=importlib.util.module_from_spec(spec);spec.loader.exec_module(release)
with tempfile.TemporaryDirectory() as directory:
 root=pathlib.Path(directory);release.ROOT=root
 (root/'eng').mkdir();(root/'os/bootc').mkdir(parents=True);(root/'docs/usage').mkdir(parents=True)
 for name in ['update-repository.py','ci-installer.py']:shutil.copyfile(repo/'eng'/name,root/'eng'/name)
 (root/'docs/usage/install.md').write_text('Installation guide')
 key=root/'key';subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True,capture_output=True)
 (root/'os/bootc/application-update-key.pem').write_bytes(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout']))
 artifact=root/'.build/ci-artifact';artifact.mkdir(parents=True);bundle=root/'bundle';bundle.mkdir()
 (bundle/'fixture').write_text('payload');files={'fixture':hashlib.sha256(b'payload').hexdigest()};identity=hashlib.sha256(json.dumps(files,sort_keys=True).encode()).hexdigest()
 (bundle/'bundle.json').write_text(json.dumps({'id':identity,'files':files,'hostAbi':1}))
 archive=artifact/'xur-app-x86_64.tar.gz'
 with tarfile.open(archive,'w:gz') as tar:tar.add(bundle,arcname='.')
 commit='a'*40;(artifact/'build.json').write_text(json.dumps({'commit':commit,'sha256':hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()}))
 installer=root/'.build/ci-installer';installer.mkdir();iso=installer/'xur-installer-x86_64.iso';iso.write_bytes(b'ISO fixture')
 report=installer/'xur-installer-x86_64.embedded.json';report.write_text(json.dumps({**dict.fromkeys(['safeKickstartTemplate','uefiAndBiosLayout','enforcementNotDisabled','fat32Compatible','onlineInstaller'],True),'verifiedFiles':{str(n):'hash' for n in range(101)}}))
 releases={};aliases={};calls=[]
 def run(*args):
  calls.append(args)
  if args[:2]==('git','ls-remote'):return commit+' refs/heads/main'
  assert args[:2]==('gh','release'),args
  action,tag=args[2:4]
  if action=='create':
   if tag in ('nightly','stable'):aliases[tag]={};return ''
   assert tag not in releases
   notes=pathlib.Path(args[args.index('--notes-file')+1]).read_text();assert '**Download Xur installer ISO**' in notes
   start=args.index('--notes-file')+2;releases[tag]={pathlib.Path(f).name:pathlib.Path(f).read_bytes() for f in args[start:]};return ''
  if action=='upload':
   f=pathlib.Path(args[4]);aliases[tag][f.name]=f.read_text();return ''
  assert action=='edit';return ''
 release.run=run;real_run=subprocess.run
 def api(args,**kwargs):
  if args[:2]==['gh','api']:
   channel=args[2].rsplit('/',1)[-1]
   if channel not in aliases:return subprocess.CompletedProcess(args,1,'','HTTP 404')
   return subprocess.CompletedProcess(args,0,json.dumps({'assets':[{'name':n} for n in aliases[channel]]}),'')
  return real_run(args,**kwargs)
 with patch.dict(os.environ,{'XUR_UPDATE_SIGNING_KEY':str(key)}),patch('subprocess.run',api):
  for channel,prefix in [('nightly','nightly-'),('stable','v')]:
   (installer/'installer-build.json').write_text(json.dumps({'schema':1,'commit':commit,'channel':channel,'iso':{'file':iso.name,'bytes':iso.stat().st_size,'sha256':hashlib.file_digest(iso.open('rb'),'sha256').hexdigest()},'inspectionSha256':hashlib.file_digest(report.open('rb'),'sha256').hexdigest()}))
   release.publish(channel,'1.0',commit)
   assert len(releases[prefix+'1.0'])==6
   frozen=dict(aliases[channel]);release.publish(channel,'1.1',commit)
   assert set(releases[prefix+'1.1'])=={'xur-installer-x86_64.iso','xur-update-x86_64.tar.gz','xur-update.json'}
   assert aliases[channel]['migration']==frozen['migration']==prefix+'1.0\n'
   assert aliases[channel]['current']==prefix+'1.1\n'
   if channel=='nightly':assert aliases[channel]['latest']==frozen['latest']==prefix+'1.0\n'
   edits=[c for c in calls if c[:3]==('gh','release','edit') and c[3].startswith(prefix)]
   assert '--latest=false' in edits[-1]
   if channel=='stable':assert '--latest=true' in edits[0]
 # Actual signed bridge descriptors remain readable by the unchanged legacy checker,
 # while the upgraded checker follows the same channel to the three-asset release.
 loader=importlib.machinery.SourceFileLoader('migration_updater',str(repo/'os/bootc/app-update'));spec=importlib.util.spec_from_loader(loader.name,loader);u=importlib.util.module_from_spec(spec);loader.exec_module(u)
 u.ROOT=root/'state';u.ROOT.mkdir();u.CONFIG=root/'settings';u.KEY=root/'os/bootc/application-update-key.pem';stage=root/'stage';stage.mkdir()
 for channel,prefix in [('nightly','nightly-'),('stable','v')]:
  u.select_channel(channel)
  def fetch(url,path,limit):
   if url==u.PUBLIC+'/latest':path.write_bytes(releases['v1.0']['latest']);return u.GITHUB+'/download/v1.0'
   tail=url.removeprefix(u.GITHUB+'/download/');tag,name=tail.split('/',1)
   data=aliases[tag][name].encode() if tag in aliases else releases[tag][name]
   assert len(data)<=limit;path.write_bytes(data)
  u.fetch=fetch
  assert u.check_legacy(stage)['version']=='1.0'
  upgraded=u.check(stage);assert upgraded['version']=='1.1'
  downloaded=u.download_bundle(stage,upgraded);assert hashlib.file_digest(downloaded.open('rb'),'sha256').hexdigest()==upgraded['sha256']
print(json.dumps({'suite':'CompactRelease','result':'Passed','futureAssets':3,'transitionAssets':6,'legacyPointersFrozen':True,'stableAndNightly':True}))
