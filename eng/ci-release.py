#!/usr/bin/env python3
"""Sign a verified CI bundle and publish it only inside an approved environment."""
import shutil,argparse,hashlib,importlib.util,json,os,pathlib,re,subprocess,tarfile,tempfile
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
 tag=('nightly-' if channel=='nightly' else 'v')+version
 # One migration release per legacy channel. Never advance its old discovery path.
 result=subprocess.run(['gh','api','repos/'+REPO+'/releases/tags/'+channel],capture_output=True,text=True)
 if result.returncode:
  if '404' not in result.stderr:raise RuntimeError(result.stderr)
  run('gh','release','create',channel,'--repo',REPO,'--target',commit,'--prerelease','--latest=false','--title',channel.title()+' update channel','--notes','Small update pointers. Download installer media from the versioned releases.')
  channel_assets=[]
 else:channel_assets=json.loads(result.stdout)['assets']
 transition=not any(a['name']=='migration' for a in channel_assets)
 installer_spec=importlib.util.spec_from_file_location('installer',ROOT/'eng/ci-installer.py');installer=importlib.util.module_from_spec(installer_spec);installer_spec.loader.exec_module(installer)
 installer.assets(ROOT/'.build/ci-installer',public/'installer',commit,channel,pathlib.Path(os.environ['XUR_UPDATE_SIGNING_KEY']),version=version)
 installer_metadata=json.loads((public/'installer/installer.json').read_text())
 if len(installer_metadata['parts'])!=1 or installer_metadata['parts'][0]['file']!=installer_metadata['iso']['file']:raise ValueError('Installer exceeds the single-ISO release limit; shrink it before publishing')
 entry=json.loads((public/(identity+'.json')).read_text())
 descriptor=module.compact(public,entry,pathlib.Path(os.environ['XUR_UPDATE_SIGNING_KEY']),installer_metadata,entry['file'] if transition else 'xur-update-x86_64.tar.gz')
 named_descriptor=public/'xur-update.json';shutil.copyfile(descriptor,named_descriptor)
 named_archive=public/entry['file'] if transition else public/'xur-update-x86_64.tar.gz'
 if not transition:shutil.copyfile(public/entry['file'],named_archive)
 iso_name=installer_metadata['iso']['file']
 files=[public/'installer'/iso_name,named_archive,named_descriptor]
 if transition:files += [public/'latest',public/(identity+'.json'),public/(identity+'.json.sig')]
 iso=installer_metadata['iso']
 notes=public/'release-notes.md'
 notes.write_text(f"[**Download Xur installer ISO**](https://github.com/{REPO}/releases/download/{tag}/{iso_name}) · {iso['bytes']/1024**3:.2f} GiB\n\n"
  "[USB installation guide](https://xur.app/download/) · Bazzite downloads during installation.\n\n"
  +("This is the one-time updater transition for this channel. The extra legacy files allow older installations to upgrade automatically. Future releases contain only the ISO, app archive and signed JSON descriptor.\n\n" if transition else "The app archive and JSON descriptor are for the built-in updater; choose the ISO for installation.\n\n")
  +f"<details><summary>Verification and build details</summary>\n\nISO SHA-256: `{iso['sha256']}`\n\nThe JSON descriptor includes its signature, authenticated ISO size/hash and inspection receipt. See [verification instructions](https://github.com/{REPO}/blob/main/docs/development/installer-releases.md#download-and-verify).\n\nCommit: `{commit}`. Automated app checks and installer contents inspection passed. Boot/install and physical GPU tests were not run for this build.\n\n</details>\n")
 run('gh','release','create',tag,'--repo',REPO,'--target',commit,'--draft','--title','Xur '+channel+' '+version,'--notes-file',str(notes),*map(str,files))
 # GitHub Latest remains the Stable migration release for unmodified old clients.
 # Modern clients use stable/current and nightly/current instead.
 run('gh','release','edit',tag,'--repo',REPO,'--draft=false','--prerelease='+str(channel=='nightly').lower(),'--latest='+str(channel=='stable' and transition).lower())
 with tempfile.TemporaryDirectory() as temp:
  temp=pathlib.Path(temp)
  if transition:
   if channel=='nightly':
    legacy=temp/'latest';legacy.write_text(tag+'\n');run('gh','release','upload',channel,str(legacy),'--repo',REPO,'--clobber')
   migration=temp/'migration';migration.write_text(tag+'\n');run('gh','release','upload',channel,str(migration),'--repo',REPO,'--clobber')
  pointer=temp/'current';pointer.write_text(tag+'\n');run('gh','release','upload',channel,str(pointer),'--repo',REPO,'--clobber')
 print(json.dumps({'published':tag,'channel':channel,'commit':commit}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--channel',required=True);p.add_argument('--version',required=True);p.add_argument('--commit',required=True);a=p.parse_args();publish(a.channel,a.version,a.commit)
