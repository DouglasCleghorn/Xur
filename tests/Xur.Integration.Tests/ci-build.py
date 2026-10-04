#!/usr/bin/env python3
"""Exercise build reuse, ISO gating and app-only publication without GitHub writes."""
import importlib.util
import json
import pathlib
import os
import runpy
import shutil
import subprocess
import tempfile
import sys
from unittest.mock import patch
from types import SimpleNamespace

repo = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(repo / "eng"))


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), repo / 'eng' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def reject(action):
    try:
        action()
    except (ValueError, subprocess.CalledProcessError):
        return
    raise AssertionError('Invalid input accepted')


gate = load('ci-iso-gate')
context = load('ci-context')
cache = load('ci-builder-cache')
release = load('ci-release')
with tempfile.TemporaryDirectory(dir=repo / '.build') as temporary:
    root = pathlib.Path(temporary)
    app, newest = 'a' * 40, 'b' * 40
    assert not gate.decision('push')
    assert not gate.decision('push', True)
    assert not gate.decision('pull_request', True)
    assert not gate.decision('workflow_dispatch')
    assert gate.decision('workflow_dispatch', True)
    for event, requested, expected in [('push', False, False), ('push', True, False),
                                        ('workflow_dispatch', False, False), ('workflow_dispatch', True, True)]:
        output = root / 'gate-output'; output.write_text('')
        with patch.dict(os.environ, {'GITHUB_EVENT_NAME': event, 'GITHUB_OUTPUT': str(output)}):
            subprocess.run(['python3', str(repo / 'eng/ci-iso-gate.py'), *(['--requested'] if requested else [])], check=True)
        assert output.read_text() == 'build=' + str(expected).lower() + '\n'

    fixture = root / 'context'; context.ROOT = fixture
    (fixture / 'eng').mkdir(parents=True); (fixture / 'dist').mkdir()
    shutil.copy2(repo / 'eng/context-receipt.py', fixture / 'eng/context-receipt.py')
    publication = fixture / '.build/context'
    for name in ('rootfs/usr/lib/xur/control/Xur.Control.dll', 'rootfs/usr/lib/xur/agent/Xur.Agent.dll',
                 'rootfs/usr/lib/xur/gateway/Xur.Gateway.dll', 'installer-rootfs/usr/share/xur/install-template.ks'):
        path = publication / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text(name)
    subprocess.run(['python3', str(fixture / 'eng/context-receipt.py'), 'create', str(publication)], check=True)
    archive = fixture / 'dist/xur-app-x86_64.tar.gz'; archive.write_bytes(b'tested application fixture')
    tested = {'commit': app, 'channel': 'nightly', 'checks': 'eng/test-fast.sh passed',
              'sha256': context.sha(archive), 'contextSha256': context.sha(publication / 'publish-receipt.json')}
    (fixture / 'dist/build.json').write_text(json.dumps(tested))
    transfer = fixture / '.build/ci-context'
    context.transfer('export', transfer, app, 'nightly')
    shutil.rmtree(publication)
    reject(lambda: context.transfer('import', transfer, app, 'stable'))
    reject(lambda: context.transfer('import', transfer, newest, 'nightly'))
    context.transfer('import', transfer, app, 'nightly')
    reject(lambda: context.transfer('import', transfer, app, 'nightly'))
    shutil.rmtree(publication)
    original = (transfer / 'context.tar').read_bytes()
    (transfer / 'context.tar').write_bytes(original + b'changed')
    reject(lambda: context.transfer('import', transfer, app, 'nightly'))
    (transfer / 'context.tar').write_bytes(original)
    context.transfer('import', transfer, app, 'nightly')
    tested['contextSha256'] = 'f' * 64
    (fixture / 'dist/build.json').write_text(json.dumps(tested))
    reject(lambda: context.transfer('export', transfer, app, 'nightly'))

    template = root / 'template'; template.mkdir()
    disk = template / 'fedora-toolchain.qcow2'; disk.write_bytes(b'template fixture')
    receipt = template / 'template.json'
    receipt.write_text(json.dumps({'schema': 1, 'key': cache.key(), 'sha256': cache.sha(disk)}))
    assert cache.verify(template) == disk
    disk.write_bytes(b'tampered'); reject(lambda: cache.verify(template))
    disk.write_bytes(b'template fixture')
    receipt.write_text(json.dumps({'schema': 1, 'key': 'old toolchain', 'sha256': cache.sha(disk)}))
    reject(lambda: cache.verify(template))
    receipt.write_text(json.dumps({'schema': 1, 'key': cache.key(), 'sha256': cache.sha(disk)}))
    builder = root / 'builder'; vm = builder / 'vm'; vm.mkdir(parents=True)
    (vm / 'builder_ed25519').write_text('fixture key')
    (vm / 'builder_ed25519.pub').write_text('ssh-ed25519 fixture')
    (vm / 'OVMF_VARS.fd').write_bytes(b'fixture firmware')
    commands = []
    with patch.dict(os.environ, {'XUR_BUILD_ROOT': str(builder), 'XUR_BUILD_CACHE': str(root / 'absent-base-cache'),
                                 'XUR_BUILDER_TEMPLATE': str(template)}), \
            patch.object(subprocess, 'run', side_effect=lambda args, **kwargs: commands.append(args)):
        runpy.run_path(str(repo / 'eng/start-builder.py'), run_name='__main__')
    seed = (vm / 'seed/user-data').read_text()
    assert 'packages:' not in seed and 'ssh-ed25519 fixture' in seed
    overlay = next(command for command in commands if command[0].endswith('/qemu-img'))
    assert overlay[overlay.index('-b') + 1] == str(disk.resolve())
    qemu = next(command for command in commands if command[0].endswith('/qemu-system-x86_64'))
    assert any('discard=unmap' in argument for argument in qemu)

    # A cold template receives only public toolchain inputs, sanitizes and shuts
    # down before disk conversion, and removes its first-boot identity afterward.
    cold = root / 'cold'; source = cold / 'source'; cache.ROOT = source
    for name in cache.INPUTS:
        target = source / name; target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(repo / name, target)
    build_root = cold / 'builder'; download = cold / 'downloads'; download.mkdir(parents=True)
    cloud = download / 'fedora-44.qcow2'; cloud.write_bytes(b'cloud fixture')
    lock_path = source / 'eng/toolchain-lock.json'; lock = json.loads(lock_path.read_text())
    lock['builderCloudImage']['sha256'] = cache.sha(cloud); lock_path.write_text(json.dumps(lock))
    calls = []
    def cold_run(command, **kwargs):
        calls.append(command)
        if command[:1] == ['python3']:
            machine = build_root / 'vm'; machine.mkdir(parents=True)
            (machine / 'qemu.pid').write_text('2147483647')
            (machine / 'builder.qcow2').write_bytes(b'prepared disk fixture')
        if command[0].endswith('/qemu-img'):
            pathlib.Path(command[-1]).write_bytes(pathlib.Path(command[-2]).read_bytes())
        if command[0] == 'ssh' and '--format json' in command[-1]:
            return SimpleNamespace(returncode=0, stdout=json.dumps({'status': 'done', 'errors': []}), stderr='')
        return SimpleNamespace(returncode=0, stdout=b'XUR_TEMPLATE_SANITIZED')
    with patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
                                 'RUNNER_TEMP': str(cold), 'XUR_BUILD_ROOT': str(build_root),
                                 'XUR_BUILD_CACHE': str(download)}), patch.object(subprocess, 'run', side_effect=cold_run):
        cache.prepare(cold / 'template')
        count = len(calls)
        cache.prepare(cold / 'template')
        assert len(calls) == count  # A verified warm cache performs no provisioning.
    assert not (build_root / 'vm').exists()
    cleanup = next(index for index, command in enumerate(calls) if 'XUR_TEMPLATE_SANITIZED' in command[-1])
    conversion = next(index for index, command in enumerate(calls) if command[0].endswith('/qemu-img'))
    assert cleanup < conversion
    assert 'cloud-init clean' in calls[cleanup][-1] and 'fstrim --all' in calls[cleanup][-1]
    assert '/etc/ssh/ssh_host_*' in calls[cleanup][-1] and '/home/builder/.ssh' in calls[cleanup][-1]

    # Superseded publication is rejected before reading any artifact or signing.
    with patch.object(release, 'run', return_value=newest + '\trefs/heads/main'):
        reject(lambda: release.publish('nightly', '26.10.001', app, with_installer=False))
        reject(lambda: release.publish_installer('nightly', '26.10.001', app))

print(json.dumps({'suite': 'CiBuild', 'result': 'Passed', 'contextTamperingRejected': True,
                  'isoBuildsManualOnly': True, 'supersededPublicationRejected': True,
                  'templateTamperingRejected': True, 'published': False}))
