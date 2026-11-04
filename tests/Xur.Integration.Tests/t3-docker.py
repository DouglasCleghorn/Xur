"""Exercise DinD installation selection, readiness, failure and shutdown offline."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest

SOURCE = Path('src/Xur.Domain/t3-docker.py').resolve()
EVIDENCE = Path('.build/evidence/t3-docker').resolve()
EVIDENCE.mkdir(parents=True, exist_ok=True)
FAKE = r'''#!/usr/bin/env python3
import os, pathlib, signal, sys, time
root=pathlib.Path(os.environ['FIXTURE'])
tool=pathlib.Path(sys.argv[0]).name
args=sys.argv[1:]
if tool=='curl':
    pathlib.Path(args[-1]).write_text('fixture signing key')
elif tool=='apt-get':
    if (root/'offline').exists(): sys.exit(22)
    if '--print-uris' in args:
        print('fixture signed packages '+(root/'version').read_text())
    elif 'download' in args:
        for name in args[args.index('download')+1:]:
            if (root/'incomplete').exists() and name=='docker-compose-plugin': continue
            pathlib.Path(name+'.deb').touch()
elif tool=='dpkg-deb':
    stage=pathlib.Path(args[-1]);bin=stage/'usr/bin';bin.mkdir(parents=True,exist_ok=True)
    for name in ('docker','dockerd','containerd','runc','rootlesskit','dockerd-rootless.sh'):
        (bin/name).write_bytes((root/'fake').read_bytes());(bin/name).chmod(0o700)
    plugins=stage/'usr/libexec/docker/cli-plugins';plugins.mkdir(parents=True,exist_ok=True)
    for name in ('docker-compose','docker-buildx'):
        (plugins/name).write_bytes((root/'fake').read_bytes());(plugins/name).chmod(0o700)
    notice=stage/'usr/share/doc/docker-ce/copyright';notice.parent.mkdir(parents=True,exist_ok=True);notice.write_text('fixture package notice')
elif args==['--version']: print('fixture Docker version')
elif tool=='docker' and args==['info']:
    sys.exit(0 if (root/'ready').exists() else 1)
elif tool=='dockerd-rootless.sh':
    (root/'daemon-started').write_text('\n'.join(args))
    def stop(*args):
        (root/'daemon-stopped').touch();sys.exit(0)
    signal.signal(signal.SIGTERM,stop)
    while True:
        if (root/'daemon-fail').exists(): sys.exit(42)
        if not (root/'hold').exists(): (root/'ready').touch()
        time.sleep(.02)
elif tool=='server':
    (root/'server-started').touch()
    def stop(*args):
        (root/'server-stopped').touch();sys.exit(0)
    signal.signal(signal.SIGTERM,stop)
    while True: time.sleep(.02)
'''


class DockerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=EVIDENCE)
        self.root = Path(self.temporary.name)
        self.home = self.root / 'home'
        self.home.mkdir()
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        (self.root / 'fake').write_text(FAKE)
        (self.root / 'version').write_text('first')
        for tool in ('curl', 'apt-get', 'dpkg-deb', 'server'):
            binary = self.bin / tool
            binary.write_text(FAKE)
            binary.chmod(0o700)
        self.env = dict(os.environ, HOME=str(self.home), FIXTURE=str(self.root),
                        PATH=str(self.bin) + ':' + os.environ['PATH'],
                        XDG_RUNTIME_DIR=str(self.root / 'run'), DOCKER_HOST='unix://' + str(self.root / 'run/docker.sock'))
        self.log = (self.root / 'log').open('w')
        self.processes = []

    def tearDown(self):
        for process in self.processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=12)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
        self.log.close()
        self.temporary.cleanup()

    @property
    def current(self):
        return self.home / '.local/share/xur-docker/current'

    def execute(self, action, background=False):
        code = 'from pathlib import Path\nSOURCE = Path(' + repr(str(SOURCE)) + ').read_text()\nexec(SOURCE)\nimport sys\n' + action
        args = [sys.executable, '-c', code]
        if background:
            process = subprocess.Popen(args, env=self.env, stdout=self.log, stderr=self.log, start_new_session=True)
            self.processes.append(process)
            return process
        return subprocess.run(args, env=self.env, capture_output=True, text=True, timeout=15)

    def wait_for(self, name):
        until = time.monotonic() + 8
        while not (self.root / name).exists() and time.monotonic() < until:
            time.sleep(.02)
        self.assertTrue((self.root / name).exists(), (self.root / 'log').read_text())

    def test_signed_selection_persists_notices_and_plugins_and_retains_saved_install_on_failure(self):
        result = self.execute('update()')
        self.assertEqual(result.returncode, 0, result.stderr)
        first = self.current.resolve()
        self.assertEqual((first / 'usr/share/doc/docker-ce/copyright').read_text(), 'fixture package notice')
        self.assertTrue((self.home / '.docker/cli-plugins/docker-compose').is_file())
        (self.root / 'version').write_text('second')
        result = self.execute('update()')
        self.assertEqual(result.returncode, 0, result.stderr)
        second = self.current.resolve()
        self.assertNotEqual(first, second)
        self.assertTrue(first.exists())
        (self.root / 'offline').touch()
        result = self.execute('update()')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.current.resolve(), second)

    def test_incomplete_upgrade_retains_selection_and_manual_plugin(self):
        self.assertEqual(self.execute('update()').returncode, 0)
        first = self.current.resolve()
        plugin = self.home / '.docker/cli-plugins/docker-compose'
        plugin.unlink()
        plugin.write_text('manually installed plugin')
        (self.root / 'version').write_text('incomplete upgrade')
        (self.root / 'incomplete').touch()
        failed = self.execute('update()')
        self.assertNotEqual(failed.returncode, 0)
        self.assertEqual(self.current.resolve(), first)
        self.assertEqual(plugin.read_text(), 'manually installed plugin')
        (self.root / 'incomplete').unlink()
        self.assertEqual(self.execute('update()').returncode, 0)
        self.assertNotEqual(self.current.resolve(), first)
        self.assertEqual(plugin.read_text(), 'manually installed plugin')

    def test_daemon_exit_before_readiness_never_starts_server(self):
        (self.root / 'daemon-fail').touch()
        process = self.execute("sys.exit(run(['server']))", background=True)
        self.assertNotEqual(process.wait(timeout=12), 0)
        self.assertFalse((self.root / 'server-started').exists())
        self.assertIn('before becoming ready', (self.root / 'log').read_text())

    def test_server_waits_for_daemon_and_shutdown_stops_both(self):
        (self.root / 'hold').touch()
        process = self.execute("sys.exit(run(['server']))", background=True)
        self.wait_for('daemon-started')
        self.assertFalse((self.root / 'server-started').exists())
        args = (self.root / 'daemon-started').read_text()
        self.assertIn('--data-root=' + str(self.home / '.local/share/docker'), args)
        self.assertIn('--host=' + self.env['DOCKER_HOST'], args)
        self.assertNotIn('tcp://', args)
        (self.root / 'hold').unlink()
        self.wait_for('server-started')
        process.terminate()
        self.assertEqual(process.wait(timeout=12), 0)
        self.wait_for('server-stopped')
        self.wait_for('daemon-stopped')

    def test_offline_replacement_uses_saved_runtime_and_daemon_failure_stops_server(self):
        self.assertEqual(self.execute('update()').returncode, 0)
        (self.root / 'offline').touch()
        process = self.execute("sys.exit(run(['server']))", background=True)
        self.wait_for('server-started')
        (self.root / 'daemon-fail').touch()
        self.assertNotEqual(process.wait(timeout=12), 0)
        self.wait_for('server-stopped')
        self.assertIn('Docker stopped unexpectedly', (self.root / 'log').read_text())

    def test_failed_first_install_does_not_start_server(self):
        (self.root / 'offline').touch()
        process = self.execute("sys.exit(run(['server']))", background=True)
        self.assertNotEqual(process.wait(timeout=12), 0)
        self.assertFalse((self.root / 'server-started').exists())
        self.assertFalse(self.current.exists())


if __name__ == '__main__':
    unittest.main()
