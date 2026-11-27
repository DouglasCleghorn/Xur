#!/usr/bin/env python3
import fcntl
import json
import os
import pathlib
import runpy
import shutil
import sqlite3
import subprocess
import tempfile
import time
from unittest.mock import patch

module = runpy.run_path('os/bootc/storage-maintenance')
base = pathlib.Path('.build/evidence/storage-maintenance')
base.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=base) as tmp:
    root = pathlib.Path(tmp).resolve() / 'app'
    root.mkdir()
    releases = root / 'releases'
    releases.mkdir()
    now = time.time()
    old = now - 10 * 86400

    def directory(path, expired=True):
        path.mkdir()
        if expired:
            os.utime(path, (old, old))
        return path

    current, previous, unused, fresh = [directory(releases / (c * 64), c != 'd') for c in 'abcd']
    (root / 'current').symlink_to(current.relative_to(root))
    (root / 'previous.json').write_text(json.dumps({'id': previous.name}))
    download = directory(root / ('download-' + '1' * 32))
    legacy = directory(root / 'download-_abc12xy')
    partial = directory(root / ('obsolete-' + '2' * 64))
    unrelated = directory(root / 'download-unrecognized')
    outside = directory(root / 'models')
    alias = releases / ('e' * 64)
    alias.symlink_to(outside)
    with (root / 'update.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        module['clean'](root, now)
        assert unused.exists() and download.exists()
    for name in ('maintenance', 'transaction.json'):
        marker = root / name
        marker.touch()
        module['clean'](root, now)
        assert unused.exists() and download.exists()
        marker.unlink()
    remove = shutil.rmtree
    def unlocked_removal(path):
        with (root / 'update.lock').open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            remove(path)

    mounted = directory(releases / ('9' * 64))
    original_read = pathlib.Path.read_text
    def mounted_fixture(path, *args, **kwargs):
        if path == pathlib.Path('/proc/self/mountinfo'):
            return '1 2 3:4 / ' + str(mounted) + '/nested - ext4 /dev/test rw'
        return original_read(path, *args, **kwargs)
    with patch('pathlib.Path.read_text', mounted_fixture), patch('shutil.rmtree', side_effect=unlocked_removal):
        for _ in range(2):
            module['clean'](root, now)
    assert all(p.exists() for p in (current, previous, fresh, unrelated, alias, outside, mounted))
    assert not any(p.exists() for p in (unused, download, legacy, partial))
    # A corrupt rollback selection fails closed for bundles and still allows
    # the independent Podman cleanup to run using the dangling-only default.
    (root / 'previous.json').write_text('{}')
    preserved = directory(releases / ('f' * 64))
    with patch('subprocess.run') as podman:
        assert module['maintain'](root) == 1
        podman.assert_any_call(['/usr/bin/podman', 'image', 'prune', '--force', '--filter', 'until=168h'], check=True)
        assert preserved.exists()
    selected = directory(root.parent / 'catalog-selected', False)
    identity = 'a' * 64
    digest = 'example.org/engine@sha256:' + 'b' * 64
    (selected / 'recipe.json').write_text(json.dumps({'image': 'sha256:' + identity}))
    with sqlite3.connect(root.parent / 'profiles.db') as db:
        db.execute('CREATE TABLE documents(kind TEXT, json TEXT)')
        db.execute("INSERT INTO documents VALUES ('profile', ?)", (json.dumps({'Workloads': [{'Recipe': {'Image': digest}}]}),))
    assert module['image_references'](root) == {digest, 'sha256:' + identity}
    def command(argv, **kwargs):
        output = identity if 'inspect' in argv else ''
        return subprocess.CompletedProcess(argv, 0, stdout=output)
    with patch('subprocess.run', side_effect=command) as podman:
        module['prune_images'](root)
        calls = [c.args[0] for c in podman.call_args_list]
        assert calls.index(['/usr/bin/podman', 'tag', identity, 'localhost/xur-retained:' + identity]) < len(calls) - 1
        assert calls[-1][1:3] == ['image', 'prune']
    (selected / 'recipe.json').write_text('{')
    with patch('subprocess.run') as podman:
        assert module['maintain'](root) == 1
        podman.assert_not_called()
    units = directory(root / 'units', False)
    source = pathlib.Path('os/bootc/systemd')
    receipt = root / 'configured'
    with patch('subprocess.run') as systemctl:
        module['configure'](source, units, receipt)
        assert receipt.exists()
        assert [c.args[0] for c in systemctl.call_args_list] == [
            ['restorecon', str(units / 'xur-storage-maintenance.service'), str(units / 'xur-storage-maintenance.timer')],
            ['systemctl', 'daemon-reload'],
            ['systemctl', 'enable', '--now', 'xur-storage-maintenance.timer']]
        systemctl.reset_mock()
        module['configure'](source, units, receipt)
        systemctl.assert_not_called()
        service = units / 'xur-storage-maintenance.service'
        service.write_text('old managed unit')
        module['configure'](source, units, receipt)
        assert all('enable' not in c.args[0] for c in systemctl.call_args_list)
        receipt.unlink()
        service.unlink()
        service.symlink_to('/dev/null')
        systemctl.reset_mock()
        module['configure'](source, units, receipt)
        assert service.is_symlink() and all('enable' not in c.args[0] for c in systemctl.call_args_list)
print('Storage maintenance configuration, retention and locking checks passed.')
