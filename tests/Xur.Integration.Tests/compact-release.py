#!/usr/bin/env python3
"""Verify compact releases and channel pointers without legacy bridge assets."""
import importlib.machinery,contextlib,hashlib,importlib.util,json,os,pathlib,shutil,subprocess,tarfile,tempfile
from unittest.mock import patch
repo=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('release',repo/'eng/ci-release.py');release=importlib.util.module_from_spec(spec);spec.loader.exec_module(release)
(repo/'.build/evidence').mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=repo/'.build/evidence') as directory:
 root=pathlib.Path(directory);release.ROOT=root
 (root/'eng').mkdir();(root/'os/bootc').mkdir(parents=True);(root/'docs/usage').mkdir(parents=True)
 for name in ['update-repository.py','ci-installer.py','verify-release.py']:shutil.copyfile(repo/'eng'/name,root/'eng'/name)
 (root/'docs/usage/install.md').write_text('Installation guide')
 key=root/'key';subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True,capture_output=True)
 (root/'os/bootc/application-update-key.pem').write_bytes(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout']))
 artifact=root/'.build/ci-artifact';artifact.mkdir(parents=True);bundle=root/'bundle';bundle.mkdir()
 (bundle/'fixture').write_text('payload')
 for name in ['control/Xur.Control','agent/Xur.Agent','gateway/Xur.Gateway','host/app-update']:
  path=bundle/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text('fixture executable');path.chmod(0o700)
 files={str(p.relative_to(bundle)):hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in bundle.rglob('*') if p.is_file()};identity=hashlib.sha256(json.dumps(files,sort_keys=True).encode()).hexdigest()
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
  if args[:2]==('gh','api'):
   tag=args[2].rsplit('/',1)[-1]
   return json.dumps({'draft':False,'prerelease':tag.startswith('nightly-'),'target_commitish':commit})
  assert args[:2]==('gh','release'),args
  action,tag=args[2:4]
  if action=='create':
   if tag in ('nightly','stable'):aliases[tag]={};return ''
   assert tag not in releases
   notes=pathlib.Path(args[args.index('--notes-file')+1]).read_text();assert '**Download Xur installer ISO**' in notes or 'Installer builds are manual' in notes
   start=args.index('--notes-file')+2;releases[tag]={pathlib.Path(f).name:pathlib.Path(f).read_bytes() for f in args[start:]};return ''
  if action=='download':
   name=args[args.index('--pattern')+1];destination=pathlib.Path(args[args.index('--dir')+1]);destination.mkdir(parents=True,exist_ok=True)
   (destination/name).write_bytes(releases[tag][name]);return ''
  if action=='delete-asset':
   del aliases[tag][args[4]];return ''
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
   (artifact/'build.json').write_text(json.dumps({'commit':commit,'channel':channel,'checks':'eng/test-fast.sh passed','sha256':hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()}))
   (installer/'installer-build.json').write_text(json.dumps({'schema':1,'commit':commit,'channel':channel,'iso':{'file':iso.name,'bytes':iso.stat().st_size,'sha256':hashlib.file_digest(iso.open('rb'),'sha256').hexdigest()},'inspectionSha256':hashlib.file_digest(report.open('rb'),'sha256').hexdigest()}))
   # A brand-new channel never creates a bridge or legacy discovery assets.
   release.publish(channel,'1.0',commit,with_installer=False)
   expected={'xur-update-x86_64.tar.gz','xur-update.json'}
   assert set(releases[prefix+'1.0'])==expected
   assert aliases[channel]=={'current':prefix+'1.0\n'}
   # An existing channel may still have retired markers; delete them after
   # publishing the new pointer without changing the previous release bytes.
   aliases[channel].update(migration='retired-ext4\n',latest='retired-ext4\n')
   original_release=dict(releases[prefix+'1.0'])
   release.publish(channel,'1.1',commit)
   assert set(releases[prefix+'1.1'])=={f'xur-{channel}-1.1-x86_64.iso','xur-update-x86_64.tar.gz','xur-update.json'}
   metadata=json.loads(releases[prefix+'1.1']['xur-update.json'])['release']['installer']
   assert metadata['iso']['file']==f'xur-{channel}-1.1-x86_64.iso'
   assert metadata['parts'][0]['file']==metadata['iso']['file']
   assert aliases[channel]=={'current':prefix+'1.1\n'}
   assert releases[prefix+'1.0']==original_release
   published=next(i for i,c in enumerate(calls) if c[:4]==('gh','release','create',prefix+'1.1'))
   upload=next(i for i,c in enumerate(calls) if i>published and c[:4]==('gh','release','upload',channel) and c[4].endswith('/current'))
   retired=[i for i,c in enumerate(calls) if c[:4]==('gh','release','delete-asset',channel)]
   assert len(retired)==2 and all(i>upload for i in retired)
   edits=[c for c in calls if c[:3]==('gh','release','edit') and c[3].startswith(prefix)]
   assert all('--latest='+str(channel=='stable').lower() in c for c in edits)
   release.publish(channel,'1.2',commit,with_installer=False)
   assert set(releases[prefix+'1.2'])=={'xur-update-x86_64.tar.gz','xur-update.json'}
   assert 'installer' not in json.loads(releases[prefix+'1.2']['xur-update.json'])['release']
   assert aliases[channel]['current']==prefix+'1.2\n'
   # Detached installer publication preserves the already-live update bytes,
   # signature, sequence and discovery pointers for both channels.
   live_release=dict(releases[prefix+'1.2']);live_pointers=dict(aliases[channel])
   shutil.rmtree(root/'.build/ci-app-release',ignore_errors=True)
   release.publish_installer(channel,'1.2',commit)
   media=releases[prefix+'1.2-installer']
   assert set(media)=={f'xur-{channel}-1.2-x86_64.iso','xur-update-x86_64.tar.gz','xur-update.json'}
   assert releases[prefix+'1.2']==live_release and aliases[channel]==live_pointers
   assert media['xur-update-x86_64.tar.gz']==live_release['xur-update-x86_64.tar.gz']
   original=json.loads(live_release['xur-update.json'])['release'];detached=json.loads(media['xur-update.json'])['release']
   assert {k:v for k,v in detached.items() if k!='installer'}==original
   descriptor=root/'detached.json';descriptor.write_bytes(media['xur-update.json'])
   media_iso=root/'detached.iso';media_iso.write_bytes(media[f'xur-{channel}-1.2-x86_64.iso'])
   verify_spec=importlib.util.spec_from_file_location('verify',root/'eng/verify-release.py');verify=importlib.util.module_from_spec(verify_spec);verify_spec.loader.exec_module(verify)
   verify.verify(descriptor,root/'os/bootc/application-update-key.pem',iso=media_iso)
   assert '--latest=false' in calls[-1]
   # The detached release cannot upload media if the app archive changed.
   shutil.rmtree(root/'.build/ci-app-release')
   archive_bytes=releases[prefix+'1.2']['xur-update-x86_64.tar.gz']
   releases[prefix+'1.2']['xur-update-x86_64.tar.gz']=archive_bytes+b'tampered'
   before=len([c for c in calls if c[:3]==('gh','release','create')])
   try:release.publish_installer(channel,'1.2',commit)
   except ValueError:pass
   else:raise AssertionError('Tampered published app archive accepted')
   assert before==len([c for c in calls if c[:3]==('gh','release','create')])
   releases[prefix+'1.2']['xur-update-x86_64.tar.gz']=archive_bytes
   # Failed discovery upload must leave the old pointer and bridge markers intact.
   aliases[channel]['migration']='retired-ext4\n';before=dict(aliases[channel])
   def fail_pointer(*args):
    if args[:4]==('gh','release','upload',channel):raise subprocess.CalledProcessError(1,args)
    return run(*args)
   with patch.object(release,'run',side_effect=fail_pointer):
    try:release.publish(channel,'1.3',commit,with_installer=False)
    except subprocess.CalledProcessError:pass
    else:raise AssertionError('Failed pointer upload accepted')
   assert aliases[channel]==before
 # The C# updater consumes the actual CI publication bytes without host activation.
 for tag,assets in releases.items():
  for name,data in assets.items():
   path=root/'repository'/tag/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(data)
 for channel,assets in aliases.items():
  for name,data in assets.items():
   path=root/'repository'/channel/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(data)
 sdk=os.environ.get('XUR_DOTNET') or shutil.which('dotnet') or str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet')
 subprocess.run([sdk,'run','--project',str(repo/'tests/Xur.Util.Tests'),'-c','Release','--','--release-fixture',str(root)],cwd=repo,check=True)
print(json.dumps({'suite':'CompactRelease','result':'Passed','installerReleaseAssets':3,'appOnlyAssets':2,'firstReleaseCompact':True,'retiredMarkersRemoved':True,'stableAndNightly':True,'detachedInstallerPreservesLiveUpdate':True}))
