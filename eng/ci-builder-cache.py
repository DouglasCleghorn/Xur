#!/usr/bin/env python3
"""Prepare a sanitized Fedora toolchain template, before any project build."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import subprocess
import time
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
INPUTS = ('eng/toolchain-lock.json', 'eng/prepare-fedora-builder.sh',
          'eng/start-builder.py', 'eng/builder_ready.py', 'eng/ci-builder-cache.py')


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def key():
    inputs = {name: sha(ROOT / name) for name in INPUTS}
    # Refresh RPMs periodically even if the selected source toolchain is unchanged.
    week = datetime.datetime.now(datetime.timezone.utc).strftime('%G-%V')
    return week + '-' + hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()


def verify(directory):
    receipt = json.loads((directory / 'template.json').read_text())
    if receipt.get('schema') != 1 or receipt.get('key') != key():
        raise ValueError('Fedora toolchain template inputs or refresh week changed')
    if sha(directory / 'fedora-toolchain.qcow2') != receipt.get('sha256'):
        raise ValueError('Fedora toolchain template checksum mismatch')
    return directory / 'fedora-toolchain.qcow2'


def prepare(directory):
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise ValueError('Toolchain snapshot preparation requires a disposable hosted runner')
    root = pathlib.Path(os.environ['XUR_BUILD_ROOT'])
    cache = pathlib.Path(os.environ['XUR_BUILD_CACHE'])
    if not root.is_relative_to(pathlib.Path(os.environ['RUNNER_TEMP'])):
        raise ValueError('Builder must be beneath RUNNER_TEMP')
    if (directory / 'template.json').exists():
        verify(directory)
        print('Fedora toolchain: verified prepared template')
        return
    vm = root / 'vm'
    if (vm / 'builder.qcow2').exists():
        raise ValueError('Refusing to snapshot an existing build VM')
    lock = json.loads((ROOT / 'eng/toolchain-lock.json').read_text())
    cache.mkdir(parents=True, exist_ok=True)
    base = cache / 'fedora-44.qcow2'
    if not base.exists():
        urllib.request.urlretrieve(lock['builderCloudImage']['url'], base)
    if sha(base) != lock['builderCloudImage']['sha256']:
        raise ValueError('Fedora Cloud checksum mismatch')
    subprocess.run(['python3', str(ROOT / 'eng/start-builder.py')], check=True)
    ssh = ['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=5', '-o', 'StrictHostKeyChecking=accept-new',
           '-o', f'UserKnownHostsFile={vm}/known_hosts', '-i', str(vm / 'builder_ed25519'),
           '-p', '22220', 'builder@127.0.0.1']
    for _ in range(90):
        if subprocess.run(ssh + ['true'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
            break
        time.sleep(2)
    else:
        raise ValueError('Fedora template SSH did not become ready')
    from builder_ready import wait_for_cloud_init
    wait_for_cloud_init(ssh)
    # Send public toolchain inputs only; this VM has never received source or signing credentials.
    for name in ('prepare-fedora-builder.sh', 'toolchain-lock.json'):
        subprocess.run(ssh + [f'cat > {name}'], input=(ROOT / 'eng' / name).read_bytes(), check=True)
    subprocess.run(ssh + ['sudo bash prepare-fedora-builder.sh toolchain-lock.json'], check=True)
    pid = int((vm / 'qemu.pid').read_text())
    # Use this same SSH session for cleanup and poweroff: cleanup removes its login key.
    result = subprocess.run(ssh + ["sudo bash -c 'set -eu; dnf clean all; cloud-init clean --logs --machine-id; "
                         "rm -rf /home/builder/.ssh /root/.ssh /var/log/journal; "
                         "rm -f /etc/ssh/ssh_host_* /home/builder/prepare-fedora-builder.sh /home/builder/toolchain-lock.json; "
                         "find /var/log -type f -exec truncate -s 0 {} +; "
                         "sync; fstrim --all; sync; printf XUR_TEMPLATE_SANITIZED; systemctl poweroff'"], capture_output=True)
    # Shut down cleanly before converting the disk; never snapshot an active filesystem.
    if result.returncode not in (0, 255) or b'XUR_TEMPLATE_SANITIZED' not in result.stdout:
        raise ValueError('Could not shut down template VM')
    for _ in range(90):
        state = pathlib.Path(f'/proc/{pid}/stat')
        if not state.exists() or state.read_text().split()[2] == 'Z':
            break
        time.sleep(1)
    else:
        raise ValueError('Fedora template VM did not shut down')
    directory.mkdir(parents=True, exist_ok=True)
    template = directory / 'fedora-toolchain.qcow2'
    subprocess.run([str(root / 'qemu/usr/bin/qemu-img'), 'convert', '-c', '-O', 'qcow2',
                    str(vm / 'builder.qcow2'), str(template)], check=True)
    (directory / 'template.json').write_text(json.dumps({'schema': 1, 'key': key(), 'sha256': sha(template)}) + '\n')
    # The actual build uses a fresh overlay, SSH identity and cloud-init seed.
    import shutil
    shutil.rmtree(vm)
    print('Fedora toolchain: prepared sanitized template')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['key', 'prepare', 'verify'])
    parser.add_argument('--directory', type=pathlib.Path, default=ROOT / '.build/fedora-toolchain')
    args = parser.parse_args()
    if args.action == 'key':
        print(key())
    elif args.action == 'verify':
        verify(args.directory)
    else:
        prepare(args.directory)
