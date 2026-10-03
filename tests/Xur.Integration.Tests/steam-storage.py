#!/usr/bin/env python3
"""Private fixtures; --filesystem PATH exercises real Btrfs/XFS extent sharing."""
import argparse
import errno
import fcntl
import importlib.util
import json
import os
import pathlib
import shutil
import sqlite3
import struct
import sys
import tempfile
from contextlib import ExitStack
from unittest.mock import patch

sys.dont_write_bytecode = True
repo = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('steam_storage', repo/'src/Xur.Agent/SteamStorage.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
parser = argparse.ArgumentParser()
parser.add_argument('--filesystem', type=pathlib.Path)
args = parser.parse_args()
evidence = repo/'.build/evidence/steam-storage'
evidence.mkdir(parents=True, exist_ok=True)
test_uid = os.getuid() if os.getuid() >= 1000 else 1000


def own(path):
    if os.geteuid() == 0:
        for item in [path, *path.rglob('*')] if path.is_dir() else [path]:
            os.chown(item, test_uid, test_uid, follow_symlinks=False)


def account(home, uid):
    return dict(username=home.name, uid=uid, home=str(home))


def fixture(home):
    common = home/'.local/share/Steam/steamapps/common'
    (common/'Example Game').mkdir(parents=True)
    return common


with tempfile.TemporaryDirectory(dir=evidence) as temporary:
    root = pathlib.Path(temporary)
    a, b = fixture(root/'alice'), fixture(root/'bob')
    payload = b'A' * module.CHUNK + b'B' * module.CHUNK + b'end'
    relative = 'Example Game/payload.bin'
    (a/relative).write_bytes(payload)
    (b/relative).write_bytes(payload)
    (a/'Example Game/linked').symlink_to(root/'outside')
    (root/'outside').write_bytes(payload)
    os.link(root/'outside', a/'Example Game/hardlink')
    os.mkfifo(a/'Example Game/fifo')
    (root/'alice/.local/share/Steam/steamapps/compatdata').mkdir()
    (root/'alice/.local/share/Steam/steamapps/compatdata/private').write_bytes(payload)
    own(root)
    with ExitStack() as stack:
        fd_a, fd_b = os.open(a, os.O_RDONLY | os.O_DIRECTORY), os.open(b, os.O_RDONLY | os.O_DIRECTORY)
        stack.callback(os.close, fd_a)
        stack.callback(os.close, fd_b)
        worker = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0)
        paths = [p for p, _ in worker.scan(fd_a, test_uid)]
        assert paths == [relative], paths
        for unsafe in ('../outside', 'Example Game/linked', '/etc/passwd'):
            try:
                module.open_beneath(fd_a, unsafe)
                raise AssertionError('Unsafe path opened: ' + unsafe)
            except (OSError, ValueError):
                pass
        db = sqlite3.connect(root/'pairs.sqlite')
        db.execute('CREATE TABLE pairs (key TEXT PRIMARY KEY, metadata TEXT, seen INTEGER, cursor INTEGER)')
        calls = []
        def compare(source, destination, offset, length):
            calls.append((offset, length))
            if os.pread(source, length, offset) != os.pread(destination, length, offset):
                return 0, 1
            return length, 0
        def pair():
            return ((fd_a, test_uid, str(a), relative, (a/relative).stat()),
                    (fd_b, test_uid, str(b), relative, (b/relative).stat()))
        with patch.object(module, 'share_range', compare):
            worker.pair(db, *pair())
            assert calls == [(0, module.CHUNK), (module.CHUNK, module.CHUNK), (2*module.CHUNK, 3)]
            worker.pair(db, *pair())
            assert worker.report['cachedPairs'] == 1 and len(calls) == 3
            with (b/relative).open('r+b') as output:
                output.write(b'C' * module.CHUNK)
            worker.pair(db, *pair())
            assert worker.report['differentRanges'] == 1 and len(calls) == 6
            expected = pair()
            (b/relative).unlink()
            (b/relative).write_bytes(payload)
            own(b/relative)
            worker.pair(db, *expected)
            assert len(calls) == 6  # Replaced inode rejected.
            # A bounded pass checkpoints a large file and resumes on the next pass.
            resumed = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0)
            available = iter([True, False])
            with patch.object(resumed, 'within_budget', side_effect=lambda: next(available)):
                resumed.pair(db, *pair())
            checkpoint = db.execute('SELECT cursor FROM pairs').fetchone()[0]
            assert checkpoint == module.CHUNK, checkpoint
            calls.clear()
            module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).pair(db, *pair())
            assert calls == [(module.CHUNK, module.CHUNK), (2*module.CHUNK, 3)], calls
        limited = module.Sharing(str(root/'limited.sqlite'), minimum_age=0, max_entries=1)
        list(limited.scan(fd_a, test_uid))
        assert limited.report['limited']
        with patch.object(module, 'filesystem', return_value=0xEF53):
            unsupported = module.Sharing(str(root/'unsupported.sqlite'), minimum_age=0).run([account(root/'alice', test_uid)])
            assert unsupported['libraries'] == 1 and unsupported['unsupportedLibraries'] == 1
            assert unsupported['pairs'] == 0 and unsupported['files'] == 0
        # Custom library discovery, deduplicated paths, and private owners.
        external = root/'Attached Disk/SteamLibrary'
        (external/'steamapps/common').mkdir(parents=True)
        (root/'alice/.local/share/Steam/steamapps/libraryfolders.vdf').write_text(
            '"libraryfolders" { "0" { "path" "'+str(root/'alice/.local/share/Steam')+'" } '
            '"1" { "path" "'+str(external)+'" } }')
        own(external)
        own(root/'alice/.local/share/Steam/steamapps/libraryfolders.vdf')
        with patch.object(module, 'filesystem', return_value=0x9123683E), ExitStack() as discovery:
            found = module.Sharing(str(root/'discover.sqlite')).discover([account(root/'alice', test_uid)], discovery)
            assert len(found) == 2, found
        db.close()


def extents(path):
    # FIEMAP, Linux UAPI linux/fiemap.h. SYNC ensures disk extents are allocated.
    data = bytearray(struct.pack('=QQIIII', 0, 2**64-1, 1, 0, 128, 0) + bytes(56*128))
    with path.open('rb') as file:
        fcntl.ioctl(file.fileno(), 0xC020660B, data, True)
    count = struct.unpack_from('=I', data, 20)[0]
    return [struct.unpack_from('=QQQQQIIII', data, 32 + 56*i) for i in range(count)]


real = False
if args.filesystem:
    assert os.geteuid() == 0, 'Real multi-user fixture requires root to assign distinct UIDs'
    with tempfile.TemporaryDirectory(dir=args.filesystem) as temporary:
        root = pathlib.Path(temporary)
        payload = os.urandom(3*module.CHUNK + 123)
        accounts, files = [], []
        for i in range(4):
            home = root/f'user{i}'
            common = fixture(home)
            file = common/'Example Game/payload.bin'
            file.write_bytes(payload)
            (common/'Example Game/version.bin').write_bytes(bytes([i if i == 3 else 0])*module.CHUNK)
            accounts.append(account(home, 11001+i))
            files.append(file)
            for path in [home, *home.rglob('*')]:
                os.chown(path, 11001+i, 11001+i)
            home.chmod(0o700)
        before = [extents(file) for file in files]
        assert len({e[0][1] for e in before}) == 4, before
        inode_numbers = [file.stat().st_ino for file in files]
        report = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).run(accounts)
        assert report['errors'] == 0 and report['differentRanges'] > 0, report
        assert report['sharedBytes'] >= 3*len(payload), report
        assert [file.stat().st_ino for file in files] == inode_numbers
        shared = [extents(file) for file in files]
        assert len({e[0][1] for e in shared}) == 1, shared
        assert all(e[0][5] & 0x2000 for e in shared), shared
        again = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).run(accounts)
        assert again['cachedPairs'] > 0, again
        with files[1].open('r+b') as output:
            output.write(b'Z'*4096)
            output.flush()
            os.fsync(output.fileno())
        assert files[1].read_bytes() == b'Z'*4096 + payload[4096:]
        assert all(files[i].read_bytes() == payload for i in (0, 2, 3))
        assert extents(files[1])[0][1] != extents(files[0])[0][1]
        changed = module.Sharing(str(root/'pairs.sqlite'), minimum_age=0).run(accounts)
        assert changed['differentRanges'] > 0 and changed['errors'] == 0, changed
        shutil.rmtree(root/'user0')
        assert files[2].read_bytes() == payload and files[3].read_bytes() == payload
        # Bind mounts within a library must be rejected even on the same device.
        import subprocess
        mounted = files[2].parent/'mounted'
        mounted.mkdir()
        subprocess.run(['mount', '--bind', str(root/'user3'), str(mounted)], check=True)
        try:
            fd = os.open(files[2].parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                try:
                    module.open_beneath(fd, 'mounted', os.O_RDONLY | os.O_DIRECTORY)
                    raise AssertionError('Bind mount was followed')
                except OSError as error:
                    assert error.errno == errno.EXDEV, error
            finally:
                os.close(fd)
        finally:
            subprocess.run(['umount', str(mounted)], check=True)
        real = True

print(json.dumps(dict(suite='SteamStorage', result='Passed', realExtentSharing=real,
                     privateWritesAndDeletion=real, kernelRejectsDifferentBlocks=real,
                     secureTraversal=True, cacheInvalidation=True, attachedLibraries=True,
                     unsupportedStorageReported=True, boundedPasses=True)))
