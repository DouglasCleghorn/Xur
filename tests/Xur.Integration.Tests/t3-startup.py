"""Exercise the actual launcher with gated upstream installers; no network/accounts."""
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

LAUNCHER = Path(sys.argv.pop(1)).resolve() if len(sys.argv) > 1 else Path('src/Xur.Domain/t3-launcher.sh').resolve()
EVIDENCE = Path('.build/evidence/t3-startup').resolve()
EVIDENCE.mkdir(parents=True, exist_ok=True)
FAKE = r'''#!/usr/bin/env python3
import gzip, hashlib, io, json, os, pathlib, platform, shutil, sys, tarfile, time
root=pathlib.Path(os.environ['FIXTURE'])
home=pathlib.Path.home()
def install(tool, version='initial'):
    target=home/'.opencode/bin/opencode' if tool=='opencode' else home/'.local/bin'/tool
    if tool=='t3':
        actual=home/'.t3/runtime/versions'/version/'t3'
        actual.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(root/'fake.py',actual);actual.chmod(0o700)
        target.parent.mkdir(parents=True,exist_ok=True);target.unlink(missing_ok=True);target.symlink_to(actual)
    elif tool=='gh':
        target.parent.mkdir(parents=True,exist_ok=True)
        target.write_text('#!/bin/sh\necho "gh version 0.0.1 (fixture)"\n');target.chmod(0o700)
    else:
        target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(root/'fake.py',target);target.chmod(0o700)
    if tool=='opencode':
        alias=home/'.local/bin/opencode';alias.parent.mkdir(parents=True,exist_ok=True)
        alias.unlink(missing_ok=True);alias.symlink_to(target)
def gate(tool):
    (root/(tool+'.started')).touch()
    while not (root/(tool+'.release')).exists(): time.sleep(.01)
    if (root/'fail').exists(): sys.exit(22)
tool=pathlib.Path(sys.argv[0]).name
args=sys.argv[1:]
if tool=='fake.py':
    install(args[1],args[2] if len(args)>2 else 'initial');sys.exit(0)
if tool=='curl':
    url=next(a for a in args if a.startswith('https://'))
    tool='tools' if 'micro.mamba.pm' in url or '/mamba-org/' in url else 'uv' if 'astral.sh' in url or '/astral-sh/' in url else 'gh' if 'github.com' in url else 't3' if 't3.codes' in url else 'codex' if 'chatgpt.com' in url else 'claude' if 'claude.ai' in url else 'opencode'
    gate(tool)
    output=pathlib.Path(args[args.index('-o')+1])
    if 'raw.githubusercontent.com' in url:
        output.write_text('Fixture upstream license')
    elif tool=='gh':
        # Synthetic release versions, never actual dependency pins.
        version=(root/'gh-version').read_text().strip() if (root/'gh-version').exists() else '0.0.2'
        arch={'x86_64':'amd64','aarch64':'arm64'}[platform.machine()]
        prefix='gh_'+version+'_linux_'+arch
        artifact=prefix+'.tar.gz';sums='gh_'+version+'_checksums.txt'
        data=io.BytesIO()
        with tarfile.open(fileobj=data,mode='w') as archive:
            for name,content in [('bin/gh',('#!/bin/sh\necho "gh version '+version+' (fixture)"\n').encode()),('LICENSE',b'Fixture upstream license')]:
                member=tarfile.TarInfo(prefix+'/'+name);member.size=len(content)
                if name=='bin/gh' and (root/'gh-unsafe').exists():
                    member.type=tarfile.SYMTYPE;member.linkname='/tmp/untrusted';member.size=0
                archive.addfile(member,io.BytesIO(content))
        payload=gzip.compress(data.getvalue(),mtime=0)
        if url.endswith('/latest'):
            output.write_text(json.dumps(dict(tag_name='v'+version,prerelease=False,draft=False,assets=[dict(name=artifact),dict(name=sums)])))
        elif url.endswith('_checksums.txt'):
            digest='0'*64 if (root/'gh-corrupt').exists() else hashlib.sha256(payload).hexdigest()
            output.write_text(digest+'  '+artifact+'\n')
        else:
            (root/'gh-downloaded').touch();output.write_bytes(payload)
    else:
        output.write_text('python3 "$FIXTURE/fake.py" install '+('micromamba' if tool=='tools' else tool)+'\n')
elif tool=='micromamba':
    gate('tools')
    if args[0] in ('create','install'):
        prefix=pathlib.Path(args[args.index('--prefix')+1]);(prefix/'bin').mkdir(parents=True,exist_ok=True)
        (prefix/'conda-meta').mkdir(exist_ok=True);(prefix/'conda-meta/history').touch()
        for command in ('rg','fd','jq','git-lfs','rsync','zip','unzip','sqlite3'):
            shutil.copyfile(root/'fake.py',prefix/'bin'/command);(prefix/'bin'/command).chmod(0o700)
elif tool=='git-lfs' and args==['install','--skip-repo']:
    (root/'lfs-configured').touch()
elif args==['self','update']:
    gate(tool)
elif args==['--version']:
    print(tool+' fixture')
elif args and args[0] in ('update','upgrade'):
    gate(tool)
    if tool=='t3': install(tool,'updated')
elif args and args[0]=='start':
    (root/'launched').write_text(str(pathlib.Path(sys.argv[0]).resolve()))
    while True: time.sleep(.05)
elif args[:2]==['auth','pairing']:
    print('fixture pairing')
else: sys.exit(3)
'''


class StartupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=EVIDENCE)
        self.root = Path(self.temp.name)
        self.home = self.root/'home'
        self.home.mkdir()
        (self.home/'projects').mkdir()
        (self.home/'projects/keep').write_text('saved project')
        (self.root/'bin').mkdir()
        (self.root/'fake.py').write_text(FAKE)
        shutil.copyfile(self.root/'fake.py',self.root/'bin/curl')
        (self.root/'bin/curl').chmod(0o700)
        self.env = dict(os.environ, HOME=str(self.home), FIXTURE=str(self.root),
                        T3CODE_HOME=str(self.home/'.t3'), T3CODE_INSTALL_BIN_DIR=str(self.home/'.local/bin'),
                        PATH=str(self.root/'bin')+os.pathsep+os.environ['PATH'])
        self.processes = []
        self.log = (self.root/'log').open('w')

    def tearDown(self):
        for p in self.processes:
            try: os.killpg(p.pid,signal.SIGKILL)
            except ProcessLookupError: pass
            p.wait(timeout=5)
        self.log.close()
        self.temp.cleanup()

    def install(self, tool):
        subprocess.run([sys.executable,str(self.root/'fake.py'),'install',tool],env=self.env,check=True)

    def release(self, *tools):
        for tool in tools: (self.root/(tool+'.release')).touch()

    def wait(self, condition):
        until=time.monotonic()+8
        while not condition() and time.monotonic()<until: time.sleep(.01)
        self.assertTrue(condition(), (self.root/'log').read_text())

    def start(self):
        p=subprocess.Popen(['sh',str(LAUNCHER),'start'],env=self.env,stdout=self.log,stderr=self.log,start_new_session=True)
        self.processes.append(p)
        return p

    def test_installed_server_starts_while_all_seven_updates_are_blocked(self):
        for tool in ('t3','claude','opencode'): self.install(tool)
        self.start()
        self.wait(lambda:(self.root/'launched').exists())
        self.wait(lambda:all((self.root/(t+'.started')).exists() for t in ('t3','codex','claude','opencode','gh','uv','tools')))
        self.assertIn('/initial/t3',(self.root/'launched').read_text())
        self.assertFalse((self.home/'.local/bin/codex').exists())
        self.release('t3','codex','claude','opencode','gh','uv','tools')
        self.wait(lambda:(self.home/'.local/bin/t3').resolve().parent.name=='updated')
        self.wait(lambda:(self.home/'.local/bin/codex').exists())
        self.wait(lambda:(self.home/'.local/bin/gh').exists())
        self.wait(lambda:all((self.home/'.local/share/xur-tools/bin'/t).exists() for t in ('rg','fd','jq','git-lfs','rsync','zip','unzip','sqlite3')))
        self.wait(lambda:(self.home/'.local/bin/uv').exists())
        self.assertEqual((self.home/'projects/keep').read_text(),'saved project')
        # The next container start uses the runtime selected by the background updater.
        replacement=self.root/'replacement';shutil.copyfile(LAUNCHER,replacement)
        (self.root/'launched').unlink()
        p=subprocess.Popen(['sh',str(replacement),'start'],env=self.env,stdout=self.log,stderr=self.log,start_new_session=True)
        self.processes.append(p)
        self.wait(lambda:(self.root/'launched').exists())
        self.assertIn('/updated/t3',(self.root/'launched').read_text())

    def test_first_install_waits_only_for_t3(self):
        self.start()
        self.wait(lambda:all((self.root/(t+'.started')).exists() for t in ('t3','codex','claude','opencode','gh','uv','tools')))
        self.assertFalse((self.root/'launched').exists())
        self.release('t3')
        self.wait(lambda:(self.root/'launched').exists())
        self.assertFalse((self.home/'.local/bin/codex').exists())
        self.release('codex','claude','opencode','gh','uv','tools')
        self.wait(lambda:all((self.home/'.local/bin'/t).exists() for t in ('codex','claude','opencode','gh')))

    def test_failed_updates_do_not_stop_installed_server(self):
        for tool in ('t3','codex','claude','opencode','gh','uv'): self.install(tool)
        self.install('micromamba')
        prefix=self.home/'.local/share/xur-tools/bin';prefix.mkdir(parents=True)
        shutil.copyfile(self.root/'fake.py',prefix/'rg');(prefix/'rg').chmod(0o700)
        (self.root/'fail').touch();self.release('t3','codex','claude','opencode','gh','uv','tools')
        p=self.start()
        self.wait(lambda:all(t+' update failed' in (self.root/'log').read_text() for t in ('t3','codex','claude','opencode','gh','uv','tools')))
        self.assertIsNone(p.poll())
        self.assertIn('/initial/t3',(self.root/'launched').read_text())

    def test_failed_first_t3_install_is_retryable(self):
        (self.root/'fail').touch();self.release('t3','codex','claude','opencode','gh','uv','tools')
        p=self.start();self.assertNotEqual(p.wait(timeout=8),0)
        self.assertFalse((self.root/'launched').exists())
        (self.root/'fail').unlink()
        self.start();self.wait(lambda:(self.root/'launched').exists())

    def test_pairing_does_not_update(self):
        self.install('t3')
        result=subprocess.run(['sh',str(LAUNCHER),'auth','pairing','create'],env=self.env,capture_output=True,text=True,timeout=5)
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertIn('fixture pairing',result.stdout)
        self.assertEqual(list(self.root.glob('*.started')),[])

    def update_gh(self):
        self.release('gh')
        return subprocess.run(['sh',str(LAUNCHER),'xur-install-harness','gh'],env=self.env,capture_output=True,text=True,timeout=8)

    def test_gh_updates_latest_and_keeps_credentials_and_license(self):
        config=self.home/'.config/gh/hosts.yml';config.parent.mkdir(parents=True)
        config.write_text('fixture saved credentials')
        first=self.update_gh();self.assertEqual(first.returncode,0,first.stderr)
        self.assertIn('gh version 0.0.2',first.stdout)
        self.assertEqual((self.home/'.local/share/gh/LICENSE').read_text(),'Fixture upstream license')
        (self.root/'gh-downloaded').unlink()
        second=self.update_gh();self.assertEqual(second.returncode,0,second.stderr)
        self.assertFalse((self.root/'gh-downloaded').exists(),'Current binaries need no archive download')
        (self.root/'gh-version').write_text('0.0.3')
        updated=self.update_gh();self.assertEqual(updated.returncode,0,updated.stderr)
        self.assertIn('gh version 0.0.3',updated.stdout)
        self.assertEqual(config.read_text(),'fixture saved credentials')

    def test_gh_rejects_bad_checksum_and_archive_links_without_replacing_saved_binary(self):
        for failure in ('gh-corrupt','gh-unsafe'):
            with self.subTest(failure=failure):
                self.install('gh');old=(self.home/'.local/bin/gh').read_bytes()
                (self.root/failure).touch()
                result=self.update_gh()
                self.assertIn('gh update failed',result.stderr)
                self.assertEqual((self.home/'.local/bin/gh').read_bytes(),old)
                self.assertIn('gh version 0.0.1',result.stdout)
                (self.root/failure).unlink()


if __name__=='__main__': unittest.main()
