#!/usr/bin/env python3
"""Upload a tested local package as an atomic, public GitHub Release (explicit opt-in)."""
import argparse,hashlib,json,pathlib,re,subprocess
ROOT=pathlib.Path(__file__).resolve().parents[1]
REPO='DouglasCleghorn/Xur'
def assets(version,iso=None):
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+)+',version):raise ValueError('Use a numeric dotted version')
    public=ROOT/'.build/update-repository';identity=(public/'latest').read_text().strip()
    if not re.fullmatch('[a-f0-9]{64}',identity):raise ValueError('Invalid packaged bundle identity')
    descriptor=public/(identity+'.json');entry=json.loads(descriptor.read_text())
    if entry['version']!=version or entry['file']!=identity+'.tar.gz':raise ValueError('Package this version first with eng/package-update.py')
    subprocess.run(['openssl','pkeyutl','-verify','-pubin','-inkey',str(ROOT/'os/bootc/application-update-key.pem'),'-rawin','-in',str(descriptor),'-sigfile',str(descriptor)+'.sig'],check=True,capture_output=True)
    archive=public/entry['file']
    if archive.stat().st_size!=entry['bytes'] or hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()!=entry['sha256']:raise ValueError('Packaged archive changed')
    receipt=json.loads((ROOT/'dist/updates'/version/'manifest.json').read_text())
    from source_files import source_files
    actual={str(p.relative_to(ROOT)):hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in source_files(ROOT)}
    if receipt['sourceFiles']!=actual:raise ValueError('Source changed since packaging; test and package again')
    source=ROOT/'dist/updates'/version/'xur-source.tar.gz'
    if hashlib.file_digest(source.open('rb'),'sha256').hexdigest()!=receipt['sourceSha256']:raise ValueError('Packaged source archive changed')
    files=[public/'latest',descriptor,pathlib.Path(str(descriptor)+'.sig'),archive,source]
    if iso:files.extend([iso,iso.with_suffix('.iso.sha256')])
    if any(not p.is_file() or p.stat().st_size>=2*1024**3 for p in files):raise ValueError('Every release asset must exist and be smaller than 2 GiB')
    return files
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--version',required=True);parser.add_argument('--iso',type=pathlib.Path);parser.add_argument('--publish',action='store_true',help='Upload and publish; default only lists verified assets')
    args=parser.parse_args();files=assets(args.version,args.iso)
    print(json.dumps({'tag':'v'+args.version,'assets':[str(p) for p in files],'publish':args.publish}))
    if args.publish:
        def git(*args):return subprocess.check_output(['git',*args],cwd=ROOT,text=True).strip()
        if git('status','--porcelain'):raise SystemExit('Commit and push the tested source before publishing')
        head=git('rev-parse','HEAD')
        remote=git('ls-remote','origin','refs/heads/main').split()
        if not remote or remote[0]!=head:raise SystemExit('Push this commit to origin/main first')
        details=json.loads(subprocess.check_output(['gh','repo','view',REPO,'--json','visibility'],text=True))
        if details['visibility']!='PUBLIC':raise SystemExit('The public updater requires a public repository')
        tag='v'+args.version
        # A draft keeps /releases/latest unchanged until every asset is uploaded.
        # Failure leaves the draft for inspection; no overwrite of existing releases.
        subprocess.run(['gh','release','create',tag,'--repo',REPO,'--target',head,'--draft','--title','Xur '+args.version,'--notes','Signed Xur application update. See the included source archive and repository documentation.',*[str(p) for p in files]],check=True)
        subprocess.run(['gh','release','edit',tag,'--repo',REPO,'--draft=false','--latest'],check=True)
