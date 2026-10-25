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
import os, pathlib, shutil, sys, time
root=pathlib.Path(os.environ['FIXTURE'])
home=pathlib.Path.home()
def install(tool, version='initial'):
    target=home/'.opencode/bin/opencode' if tool=='opencode' else home/'.local/bin'/tool
    if tool=='t3':
        actual=home/'.t3/runtime/versions'/version/'t3'
        actual.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(root/'fake.py',actual);actual.chmod(0o700)
        target.parent.mkdir(parents=True,exist_ok=True);target.unlink(missing_ok=True);target.symlink_to(actual)
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
    tool='t3' if 't3.codes' in url else 'codex' if 'chatgpt.com' in url else 'claude' if 'claude.ai' in url else 'opencode'
    gate(tool)
    output=pathlib.Path(args[args.index('-o')+1])
    output.write_text('python3 "$FIXTURE/fake.py" install '+tool+'\n')
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

    def test_installed_server_starts_while_all_four_updates_are_blocked(self):
        for tool in ('t3','claude','opencode'): self.install(tool)
        self.start()
        self.wait(lambda:(self.root/'launched').exists())
        self.wait(lambda:all((self.root/(t+'.started')).exists() for t in ('t3','codex','claude','opencode')))
        self.assertIn('/initial/t3',(self.root/'launched').read_text())
        self.assertFalse((self.home/'.local/bin/codex').exists())
        self.release('t3','codex','claude','opencode')
        self.wait(lambda:(self.home/'.local/bin/t3').resolve().parent.name=='updated')
        self.wait(lambda:(self.home/'.local/bin/codex').exists())
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
        self.wait(lambda:all((self.root/(t+'.started')).exists() for t in ('t3','codex','claude','opencode')))
        self.assertFalse((self.root/'launched').exists())
        self.release('t3')
        self.wait(lambda:(self.root/'launched').exists())
        self.assertFalse((self.home/'.local/bin/codex').exists())
        self.release('codex','claude','opencode')
        self.wait(lambda:all((self.home/'.local/bin'/t).exists() for t in ('codex','claude','opencode')))

    def test_failed_updates_do_not_stop_installed_server(self):
        for tool in ('t3','codex','claude','opencode'): self.install(tool)
        (self.root/'fail').touch();self.release('t3','codex','claude','opencode')
        p=self.start()
        self.wait(lambda:all(t+' update failed' in (self.root/'log').read_text() for t in ('t3','codex','claude','opencode')))
        self.assertIsNone(p.poll())
        self.assertIn('/initial/t3',(self.root/'launched').read_text())

    def test_failed_first_t3_install_is_retryable(self):
        (self.root/'fail').touch();self.release('t3','codex','claude','opencode')
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


if __name__=='__main__': unittest.main()
