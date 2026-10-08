#!/usr/bin/env python3
"""Qualify an unchanged installer ISO in disposable UEFI/KVM VMs.

Uses the supported diagnostic console API, not a replacement installer or a
guest root shell. Only newly created file-backed disks are attached writable.
"""
import argparse
import datetime
import errno
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
TARGET_SERIAL = 'XUR-VM-TARGET'
DATA_SERIAL = 'XUR-VM-PRESERVE'
CONFIG_SERIAL = 'XUR-VM-CONFIG'
SCENARIOS = ('offline', 'no-disks', 'read-only', 'conflicting-answers', 'cancel', 'install')


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


class ApiStatusError(RuntimeError):
    def __init__(self, path, expected, received):
        super().__init__(f'{path}: expected HTTP {expected}, received {received}')
        self.status = received


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def wait_for(read, accept, timeout, description, interval=2):
    deadline = time.monotonic() + timeout
    while True:
        try:
            value = read()
            if accept(value):
                return value
        except (OSError, urllib.error.URLError, json.JSONDecodeError):
            pass
        except ApiStatusError as error:
            if error.status not in (404, 429, 500, 502, 503, 504):
                raise
        if time.monotonic() >= deadline:
            raise RuntimeError('Timed out: ' + description)
        time.sleep(min(interval, max(0, deadline - time.monotonic())))


class Api:
    def __init__(self, port, token=None):
        self.base = f'https://127.0.0.1:{port}'
        self.token = token
        # This is the loopback port of our own QEMU process, with a per-boot
        # self-signed certificate. Never use this client for remote lab hosts.
        self.context = ssl._create_unverified_context()

    def request(self, path, body=None, expected=200, origin=None):
        headers = {'Content-Type': 'application/json'}
        if origin is not None:
            headers['Origin'] = origin
        if self.token:
            headers['Authorization'] = 'Bearer ' + self.token
        request = urllib.request.Request(self.base + path, headers=headers,
            data=None if body is None else json.dumps(body).encode())
        # No mutation retries: an approval/reboot timeout is an uncertain result.
        try:
            response = urllib.request.urlopen(request, timeout=20, context=self.context)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            if response.status != expected:
                raise ApiStatusError(path, expected, response.status)
            raw = response.read(2 * 1024 * 1024 + 1)
            require(len(raw) <= 2 * 1024 * 1024, 'API response exceeds limit')
            if not raw:
                return None
            return json.loads(raw) if 'application/json' in response.headers.get('Content-Type', '') else raw.decode()


class Qmp:
    def __init__(self, path):
        self.path = path

    def command(self, name, arguments=None):
        directory_fd = os.open(self.path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            return self._command(name, arguments, f'/proc/self/fd/{directory_fd}/{self.path.name}')
        finally:
            os.close(directory_fd)

    def _command(self, name, arguments, address):
        with socket.socket(socket.AF_UNIX) as client:
            client.settimeout(10)
            client.connect(address)
            with client.makefile('rwb', buffering=0) as stream:
                require(bool(stream.readline()), 'QEMU monitor disconnected')
                def call(command, args):
                    stream.write(json.dumps({'execute': command, 'arguments': args}).encode() + b'\n')
                    while True:
                        line = stream.readline()
                        require(bool(line), 'QEMU monitor disconnected')
                        result = json.loads(line)
                        if 'error' in result:
                            raise RuntimeError('QEMU monitor rejected ' + command)
                        if 'return' in result:
                            return result['return']
                call('qmp_capabilities', {})
                return call(name, arguments or {})


def tools(qemu_root):
    candidates = [qemu_root] if qemu_root else [pathlib.Path('/usr'), pathlib.Path.home() / '.local/share/xur-build/qemu/usr']
    for root in candidates:
        qemu = root / 'bin/qemu-system-x86_64'
        firmware = root / 'share/OVMF'
        for suffix in ('_4M', ''):
            code, variables = firmware / f'OVMF_CODE{suffix}.fd', firmware / f'OVMF_VARS{suffix}.fd'
            if all(path.is_file() for path in (qemu, code, variables)):
                generator = shutil.which('genisoimage') or str(root / 'bin/genisoimage')
                require(pathlib.Path(generator).is_file(), 'Install genisoimage')
                return root, qemu, code, variables, generator
    raise RuntimeError('QEMU/OVMF unavailable; install qemu-system-x86 and ovmf or set --qemu-root')


def sparse(path, size):
    # Exclusive creation also refuses pre-existing files and symlinks.
    with path.open('xb') as stream:
        stream.truncate(size)


def untouched_sparse(path, size):
    """Prove a freshly created target still consists entirely of holes.

    Avoid reading 80 GiB of zeros. Unsupported SEEK_DATA fails closed; an
    allocated extent, including an extent containing zeros, fails this check.
    """
    with path.open('rb') as stream:
        if os.fstat(stream.fileno()).st_size != size:
            return False
        try:
            os.lseek(stream.fileno(), 0, os.SEEK_DATA)
        except OSError as error:
            if error.errno == errno.ENXIO:
                return True
            raise
        return False


def ports():
    # Reserve all ports together so each selected number is distinct.
    sockets = [socket.socket() for _ in range(2)]
    try:
        for client in sockets:
            client.bind(('127.0.0.1', 0))
        return [client.getsockname()[1] for client in sockets]
    finally:
        for client in sockets:
            client.close()


def ensure_kvm(refresh=False):
    if refresh:
        # Explicit opt-in: logind/udev can revoke this transient ACL when the
        # preceding VM exits. Refresh only the invoking user's device access.
        subprocess.run(['sudo', '-n', 'setfacl', '-m', f'u:{os.getuid()}:rw', '/dev/kvm'], check=True)
    require(os.access('/dev/kvm', os.R_OK | os.W_OK),
            'KVM read/write access is required; use --refresh-kvm-access if a transient ACL is needed')


def tool_environment(root):
    env = dict(os.environ)
    if root != pathlib.Path('/usr'):
        env['LD_LIBRARY_PATH'] = str(root / 'lib/x86_64-linux-gnu')
    return env


def tool_identity(toolchain):
    root, qemu, code, variables, _ = toolchain
    version = subprocess.check_output([str(qemu), '--version'], env=tool_environment(root),
                                      text=True, timeout=10).splitlines()[0]
    require(version.startswith('QEMU emulator version '), 'Unrecognized QEMU version response')
    return {'qemuVersion': version, 'qemuSha256': sha(qemu),
            'ovmfCodeSha256': sha(code), 'ovmfVariablesTemplateSha256': sha(variables)}


class Vm:
    def __init__(self, directory, iso, toolchain, memory, cpus, scenario):
        self.directory, self.iso = directory, iso
        self.toolchain, self.memory, self.cpus = toolchain, memory, cpus
        self.scenario = scenario
        self.process = None
        self.log = None
        self.checks = []
        self.diagnostic_key = secrets.token_hex(32)
        self.bootstrap = ''.join(secrets.choice('0123456789ABCDEFGHJKMNPQRSTVWXYZ') for _ in range(6))
        self.password = secrets.token_urlsafe(32)
        self.username = 'vm-owner'
        self.diagnostic_port, self.web_port = ports()
        self.diagnostics = Api(self.diagnostic_port, self.diagnostic_key)
        self.web = Api(self.web_port)
        self.qmp = Qmp(directory / 'qmp.sock')

    def check(self, condition, description):
        require(condition, description)
        self.checks.append(description)
        print(f'[{self.scenario}] {description}', flush=True)

    def prepare(self):
        self.directory.mkdir(parents=True, mode=0o700)
        config = self.directory / 'config'
        config.mkdir()
        (config / 'xur-diagnostics.yml').write_text(
            f'schemaVersion: 1\napiKey: {self.diagnostic_key}\nallowControl: '
            + ('false' if self.scenario == 'read-only' else 'true') + '\n')
        (config / 'xur.yml').write_text(f'schemaVersion: 1\nbootstrapToken: {self.bootstrap}\n')
        if self.scenario == 'conflicting-answers':
            (config / 'xur.yaml').write_text(f'schemaVersion: 1\nbootstrapToken: {self.bootstrap}\n')
        (self.directory / 'credentials.private.json').write_text(json.dumps({
            'username': self.username, 'password': self.password,
            'bootstrapToken': self.bootstrap, 'diagnosticKey': self.diagnostic_key}))
        config_iso = self.directory / 'config.iso'
        subprocess.run([self.toolchain[4], '-quiet', '-J', '-R', '-V', 'XUR_VM_CONFIG',
                        '-o', str(config_iso), str(config)], check=True, capture_output=True)
        self.config_sha = sha(config_iso)
        data = self.directory / 'data.raw'
        # A small recognizable data disk verifies preservation without host mounts.
        with data.open('xb') as stream:
            stream.write(b'Xur VM non-target preservation\0' * 1000)
            stream.truncate(32 * 1024 * 1024)
        self.data_sha = sha(data)
        if self.scenario != 'no-disks':
            self.target_bytes = 80 * 1024**3
            sparse(self.directory / 'target.raw', self.target_bytes)
            require(untouched_sparse(self.directory / 'target.raw', self.target_bytes),
                    'Fresh disposable target must contain only sparse holes')
        shutil.copyfile(self.toolchain[3], self.directory / 'OVMF_VARS.fd')

    def arguments(self):
        root, qemu, code, _, _ = self.toolchain
        args = [str(qemu), '-name', 'xur-vm-test-' + self.scenario,
            # The release kernel's RTC update-interrupt read stalls with this
            # QEMU HPET emulation. Keep the RTC enabled and use KVM's clock.
            '-machine', 'q35,accel=kvm,hpet=off', '-cpu', 'host', '-smp', str(self.cpus), '-m', str(self.memory),
            '-rtc', 'base=utc,clock=host,driftfix=slew',
            '-nodefaults', '-L', str(root / 'share/qemu'), '-display', 'none',
            '-device', f'VGA,romfile={root}/share/seabios/vgabios-stdvga.bin',
            '-drive', f'if=pflash,format=raw,readonly=on,file={code}',
            '-drive', f'if=pflash,format=raw,file={self.directory}/OVMF_VARS.fd',
            '-device', 'qemu-xhci,id=xhci',
            '-drive', f'if=none,id=installer,file={self.iso},format=raw,readonly=on',
            '-device', 'usb-storage,drive=installer,serial=XUR-VM-INSTALLER,bootindex=2',
            '-drive', f'if=none,id=config,file={self.directory}/config.iso,format=raw,readonly=on',
            '-device', f'virtio-blk-pci,drive=config,serial={CONFIG_SERIAL}',
            '-drive', f'if=none,id=data,file={self.directory}/data.raw,format=raw',
            '-device', f'virtio-blk-pci,drive=data,serial={DATA_SERIAL}',
            '-netdev', ('user,restrict=on,' if self.scenario == 'offline' else 'user,') +
                f'id=lan,hostfwd=tcp:127.0.0.1:{self.diagnostic_port}-:9443,hostfwd=tcp:127.0.0.1:{self.web_port}-:8443',
            '-device', 'virtio-net-pci,netdev=lan,romfile=',
            '-serial', f'file:{self.directory}/console.private.log',
            '-qmp', 'unix:qmp.sock,server=on,wait=off']
        if self.scenario != 'no-disks':
            args += ['-drive', f'if=none,id=target,file={self.directory}/target.raw,format=raw,discard=unmap',
                     '-device', f'virtio-blk-pci,drive=target,serial={TARGET_SERIAL},bootindex=1']
        return args

    def start(self):
        self.log = (self.directory / 'qemu.private.log').open('wb')
        self.process = subprocess.Popen(self.arguments(), cwd=self.directory, env=tool_environment(self.toolchain[0]),
                                        stdout=self.log, stderr=self.log)

    def alive(self):
        require(self.process.poll() is None, 'QEMU exited; inspect private VM logs')

    def read_status(self):
        self.alive()
        return self.diagnostics.request('/v1/status')

    def action(self, screen, option=None, text=None, confirm=False, expected=200):
        body = {'revision': screen['revision']}
        body.update({'text': text} if text is not None else {'option': option})
        if confirm:
            body['confirmErase'] = True
        return self.diagnostics.request('/v1/console/action', body, expected)

    def choose(self, label, confirm=False):
        screen = self.diagnostics.request('/v1/console')
        options = [o for o in screen['options'] if o['label'] == label and o['enabled']]
        require(len(options) == 1, 'Expected enabled console option: ' + label)
        return self.action(screen, option=options[0]['id'], confirm=confirm)

    def live(self, timeout):
        expected_scan = 'Ambiguous' if self.scenario == 'conflicting-answers' else 'AnswerFound'
        status = wait_for(self.read_status, lambda s: s['installer']['scan']['state'] == expected_scan,
                          timeout, 'installer diagnostics and answer discovery')
        self.bundle = status['bundle']
        self.check(status['installer']['installer'] is True, 'Live installer started')
        self.check(status['installer'].get('operation') is None, 'Answer file did not approve disk erasure')
        Api(self.diagnostic_port).request('/v1/status', expected=401)
        self.check(True, 'Anonymous diagnostic access denied')
        Api(self.diagnostic_port, secrets.token_hex(32)).request('/v1/status', expected=401)
        self.check(True, 'Incorrect diagnostic credential denied')
        self.diagnostics.request('/v1/status', expected=403, origin='https://localhost')
        self.check(True, 'Browser-origin diagnostic requests denied')
        screen = wait_for(lambda: self.diagnostics.request('/v1/console'),
            lambda s: s['screen'] == 'computer-name' or
                any(o['label'] == 'Setup and installation' and o['enabled'] for o in s['options']),
            30, 'setup console ready')
        if self.scenario == 'read-only':
            self.check(status['allowControl'] is False, 'Read-only diagnostic permission applied')
            option = next((o['id'] for o in screen['options'] if o['enabled']), None)
            require(option is not None, 'Expected a valid option to test control denial')
            self.action(screen, option=option, expected=403)
            self.action(screen, option=option, confirm=True, expected=403)
            if screen['acceptsText']:
                self.action(screen, text='must-not-change', expected=403)
            self.check(self.diagnostics.request('/v1/console')['revision'] == screen['revision'],
                       'Read-only console rejects control and erase consent without changing the screen')
            return screen
        # Some installer versions open setup automatically on first boot.
        if screen['screen'] != 'computer-name':
            screen = self.choose('Setup and installation')
        require(screen['screen'] == 'computer-name' and screen['acceptsText'], 'Expected server-name screen')
        self.action(screen, text='xur-vm-test')
        self.choose('Continue to disk selection')
        screen = self.diagnostics.request('/v1/console')
        self.check(screen['screen'] == 'setup-disks', 'Console reached disk selection')
        if self.scenario in ('no-disks', 'conflicting-answers'):
            self.check(not any(o['id'] >= 256 and o['enabled'] for o in screen['options']),
                       'Ambiguous answer files block disk selection' if self.scenario == 'conflicting-answers'
                       else 'No eligible installation disk is selectable')
        else:
            match = re.search(r'(/dev/[^\s]+) · [^\n]*\n\s*Serial: ' + TARGET_SERIAL + r'\b', screen['body'])
            require(match is not None, 'Disposable target serial absent from disk inventory')
            self.target_path = match.group(1)
            self.check(any(o['enabled'] and o['label'].startswith(self.target_path + ' ·')
                           for o in screen['options']), 'Disposable target disk is selectable')
        return screen

    def review(self, screen):
        option = next(o for o in screen['options'] if o['enabled'] and o['label'].startswith(self.target_path + ' ·'))
        review = self.action(screen, option=option['id'])
        require(review['screen'] == 'setup-review', 'Expected exact-disk review')
        erase = review['body'].split('Planned changes:')[0]
        self.check(self.target_path in erase and re.search(r'Serial: ' + TARGET_SERIAL + r'\b', erase) and
                   DATA_SERIAL in review['body'] and
                   CONFIG_SERIAL in review['body'], 'Review identifies target and preserved disks')
        self.check('Btrfs' in review['body'], 'Review plans a Btrfs installation')
        return review

    def cancel(self, screen):
        review = self.review(screen)
        canceled = self.choose('Cancel')
        self.check(canceled['screen'] == 'setup-disks', 'Cancel returns from disk review to selection')
        self.action(review, option=ord('y'), expected=409)
        self.check(self.read_status()['installer'].get('operation') is None,
                   'Canceled disk review cannot be reused')
        self.review(canceled)
        self.choose('Continue to erase confirmation')
        confirmation = self.diagnostics.request('/v1/console')
        declined = self.choose('No')
        self.check(declined['screen'] == 'setup-disks', 'Declining erasure returns to disk selection')
        self.action(confirmation, option=ord('y'), confirm=True, expected=409)
        self.check(self.read_status()['installer'].get('operation') is None,
                   'Declined erase confirmation cannot be reused')

    def install(self, screen, timeout):
        self.review(screen)
        self.choose('Continue to erase confirmation')
        confirmation = self.diagnostics.request('/v1/console')
        require(confirmation['screen'] == 'setup-confirm' and TARGET_SERIAL in confirmation['body'],
                'Expected confirmation of disposable target')
        yes = next(o['id'] for o in confirmation['options'] if o['label'] == 'Yes' and o['enabled'])
        self.action(confirmation, option=yes, expected=409)
        self.action(dict(confirmation, revision='invalid-stale-revision'), option=yes, confirm=True, expected=409)
        self.check(self.read_status()['installer'].get('operation') is None,
                   'Missing erase consent and stale screen both rejected without installation')
        self.action(confirmation, option=yes, confirm=True)

        previous = None
        def completed(status):
            nonlocal previous
            operation = status['installer'].get('operation') or {}
            stage = operation.get('stage')
            progress = operation.get('progress') or {}
            self.installation = {'stage': stage, 'step': progress.get('currentStep') or
                                 getattr(self, 'installation', {}).get('step')}
            require(stage != 'Failed', 'Installation failed; inspect private installation logs')
            step = (stage, progress.get('currentStep'))
            if step != previous:
                print(f'[install] Stage: {stage}; step: {step[1]}', flush=True)
                previous = step
            return stage == 'Complete'
        wait_for(self.read_status, completed, timeout, 'online Bazzite installation', interval=5)
        self.check(True, 'Real Anaconda/bootc installation completed')
        # The product menu reboots through its own power API. Do not reset QEMU.
        wait_for(lambda: self.diagnostics.request('/v1/console'),
                 lambda s: any(o['label'] == 'Reboot into installed system' for o in s['options']),
                 30, 'completed installation console')
        self.choose('Reboot into installed system')
        self.choose('Reboot now')
        return self.installed(timeout=300)

    def installed(self, timeout):
        status = wait_for(lambda: self.web.request('/api/status'), lambda s: s['mode'] == 'Installed',
                          timeout, 'installed system boot with installer USB attached')
        self.check(True, 'Installed system booted with installer USB attached')
        self.web.request('/api/profiles', expected=401)
        setup = self.web.request('/api/bootstrap', {'token': self.bootstrap})
        require(setup.get('setupRequired') is True, 'Expected required administrator creation')
        self.web.token = setup['accessToken']
        login = self.web.request('/api/auth/setup', {'username': self.username, 'password': self.password})
        self.web.token = login['accessToken']
        self.check(True, 'Post-install administrator account created through HTTPS')
        current = self.web.request('/api/application-updates')['current']['id']
        self.check(current == self.bundle, 'Installed app matches the ISO embedded bundle')
        mounts = self.web.request('/api/storage/mounts')
        self.check(any(m['type'] == 'btrfs' and m['path'] in ('/', '/sysroot', '/var', '/var/home')
                       for m in mounts), 'Installed persistent storage uses Btrfs')
        self.os_deployment = self.web.request('/api/updates')['current']
        self.check(bool(self.os_deployment and self.os_deployment.get('digest')),
                   'Installed Bazzite image digest recorded')
        profile = self.web.request('/api/profiles/create', {})
        profile['name'] = 'VM persistence check'
        profile = self.web.request('/api/profiles', profile)
        self.web.request('/api/profiles/' + profile['id'] + '/load', {})
        wait_for(lambda: self.web.request('/api/profiles'),
                 lambda s: (s.get('active') or {}).get('id') == profile['id'] and
                    (s.get('operation') or {}).get('stage') == 'Complete',
                 60, 'empty profile load')
        self.check(True, 'Saved profile loads through the installed manager')
        self.web.request('/api/power/reboot', {}, expected=202)
        wait_for(lambda: self.web.request('/api/status'),
                 lambda s: s['mode'] == 'Installed' and s['bootId'] != status['bootId'],
                 timeout, 'second product reboot')
        profiles = self.web.request('/api/profiles')['profiles']
        self.check(any(p['id'] == profile['id'] and p['name'] == profile['name'] for p in profiles),
                   'Manager session and saved profile survive a second reboot')
        self.web.request('/api/auth/login', {'username': self.username, 'password': self.password})
        self.check(True, 'Password login survives reboot')
        self.check(self.web.request('/api/application-updates')['current']['id'] == self.bundle,
                   'Embedded bundle remains selected after reboot')
        try:
            self.diagnostics.request('/v1/status')
        except (OSError, urllib.error.URLError):
            self.check(True, 'Installer diagnostic listener is absent after installation')
        else:
            raise RuntimeError('Installer diagnostic listener remains active after installation')
        self.installed_checks_complete = True

    def collect_private_logs(self):
        for route in ('installation-logs', 'boot-logs'):
            try:
                data = self.diagnostics.request('/v1/' + route)
                (self.directory / (route + '.private.json')).write_text(json.dumps(data))
            except (OSError, RuntimeError, urllib.error.URLError):
                pass

    def stop(self):
        if self.process and self.process.poll() is None:
            try:
                self.qmp.command('quit')
            except (OSError, RuntimeError):
                self.process.terminate()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=10)
        if self.log:
            self.log.close()

    def preservation(self):
        self.check(sha(self.directory / 'data.raw') == self.data_sha, 'Non-target disk is byte-for-byte unchanged')
        self.check(sha(self.directory / 'config.iso') == self.config_sha, 'Configuration media is byte-for-byte unchanged')
        if self.scenario != 'install' and hasattr(self, 'target_bytes'):
            self.check(untouched_sparse(self.directory / 'target.raw', self.target_bytes),
                       'Unapproved target disk remains entirely sparse and unchanged')


def verify_input(iso, descriptor, expected):
    require(iso.is_file(), 'ISO must be a regular file')
    require(',' not in str(iso), 'QEMU paths must not contain commas')
    if descriptor:
        spec = importlib.util.spec_from_file_location('verify_release', ROOT / 'eng/verify-release.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        release = module.verify(descriptor, ROOT / 'os/bootc/application-update-key.pem', iso=iso)
        return {'sha256': release['installer']['iso']['sha256'], 'bytes': iso.stat().st_size,
                'verification': 'Signed release descriptor',
                'releaseCommit': release['installer'].get('commit'), 'version': release.get('version')}
    require(bool(re.fullmatch('[a-f0-9]{64}', expected or '')), 'Provide a lowercase SHA-256 from the trusted candidate receipt')
    require(sha(iso) == expected, 'ISO does not match the expected SHA-256')
    return {'sha256': expected, 'bytes': iso.stat().st_size, 'verification': 'Expected candidate SHA-256'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('iso', type=pathlib.Path)
    verification = parser.add_mutually_exclusive_group(required=True)
    verification.add_argument('--descriptor', type=pathlib.Path)
    verification.add_argument('--sha256')
    parser.add_argument('--scenario', choices=('all', 'smoke', *SCENARIOS), default='all')
    parser.add_argument('--run-id', default=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S-') + secrets.token_hex(3))
    parser.add_argument('--qemu-root', type=pathlib.Path, default=os.environ.get('XUR_QEMU_ROOT'))
    parser.add_argument('--memory-mib', type=int, default=8192)
    parser.add_argument('--cpus', type=int, default=4)
    parser.add_argument('--refresh-kvm-access', action='store_true',
                        help='Use passwordless sudo/setfacl to refresh your transient /dev/kvm ACL before each VM')
    parser.add_argument('--boot-timeout', type=int, default=300)
    parser.add_argument('--install-timeout', type=int, default=1800)
    args = parser.parse_args()
    require(bool(re.fullmatch('[a-z0-9][a-z0-9-]{0,47}', args.run_id)), 'Invalid run ID')
    require(4096 <= args.memory_mib <= 32768 and 1 <= args.cpus <= 16, 'Invalid VM resources')
    require(1 <= args.boot_timeout <= 1800 and 1 <= args.install_timeout <= 7200, 'Invalid timeout')
    os.umask(0o077)
    iso = args.iso.resolve()
    directory = ROOT / '.build/vm-tests' / args.run_id
    evidence = ROOT / '.build/evidence/vm-tests' / args.run_id
    require(not directory.exists() and not evidence.exists(), 'Run ID already exists; use a new ID')
    directory.mkdir(parents=True)
    evidence.mkdir(parents=True)
    scenarios = list(SCENARIOS) if args.scenario == 'all' else list(SCENARIOS[:-1]) if args.scenario == 'smoke' else [args.scenario]
    report = {'schema': 1, 'suite': 'InstallerVM', 'result': 'Running', 'iso': {'verification': 'Pending'},
              'testHarnessCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'testHarnessSha256': sha(pathlib.Path(__file__)),
              'startedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'environment': {'firmware': 'UEFI OVMF', 'accelerator': 'KVM', 'memoryMiB': args.memory_mib,
                              'vcpus': args.cpus, 'machine': 'Q35', 'hpet': False, 'rtc': 'UTC, host clock',
                              'physicalDisksAttached': False, 'isoModified': False},
              'requestedScenarios': scenarios, 'fullQualification': False,
              'scenarios': [], 'notRun': ['Physical GPU/USB/audio', 'Application upgrade/rollback', 'Real model inference', 'Secure Boot', 'PXE boot'] +
                  [s + ' scenario (not selected)' for s in SCENARIOS if s not in scenarios]}
    receipt = evidence / 'receipt.json'
    def save():
        temporary = receipt.with_suffix('.json.tmp')
        temporary.write_text(json.dumps(report, indent=2) + '\n')
        temporary.replace(receipt)
    save()
    def interrupted(signum, frame):
        raise InterruptedError('VM test interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    result = 0
    try:
        artifact = verify_input(iso, args.descriptor, args.sha256)
        report['iso'] = artifact
        toolchain = tools(pathlib.Path(args.qemu_root) if args.qemu_root else None)
        report['environment'].update(tool_identity(toolchain))
        for scenario in scenarios:
            ensure_kvm(args.refresh_kvm_access)
            vm = Vm(directory / scenario, iso, toolchain, args.memory_mib, args.cpus, scenario)
            case = {'name': scenario, 'result': 'Running', 'checks': vm.checks}
            report['scenarios'].append(case)
            save()
            started = time.monotonic()
            try:
                vm.prepare()
                vm.start()
                screen = vm.live(args.boot_timeout)
                case['bundle'] = vm.bundle
                if scenario == 'install':
                    vm.install(screen, args.install_timeout)
                    case['osDeployment'] = vm.os_deployment
                else:
                    if scenario == 'cancel':
                        vm.cancel(screen)
                    vm.check(vm.read_status()['installer'].get('operation') is None, 'No installation started')
                case['result'] = 'Passed'
            except BaseException:
                case['result'] = 'Failed'
                vm.collect_private_logs()
                raise
            finally:
                vm.stop()
                try:
                    if hasattr(vm, 'data_sha') and hasattr(vm, 'config_sha'):
                        vm.preservation()
                except Exception:
                    case['result'] = 'Failed'
                    raise
                finally:
                    if hasattr(vm, 'installation'):
                        case['installation'] = vm.installation
                    if scenario == 'install' and not getattr(vm, 'installed_checks_complete', False):
                        report['notRun'].append('Remaining installed boot/account/profile/reboot checks (install scenario incomplete)')
                    case['seconds'] = round(time.monotonic() - started, 2)
                    save()
        report['result'] = 'Passed'
        report['fullQualification'] = args.scenario == 'all'
    except (Exception, KeyboardInterrupt) as error:
        report['result'] = 'Failed'
        # Do not include HTTP bodies, credentials, or arbitrary exception strings.
        report['failure'] = {'type': type(error).__name__, 'privateLogs': '.build/vm-tests/' + args.run_id}
        if isinstance(error, RuntimeError):
            report['failure']['message'] = str(error)
            print(str(error), file=sys.stderr)
        print(f'VM qualification failed ({type(error).__name__}). Inspect private logs in {directory}', file=sys.stderr)
        result = 1
    finally:
        if 'sha256' in report['iso']:
            try:
                report['iso']['postRunSha256'] = sha(iso)
                report['iso']['unchanged'] = report['iso']['postRunSha256'] == report['iso']['sha256']
                require(report['iso']['unchanged'], 'ISO changed during qualification')
            except Exception as error:
                report['result'] = 'Failed'
                report['fullQualification'] = False
                report['iso']['unchanged'] = False
                report.setdefault('failure', {'type': type(error).__name__,
                    'message': 'ISO identity could not be preserved', 'privateLogs': '.build/vm-tests/' + args.run_id})
                result = 1
        report['finishedAt'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        save()
        print('VM qualification receipt: ' + str(receipt), flush=True)
    return result


if __name__ == '__main__':
    sys.exit(main())
