#!/usr/bin/env python3
"""Regression checks for VM isolation, approval, timeouts and failure reporting."""
import importlib.util
import errno
import io
import json
import pathlib
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.error
from unittest.mock import Mock, patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('vm_tests', ROOT / 'eng/test-vm.py')
vm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vm)


class VmTests(unittest.TestCase):
    def setUp(self):
        (ROOT / '.build').mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=ROOT / '.build')
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.iso = self.root / 'release.iso'
        self.iso.write_bytes(b'unchanged release ISO')
        self.toolchain = (self.root, self.root / 'qemu', self.iso, self.iso, 'genisoimage')

    def machine(self, scenario='install'):
        return vm.Vm(self.root / scenario, self.iso, self.toolchain, 8192, 4, scenario)

    def test_iso_identity_is_checked_before_boot(self):
        identity = vm.verify_input(self.iso, None, vm.sha(self.iso))
        self.assertEqual(identity['sha256'], vm.sha(self.iso))
        for expected in ('f' * 64, '', 'G' * 64):
            with self.assertRaises(RuntimeError):
                vm.verify_input(self.iso, None, expected)
        with self.assertRaises(RuntimeError):
            vm.verify_input(pathlib.Path('/dev/null'), None, 'f' * 64)

    def test_qemu_boots_original_media_with_only_file_backed_disks(self):
        machine = self.machine()
        args = machine.arguments()
        self.assertNotIn('-kernel', args)
        self.assertNotIn('-append', args)
        self.assertEqual(args[args.index('-machine') + 1], 'q35,accel=kvm,hpet=off')
        drives = [args[i + 1] for i, arg in enumerate(args) if arg == '-drive']
        self.assertTrue(any(f'file={self.iso},format=raw,readonly=on' in d for d in drives))
        self.assertTrue(all('file=/dev/' not in d for d in drives))
        writable = [d for d in drives if 'readonly=on' not in d]
        self.assertEqual(len(writable), 3)  # firmware variables, data and target
        self.assertTrue(all(str(machine.directory) in d for d in writable))
        network = args[args.index('-netdev') + 1]
        self.assertEqual(network.count('hostfwd=tcp:127.0.0.1:'), 2)
        self.assertNotEqual(machine.diagnostic_port, machine.web_port)
        self.assertIn('restrict=on', self.machine('offline').arguments()[args.index('-netdev') + 1])
        self.assertFalse(any('id=target,' in arg for arg in self.machine('no-disks').arguments()))

    def test_existing_disk_and_symlink_are_not_overwritten(self):
        disk = self.root / 'disk.raw'
        disk.write_bytes(b'preserve')
        with self.assertRaises(FileExistsError):
            vm.sparse(disk, 1024)
        link = self.root / 'link.raw'
        link.symlink_to(disk)
        with self.assertRaises(FileExistsError):
            vm.sparse(link, 1024)
        self.assertEqual(disk.read_bytes(), b'preserve')

    def test_configuration_does_not_contain_install_approval(self):
        machine = self.machine()
        def generate(args, **kwargs):
            pathlib.Path(args[args.index('-o') + 1]).write_bytes(b'configuration fixture')
        with patch.object(subprocess, 'run', side_effect=generate):
            machine.prepare()
        answer = (machine.directory / 'config/xur.yml').read_text()
        self.assertIn('bootstrapToken:', answer)
        self.assertNotIn('approve', answer)
        self.assertNotIn('target', answer)
        self.assertEqual((machine.directory / 'target.raw').stat().st_size, 80 * 1024**3)
        self.assertTrue((machine.directory / 'credentials.private.json').is_file())

    def test_negative_scenario_configuration_still_requires_explicit_approval(self):
        def generate(args, **kwargs):
            pathlib.Path(args[args.index('-o') + 1]).write_bytes(b'configuration fixture')
        with patch.object(subprocess, 'run', side_effect=generate):
            for scenario in ('read-only', 'conflicting-answers'):
                machine = self.machine(scenario)
                machine.prepare()
                config = machine.directory / 'config'
                diagnostic = (config / 'xur-diagnostics.yml').read_text()
                self.assertIn('allowControl: ' + ('false' if scenario == 'read-only' else 'true'), diagnostic)
                self.assertEqual(len(list(config.glob('xur-diagnostics.*'))), 1)
                answers = [p for p in config.iterdir() if p.name in ('xur.yml', 'xur.yaml')]
                self.assertEqual(len(answers), 2 if scenario == 'conflicting-answers' else 1)
                self.assertTrue(all('approve' not in p.read_text() for p in answers))

    def test_unapproved_target_verification_detects_writes_and_truncation(self):
        disk = self.root / 'target.raw'
        vm.sparse(disk, 1024 * 1024)
        self.assertTrue(vm.untouched_sparse(disk, 1024 * 1024))
        self.assertFalse(vm.untouched_sparse(disk, 2 * 1024 * 1024))
        with disk.open('r+b') as stream:
            stream.seek(512 * 1024)
            stream.write(b'erased disk')
        self.assertFalse(vm.untouched_sparse(disk, 1024 * 1024))

    def test_unapproved_target_verification_does_not_ignore_unsupported_filesystem(self):
        disk = self.root / 'target.raw'
        vm.sparse(disk, 1024)
        with patch.object(vm.os, 'lseek', side_effect=OSError(errno.EINVAL, 'Unsupported')):
            with self.assertRaises(OSError):
                vm.untouched_sparse(disk, 1024)

    def test_diagnostic_browser_origin_is_explicit(self):
        error = urllib.error.HTTPError('https://127.0.0.1:1234', 403, 'Forbidden', {}, io.BytesIO())
        with patch.object(vm.urllib.request, 'urlopen', side_effect=error) as request:
            self.assertIsNone(vm.Api(1234, 'credential').request('/v1/status', expected=403,
                                                              origin='https://localhost'))
        self.assertEqual(request.call_args.args[0].get_header('Origin'), 'https://localhost')

    def test_toolchain_identity_binds_the_executable_and_firmware(self):
        self.toolchain[1].write_bytes(b'QEMU fixture')
        with patch.object(subprocess, 'check_output', return_value='QEMU emulator version 10.2.1\nCopyright\n'):
            identity = vm.tool_identity(self.toolchain)
        self.assertEqual(identity['qemuVersion'], 'QEMU emulator version 10.2.1')
        self.assertEqual(identity['qemuSha256'], vm.sha(self.toolchain[1]))
        self.assertEqual(identity['ovmfCodeSha256'], vm.sha(self.iso))
        with patch.object(subprocess, 'check_output', return_value='unknown tool\n'):
            with self.assertRaisesRegex(RuntimeError, 'Unrecognized QEMU'):
                vm.tool_identity(self.toolchain)

    def test_read_only_scenario_never_sends_permitted_control(self):
        machine = self.machine('read-only')
        machine.read_status = Mock(return_value={'bundle': 'bundle', 'allowControl': False,
            'installer': {'installer': True, 'operation': None, 'scan': {'state': 'AnswerFound'}}})
        screen = {'screen': 'computer-name', 'revision': 'r', 'acceptsText': True,
                  'options': [{'id': 48, 'label': 'Back to menu', 'enabled': True}]}
        machine.diagnostics.request = Mock(return_value=screen)
        machine.choose = Mock()
        with patch.object(vm.Api, 'request'), patch('sys.stdout', new_callable=io.StringIO):
            self.assertEqual(machine.live(1), screen)
        actions = [c for c in machine.diagnostics.request.call_args_list if c.args[0].endswith('/action')]
        self.assertEqual(len(actions), 3)
        self.assertTrue(all(c.args[2] == 403 for c in actions))
        self.assertEqual(machine.read_status.call_count, 1)
        machine.choose.assert_not_called()

    def test_conflicting_answers_reach_no_selectable_disk(self):
        machine = self.machine('conflicting-answers')
        machine.read_status = Mock(return_value={'bundle': 'bundle', 'installer': {
            'installer': True, 'operation': None, 'scan': {'state': 'Ambiguous'}}})
        name = {'screen': 'computer-name', 'revision': 'name', 'acceptsText': True}
        disks = {'screen': 'setup-disks', 'options': [{'id': 256, 'enabled': False}]}
        machine.diagnostics.request = Mock(side_effect=[None, name, None, disks])
        machine.choose = Mock()
        with patch.object(vm.Api, 'request'), patch('sys.stdout', new_callable=io.StringIO):
            self.assertEqual(machine.live(1), disks)
        self.assertIn('Ambiguous answer files block disk selection', machine.checks)

    def test_cancel_and_decline_never_approve_or_reuse_old_screen(self):
        machine = self.machine('cancel')
        review = {'screen': 'setup-review', 'revision': 'review'}
        confirmation = {'screen': 'setup-confirm', 'revision': 'confirm'}
        disks = {'screen': 'setup-disks', 'revision': 'disks'}
        machine.review = Mock(return_value=review)
        machine.choose = Mock(side_effect=[disks, confirmation, disks])
        machine.diagnostics.request = Mock(return_value=confirmation)
        machine.read_status = Mock(return_value={'installer': {'operation': None}})
        with patch('sys.stdout', new_callable=io.StringIO):
            machine.cancel(disks)
        actions = [c for c in machine.diagnostics.request.call_args_list if c.args[0].endswith('/action')]
        self.assertEqual([c.args[1]['revision'] for c in actions], ['review', 'confirm'])
        self.assertTrue(all(c.args[2] == 409 for c in actions))
        self.assertEqual([c.args[0] for c in machine.choose.call_args_list],
                         ['Cancel', 'Continue to erase confirmation', 'No'])

    def test_failed_vm_run_rechecks_input_and_stops_owned_vm(self):
        self.run_with_input_change(fail_boot=True)

    def test_changed_input_cannot_produce_a_passing_receipt(self):
        self.run_with_input_change(fail_boot=False)

    def run_with_input_change(self, fail_boot):
        previous = signal.getsignal(signal.SIGTERM)
        self.addCleanup(signal.signal, signal.SIGTERM, previous)
        original_sha = vm.sha(self.iso)
        machine = self.machine('offline')
        machine.prepare = Mock()
        machine.start = Mock()
        machine.stop = Mock()
        machine.collect_private_logs = Mock()
        machine.bundle = 'bundle'
        machine.read_status = Mock(return_value={'installer': {'operation': None}})
        def live(timeout):
            self.iso.write_bytes(b'changed outside QEMU')
            if fail_boot:
                raise RuntimeError('Boot failed')
            return {}
        machine.live = live
        arguments = ['test-vm.py', str(self.iso), '--sha256', original_sha,
                     '--run-id', 'identity', '--scenario', 'offline']
        with patch.object(vm, 'ROOT', self.root), patch.object(sys, 'argv', arguments), \
             patch.object(vm, 'tools', return_value=self.toolchain), patch.object(vm, 'ensure_kvm'), \
             patch.object(vm, 'tool_identity', return_value={}), \
             patch.object(subprocess, 'check_output', return_value='a' * 40), \
             patch.object(vm, 'Vm', return_value=machine), patch('sys.stderr', new_callable=io.StringIO), \
             patch('sys.stdout', new_callable=io.StringIO):
            self.assertEqual(vm.main(), 1)
        machine.stop.assert_called_once()
        receipt = json.loads((self.root / '.build/evidence/vm-tests/identity/receipt.json').read_text())
        self.assertEqual(receipt['result'], 'Failed')
        self.assertFalse(receipt['fullQualification'])
        self.assertFalse(receipt['iso']['unchanged'])
        self.assertEqual(receipt['iso']['postRunSha256'], vm.sha(self.iso))
        if fail_boot:
            machine.collect_private_logs.assert_called_once()
            self.assertEqual(receipt['failure']['message'], 'Boot failed')

    def test_mutations_are_never_retried(self):
        with patch.object(vm.urllib.request, 'urlopen', side_effect=urllib.error.URLError('lost connection')) as request:
            with self.assertRaises(urllib.error.URLError):
                vm.Api(1234).request('/v1/console/action', {'confirmErase': True})
        self.assertEqual(request.call_count, 1)

    def test_negative_http_check_does_not_expose_response(self):
        error = urllib.error.HTTPError('https://127.0.0.1:1234', 409, 'Conflict',
            {'Content-Type': 'application/json'}, io.BytesIO(b'{"error":"private-password"}'))
        with patch.object(vm.urllib.request, 'urlopen', side_effect=error):
            with self.assertRaisesRegex(RuntimeError, 'expected HTTP 200, received 409') as caught:
                vm.Api(1234).request('/v1/console/action', {})
        self.assertNotIn('private-password', str(caught.exception))

    def test_deadline_does_not_retry_mutation(self):
        with patch.object(vm.time, 'monotonic', side_effect=[0, 2]):
            with self.assertRaisesRegex(RuntimeError, 'Timed out'):
                vm.wait_for(lambda: None, lambda value: False, 1, 'fixture')

    def test_review_without_exact_disk_is_rejected_before_approval(self):
        machine = self.machine()
        machine.target_path = '/dev/vda'
        machine.diagnostics = Mock()
        machine.diagnostics.request.return_value = {'screen': 'setup-review', 'body': 'Wrong target'}
        with self.assertRaisesRegex(RuntimeError, 'Review identifies target'):
            machine.install({'revision': 'r', 'options': [{'id': 256, 'enabled': True, 'label': '/dev/vda · SSD'}]}, 1)
        self.assertEqual(machine.diagnostics.request.call_count, 1)

    def test_target_serial_in_unaffected_list_is_not_erase_approval(self):
        machine = self.machine()
        machine.target_path = '/dev/vda'
        machine.diagnostics = Mock()
        machine.diagnostics.request.return_value = {'screen': 'setup-review', 'body':
            'WILL ERASE /dev/vdb\nSerial: WRONG\nPlanned changes:\nBtrfs\nOther disks: ' +
            vm.TARGET_SERIAL + ' ' + vm.DATA_SERIAL + ' ' + vm.CONFIG_SERIAL}
        with self.assertRaisesRegex(RuntimeError, 'Review identifies target'):
            machine.install({'revision': 'r', 'options': [{'id': 256, 'enabled': True, 'label': '/dev/vda · SSD'}]}, 1)
        self.assertEqual(machine.diagnostics.request.call_count, 1)

    def test_installation_failure_does_not_retry_approval_or_reboot(self):
        machine = self.machine()
        machine.target_path = '/dev/vda'
        confirmation = {'screen': 'setup-confirm', 'revision': 'confirm', 'options': [
            {'id': ord('y'), 'label': 'Yes', 'enabled': True}],
            'body': 'Serial: ' + vm.TARGET_SERIAL}
        review = {'screen': 'setup-review', 'body': '/dev/vda\nSerial: ' + vm.TARGET_SERIAL +
                  '\nPlanned changes:\nBtrfs\nOther disks: ' + vm.DATA_SERIAL + ' ' + vm.CONFIG_SERIAL}
        machine.diagnostics = Mock()
        calls = []
        def request(path, body=None, expected=200):
            calls.append((path, body, expected))
            if path == '/v1/console':
                return confirmation
            return review if body['option'] == 256 else {}
        machine.diagnostics.request.side_effect = request
        machine.choose = Mock()
        machine.installed = Mock()
        machine.read_status = Mock(side_effect=[{'installer': {'operation': None}},
            {'installer': {'operation': {'stage': 'Failed', 'progress': {'currentStep': 'Clock'}}}}])
        with patch('sys.stdout', new_callable=io.StringIO):
            with self.assertRaisesRegex(RuntimeError, 'Installation failed'):
                machine.install({'revision': 'r', 'options': [
                    {'id': 256, 'enabled': True, 'label': '/dev/vda · SSD'}]}, 1)
        approvals = [body for path, body, expected in calls if path.endswith('/action') and
                     expected == 200 and body.get('confirmErase')]
        self.assertEqual(len(approvals), 1)
        self.assertEqual(machine.installation, {'stage': 'Failed', 'step': 'Clock'})
        machine.choose.assert_called_once_with('Continue to erase confirmation')
        machine.installed.assert_not_called()

    def test_boot_readiness_retries_transient_status_but_not_authentication(self):
        read = Mock(side_effect=[vm.ApiStatusError('/v1/status', 200, 503), {'ready': True}])
        with patch.object(vm.time, 'sleep'):
            self.assertEqual(vm.wait_for(read, bool, 5, 'readiness'), {'ready': True})
        read = Mock(side_effect=vm.ApiStatusError('/v1/status', 200, 401))
        with self.assertRaises(vm.ApiStatusError):
            vm.wait_for(read, bool, 5, 'authentication')
        self.assertEqual(read.call_count, 1)

    def test_preflight_failure_still_writes_failed_evidence(self):
        previous = signal.getsignal(signal.SIGTERM)
        self.addCleanup(signal.signal, signal.SIGTERM, previous)
        arguments = ['test-vm.py', str(self.iso), '--sha256', 'f' * 64, '--run-id', 'preflight']
        with patch.object(vm, 'ROOT', self.root), patch.object(sys, 'argv', arguments), \
             patch.object(vm, 'verify_input', return_value={'sha256': 'f' * 64}), \
             patch.object(vm, 'tools', return_value=self.toolchain), \
             patch.object(vm, 'tool_identity', return_value={}), \
             patch.object(vm.os, 'access', return_value=False), \
             patch.object(subprocess, 'check_output', return_value='a' * 40), \
             patch.object(vm, 'Vm') as machine, patch('sys.stderr', new_callable=io.StringIO), \
             patch('sys.stdout', new_callable=io.StringIO):
            self.assertEqual(vm.main(), 1)
        machine.assert_not_called()
        receipt = json.loads((self.root / '.build/evidence/vm-tests/preflight/receipt.json').read_text())
        self.assertEqual(receipt['result'], 'Failed')
        self.assertIn('KVM read/write access', receipt['failure']['message'])
        self.assertEqual(receipt['scenarios'], [])

    def test_kvm_permission_refresh_is_explicit_and_user_scoped(self):
        with patch.object(subprocess, 'run') as command, patch.object(vm.os, 'access', return_value=True):
            vm.ensure_kvm()
            command.assert_not_called()
            vm.ensure_kvm(True)
        self.assertEqual(command.call_args.args[0],
                         ['sudo', '-n', 'setfacl', '-m', f'u:{vm.os.getuid()}:rw', '/dev/kvm'])

    def test_qmp_handles_events_and_long_workspace_paths(self):
        directory = self.root / ('long-' + 'a' * 85)
        directory.mkdir()
        fd = vm.os.open(directory, vm.os.O_RDONLY | vm.os.O_DIRECTORY)
        self.addCleanup(vm.os.close, fd)
        server = socket.socket(socket.AF_UNIX)
        self.addCleanup(server.close)
        server.bind(f'/proc/self/fd/{fd}/qmp.sock')
        server.listen(1)
        server.settimeout(5)
        commands, failures = [], []
        def monitor():
            try:
                client, _ = server.accept()
                with client, client.makefile('rwb', buffering=0) as stream:
                    stream.write(b'{"QMP":{}}\n')
                    for _ in range(2):
                        commands.append(json.loads(stream.readline()))
                        stream.write(b'{"event":"RESET"}\n{"return":{}}\n')
            except Exception as error:
                failures.append(error)
        thread = threading.Thread(target=monitor, daemon=True)
        thread.start()
        vm.Qmp(directory / 'qmp.sock').command('quit')
        thread.join(5)
        self.assertFalse(thread.is_alive())
        self.assertEqual(failures, [])
        self.assertEqual([c['execute'] for c in commands], ['qmp_capabilities', 'quit'])

    def test_cleanup_terminates_only_owned_process(self):
        machine = self.machine()
        machine.process = Mock()
        machine.process.poll.return_value = None
        machine.process.wait.side_effect = [subprocess.TimeoutExpired('qemu', 15), 0]
        machine.qmp = Mock()
        machine.qmp.command.side_effect = OSError('closed')
        machine.stop()
        machine.process.terminate.assert_called_once()
        machine.process.kill.assert_called_once()


if __name__ == '__main__':
    unittest.main()
