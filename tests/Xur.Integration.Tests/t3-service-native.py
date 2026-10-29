"""Opt-in IPC contract check against an installed upstream prebuilt t3 executable.

Usage: python3 -B tests/Xur.Integration.Tests/t3-service-native.py /path/to/t3
Only synthetic runtimes/data are used; no provider accounts or network needed.
"""
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

T3 = Path(sys.argv.pop(1)).resolve()
LAUNCHER = Path('src/Xur.Domain/t3-launcher.sh').resolve()
EVIDENCE = Path('.build/evidence/t3-service-native').resolve()
EVIDENCE.mkdir(parents=True, exist_ok=True)

# The release versions below are synthetic test fixtures, not dependency pins.
CHILD = r'''
const fs = require('node:fs');
const path = require('node:path');
const root = process.env.FIXTURE;
const context = JSON.parse(process.env.T3_SERVICE_LAUNCHER_CONTEXT);
fs.writeFileSync(path.join(root, 'child.pid'), String(process.pid));
fs.writeFileSync(path.join(root, 'context.json'), JSON.stringify(context));
process.on('message', message => {
  fs.writeFileSync(path.join(root, message.type), JSON.stringify(message));
});
if (context.update?.status === 'pending') {
  // Model a migration in the trial. Upstream must restore it if the trial dies.
  fs.writeFileSync(path.join(root, 'database'), 'trial migration');
  if (fs.existsSync(path.join(root, 'fail-trial'))) process.exit(1);
  process.send({type: 'prepared', updateId: context.update.id});
} else if (!context.update) {
  process.send({type: 'request-update', targetVersion: '0.0.2', dbPath: path.join(root, 'database')});
}
setInterval(() => {}, 1000);
'''


class NativeServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=EVIDENCE)
        self.root = Path(self.temp.name)
        self.home = self.root/'home'
        self.base = self.home/'.t3'
        self.state = self.base/'runtime/service-state.json'
        self.node = shutil.which('node')
        self.assertIsNotNone(self.node)
        (self.root/'child.cjs').write_text(CHILD)
        (self.root/'database').write_text('original database')
        # Xur chooses a runtime; its hidden command delegates to the real upstream
        # supervisor, which then spawns these fixtures with real inherited IPC.
        for version in ('0.0.1', '0.0.2'):
            directory = self.base/'runtime/versions'/version
            directory.mkdir(parents=True)
            (directory/'.install-complete').write_text(version)
            (directory/'t3').write_text('#!/bin/sh\nif [ "$1" = __service-launcher ]; then\n'
                ' exec "$NATIVE_T3" __service-launcher\nfi\nexec "$NATIVE_NODE" "$FIXTURE/child.cjs"\n')
            (directory/'t3').chmod(0o755)
        self.state.write_text(json.dumps(dict(protocol=3, activeVersion='0.0.1')))
        (self.root/'bin').mkdir()
        (self.root/'bin/curl').write_text('#!/bin/sh\nexit 22\n')
        (self.root/'bin/curl').chmod(0o755)
        self.env = dict(os.environ, HOME=str(self.home), T3CODE_HOME=str(self.base),
                        T3CODE_INSTALL_BIN_DIR=str(self.home/'.local/bin'), FIXTURE=str(self.root),
                        NATIVE_T3=str(T3), NATIVE_NODE=self.node,
                        PATH=str(self.root/'bin')+':'+os.environ['PATH'])
        self.log = (self.root/'log').open('w')
        self.processes = []

    def tearDown(self):
        for process in self.processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    pass
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=5)
        self.log.close()
        self.temp.cleanup()

    def start(self):
        process = subprocess.Popen(['sh', str(LAUNCHER), 'start'], env=self.env,
            stdout=self.log, stderr=self.log, start_new_session=True)
        self.processes.append(process)
        return process

    def wait(self, condition):
        deadline = time.monotonic()+20
        while time.monotonic()<deadline:
            if condition():
                return
            time.sleep(.05)
        self.fail((self.root/'log').read_text())

    def outcome(self):
        return json.loads(self.state.read_text()).get('update', {}).get('status')

    def exercise(self, fails):
        if fails:
            (self.root/'fail-trial').touch()
        process = self.start()
        status = 'rolled-back' if fails else 'committed'
        version = '0.0.1' if fails else '0.0.2'
        self.wait(lambda: self.outcome()==status)
        self.wait(lambda: json.loads((self.root/'context.json').read_text()).get('childVersion')==version)
        if not fails:
            self.wait(lambda: (self.root/'committed').exists())
        self.assertEqual(json.loads(self.state.read_text())['activeVersion'], version)
        self.assertEqual((self.root/'database').read_text(), 'original database' if fails else 'trial migration')
        before = self.state.read_bytes()
        # Signal only the launcher, as Podman does. It must reap its server child.
        child = int((self.root/'child.pid').read_text())
        process.terminate()
        self.assertEqual(process.wait(timeout=10), 0)
        with self.assertRaises(ProcessLookupError):
            os.kill(child, 0)
        (self.root/'context.json').unlink()
        self.start()
        self.wait(lambda: (self.root/'context.json').exists())
        self.assertEqual(json.loads((self.root/'context.json').read_text())['childVersion'], version)
        self.assertEqual(self.state.read_bytes(), before)

    def test_commit_survives_restart(self):
        self.exercise(False)

    def test_failed_trial_restores_database_and_survives_restart(self):
        self.exercise(True)


if __name__ == '__main__':
    unittest.main()
