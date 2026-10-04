#!/usr/bin/env python3
"""Sign a verified CI bundle and publish it only inside an approved environment."""
import shutil,argparse,hashlib,importlib.util,json,os,pathlib,re,subprocess,tarfile,tempfile
ROOT=pathlib.Path(__file__).resolve().parents[1];REPO='DouglasCleghorn/Xur'
def run(*args):return subprocess.check_output(args,cwd=ROOT,text=True).strip()
def check_current(channel,version,commit):
 if channel not in ('nightly','stable') or not re.fullmatch(r'[0-9]+(?:\.[0-9]+)+',version) or not re.fullmatch('[a-f0-9]{40}',commit):raise ValueError('Invalid publication parameters')
 branch='main' if channel=='nightly' else 'release'
 # Approvals can arrive out of order. A superseded commit must not move the channel backwards.
 if run('git','ls-remote','origin','refs/heads/'+branch).split()[0]!=commit:raise ValueError('This build is superseded; approve the latest branch build instead')
def publish(channel,version,commit,with_installer=True):
 check_current(channel,version,commit)
 artifact=ROOT/'.build/ci-artifact';receipt=json.loads((artifact/'build.json').read_text())
 archive=artifact/'xur-app-x86_64.tar.gz'
 if receipt['commit']!=commit or receipt.get('channel')!=channel or receipt.get('checks')!='eng/test-fast.sh passed' or hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()!=receipt['sha256']:raise ValueError('CI artifact identity mismatch')
 bundle=ROOT/'.build/context/rootfs/usr/share/xur/app-bundle';bundle.mkdir(parents=True,exist_ok=True)
 with tarfile.open(archive) as tar:tar.extractall(bundle,filter='data')
 spec=importlib.util.spec_from_file_location('repository',ROOT/'eng/update-repository.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);module.publish(version,channel)
 public=module.PUBLIC;identity=(public/'latest').read_text().strip()
 tag=('nightly-' if channel=='nightly' else 'v')+version
 # Channel aliases contain only the current public release pointer.
 result=subprocess.run(['gh','api','repos/'+REPO+'/releases/tags/'+channel],capture_output=True,text=True)
 if result.returncode:
  if '404' not in result.stderr:raise RuntimeError(result.stderr)
  run('gh','release','create',channel,'--repo',REPO,'--target',commit,'--prerelease','--latest=false','--title',channel.title()+' update channel','--notes','Small update pointers. Download installer media from the versioned releases.')
  channel_assets=[]
 else:channel_assets=json.loads(result.stdout)['assets']
 installer_metadata=None
 if with_installer:
  installer_spec=importlib.util.spec_from_file_location('installer',ROOT/'eng/ci-installer.py');installer=importlib.util.module_from_spec(installer_spec);installer_spec.loader.exec_module(installer)
  installer.assets(ROOT/'.build/ci-installer',public/'installer',commit,channel,pathlib.Path(os.environ['XUR_UPDATE_SIGNING_KEY']),version=version)
  installer_metadata=json.loads((public/'installer/installer.json').read_text())
  if len(installer_metadata['parts'])!=1 or installer_metadata['parts'][0]['file']!=installer_metadata['iso']['file']:raise ValueError('Installer exceeds the single-ISO release limit; shrink it before publishing')
 entry=json.loads((public/(identity+'.json')).read_text())
 descriptor=module.compact(public,entry,pathlib.Path(os.environ['XUR_UPDATE_SIGNING_KEY']),installer_metadata,'xur-update-x86_64.tar.gz')
 named_descriptor=public/'xur-update.json';shutil.copyfile(descriptor,named_descriptor)
 named_archive=public/'xur-update-x86_64.tar.gz'
 shutil.copyfile(public/entry['file'],named_archive)
 files=[named_archive,named_descriptor]
 if with_installer:
  iso_name=installer_metadata['iso']['file']
  files.insert(0,public/'installer'/iso_name)
 notes=public/'release-notes.md'
 if with_installer:
  iso=installer_metadata['iso']
  notes.write_text(f"[**Download Xur installer ISO**](https://github.com/{REPO}/releases/download/{tag}/{iso_name}) · {iso['bytes']/1024**3:.2f} GiB\n\n"
  "[USB installation guide](https://xur.app/download/) · Bazzite downloads during installation.\n\n"
  +"The app archive and JSON descriptor are for the built-in updater; choose the ISO for installation.\n\n"
  +f"<details><summary>Verification and build details</summary>\n\nISO SHA-256: `{iso['sha256']}`\n\nThe JSON descriptor includes its signature, authenticated ISO size/hash and inspection receipt. See [verification instructions](https://github.com/{REPO}/blob/main/docs/development/installer-releases.md#download-and-verify).\n\nCommit: `{commit}`. Automated app checks and installer contents inspection passed. Boot/install and physical GPU tests were not run for this build.\n\n</details>\n")
 else:
  notes.write_text("Application update. Installer builds are manual; use the [most recent installer](https://xur.app/download/) for bootable media.\n\n"
   +f"Commit: `{commit}`. Automated app checks passed. The app archive and signed JSON descriptor are for the built-in updater.\n")
 run('gh','release','create',tag,'--repo',REPO,'--target',commit,'--draft','--title','Xur '+channel+' '+version,'--notes-file',str(notes),*map(str,files))
 # Stable's app release is GitHub Latest; updater clients use channel/current.
 run('gh','release','edit',tag,'--repo',REPO,'--draft=false','--prerelease='+str(channel=='nightly').lower(),'--latest='+str(channel=='stable').lower())
 with tempfile.TemporaryDirectory() as temp:
  temp=pathlib.Path(temp)
  pointer=temp/'current';pointer.write_text(tag+'\n');run('gh','release','upload',channel,str(pointer),'--repo',REPO,'--clobber')
 # Retire leftover bridge markers only after the new release is discoverable.
 for asset in channel_assets:
  if asset['name'] in ('migration','latest'):
   run('gh','release','delete-asset',channel,asset['name'],'--repo',REPO,'--yes')
 print(json.dumps({'published':tag,'channel':channel,'commit':commit}))

def publish_installer(channel,version,commit):
 """Publish inspected media separately, preserving the app release and pointers."""
 check_current(channel,version,commit)
 app_tag=('nightly-' if channel=='nightly' else 'v')+version
 published=json.loads(run('gh','api',f'repos/{REPO}/releases/tags/{app_tag}'))
 if published['draft'] or published['prerelease']!=(channel=='nightly') or published['target_commitish']!=commit:
  raise ValueError('Published application release identity mismatch')
 stage=ROOT/'.build/ci-app-release';stage.mkdir(parents=True,exist_ok=True)
 run('gh','release','download',app_tag,'--repo',REPO,'--pattern','xur-update.json','--dir',str(stage))
 spec=importlib.util.spec_from_file_location('verify_release',ROOT/'eng/verify-release.py');verify=importlib.util.module_from_spec(spec);spec.loader.exec_module(verify)
 trusted=ROOT/'os/bootc/application-update-key.pem'
 entry=verify.verify(stage/'xur-update.json',trusted)
 filename=entry['file']
 if entry['channel']!=channel or entry['version']!=version or pathlib.Path(filename).name!=filename or not filename.endswith('.tar.gz'):
  raise ValueError('Published application descriptor identity mismatch')
 run('gh','release','download',app_tag,'--repo',REPO,'--pattern',filename,'--dir',str(stage))
 verify.verify(stage/'xur-update.json',trusted,archive=stage/filename)
 spec=importlib.util.spec_from_file_location('repository',ROOT/'eng/update-repository.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
 key,_=module.keys(local=False)
 spec=importlib.util.spec_from_file_location('installer',ROOT/'eng/ci-installer.py');installer=importlib.util.module_from_spec(spec);spec.loader.exec_module(installer)
 output=module.PUBLIC/'installer'
 installer.assets(ROOT/'.build/ci-installer',output,commit,channel,key,version=version)
 metadata=json.loads((output/'installer.json').read_text())
 if len(metadata['parts'])!=1 or metadata['parts'][0]['file']!=metadata['iso']['file']:
  raise ValueError('Installer exceeds the single-ISO release limit; shrink it before publishing')
 descriptor=module.compact(module.PUBLIC,entry,key,metadata)
 signed=module.PUBLIC/'xur-update.json';shutil.copyfile(descriptor,signed)
 tag=app_tag+'-installer';iso=metadata['iso']
 notes=module.PUBLIC/'installer-release-notes.md'
 notes.write_text(f"[**Download Xur installer ISO**](https://github.com/{REPO}/releases/download/{tag}/{iso['file']}) · {iso['bytes']/1024**3:.2f} GiB\n\n"
  "[USB installation guide](https://xur.app/download/) · Bazzite downloads during installation.\n\n"
  +f"The application update was already published in [{app_tag}](https://github.com/{REPO}/releases/tag/{app_tag}). This release adds inspected installer media without changing the update channel.\n\n"
  +f"ISO SHA-256: `{iso['sha256']}`\n\nThe signed JSON descriptor authenticates the ISO and the unchanged application archive.\n\n"
  +f"Commit: `{commit}`. Automated app checks and installer contents inspection passed. Boot/install and physical GPU tests were not run for this build.\n")
 check_current(channel,version,commit)
 run('gh','release','create',tag,'--repo',REPO,'--target',commit,'--draft','--title','Xur '+channel+' '+version+' installer','--notes-file',str(notes),str(output/iso['file']),str(stage/filename),str(signed))
 run('gh','release','edit',tag,'--repo',REPO,'--draft=false','--prerelease='+str(channel=='nightly').lower(),'--latest=false')
 print(json.dumps({'published':tag,'channel':channel,'commit':commit,'applicationRelease':app_tag,'channelPointerChanged':False}))

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--channel',required=True);p.add_argument('--version',required=True);p.add_argument('--commit',required=True);mode=p.add_mutually_exclusive_group();mode.add_argument('--with-installer',action='store_true');mode.add_argument('--installer-release',action='store_true');a=p.parse_args()
 if a.installer_release:publish_installer(a.channel,a.version,a.commit)
 else:publish(a.channel,a.version,a.commit,a.with_installer)
