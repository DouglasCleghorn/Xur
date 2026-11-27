#!/usr/bin/env python3
import fcntl
import json
import os
import pathlib
import runpy
import tempfile
import time
from unittest.mock import patch

module = runpy.run_path('os/bootc/storage-maintenance')
base = pathlib.Path('.build/evidence/storage-maintenance')
base.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(dir=base) as tmp:
    root = pathlib.Path(tmp).resolve()
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
    for _ in range(2):
        module['clean'](root, now)
    assert all(p.exists() for p in (current, previous, fresh, unrelated, alias, outside))
    assert not any(p.exists() for p in (unused, download, legacy, partial))
    # A corrupt rollback selection fails closed for bundles and still allows
    # the independent Podman cleanup to run using the dangling-only default.
    (root / 'previous.json').write_text('{}')
    preserved = directory(releases / ('f' * 64))
    with patch('subprocess.run') as podman:
        assert module['maintain'](root) == 1
        podman.assert_called_once_with(['/usr/bin/podman', 'image', 'prune', '--force', '--filter', 'until=168h'])
        assert preserved.exists()
    units = directory(root / 'units', False)
    source = pathlib.Path('os/bootc/systemd')
    receipt = root / 'configured'
    with patch('subprocess.run') as systemctl:
        module['configure'](source, units, receipt)
        assert receipt.exists()
        assert [c.args[0] for c in systemctl.call_args_list] == [
            ['systemctl', 'daemon-reload'],
            ['systemctl', 'enable', '--now', 'xur-storage-maintenance.timer']]
        systemctl.reset_mock()
        module['configure'](source, units, receipt)
        systemctl.assert_not_called()
print('Storage maintenance configuration, retention and locking checks passed.')
