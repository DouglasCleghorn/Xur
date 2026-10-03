"""Share equal Steam game extents through FIDEDUPERANGE, preserving private inodes.

Only account-owned files in steamapps/common qualify. The kernel compares bytes
under its own locks; this helper never copies, truncates, links or replaces files.
"""
import ctypes
import errno
import fcntl
import json
import os
import platform
import re
import sqlite3
import stat
import struct
import sys
import time
from contextlib import ExitStack

# Linux UAPI: include/uapi/linux/fs.h and include/uapi/linux/openat2.h.
FIDEDUPERANGE = 0xC0189436
HEADER = struct.Struct('=QQHHI')
INFO = struct.Struct('=qQQiI')
CHUNK = 1024 * 1024
LIBRARIES = ('.local/share/Steam', '.steam/steam', '.steam/root',
             '.var/app/com.valvesoftware.Steam/.local/share/Steam')
libc = ctypes.CDLL(None, use_errno=True)


def open_beneath(parent, path, flags=os.O_RDONLY, cross_mounts=False):
    if platform.machine() not in ('x86_64', 'aarch64'):
        raise OSError(errno.ENOSYS, 'Unsupported openat2 architecture')
    if not path or path.startswith('/') or any(p in ('', '.', '..') for p in path.split('/')):
        raise ValueError('Invalid relative path')
    # BENEATH | NO_SYMLINKS | NO_XDEV (including bind mounts). No unsafe fallback.
    resolve = 0x08 | 0x04 | (0 if cross_mounts else 0x01)
    how = struct.pack('=QQQ', flags | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK, 0, resolve)
    fd = libc.syscall(ctypes.c_long(437), ctypes.c_int(parent), ctypes.c_char_p(os.fsencode(path)),
                      ctypes.c_char_p(how), ctypes.c_size_t(len(how)))
    if fd < 0:
        raise OSError(ctypes.get_errno(), 'Safe open failed')
    return fd


def filesystem(fd):
    data = ctypes.create_string_buffer(256)
    if libc.fstatfs(fd, data) != 0:
        raise OSError(ctypes.get_errno(), 'Filesystem probe failed')
    return ctypes.c_long.from_buffer(data).value & 0xffffffff


def fingerprint(st):
    return [st.st_dev, st.st_ino, st.st_size, st.st_mtime_ns, st.st_ctime_ns]


def eligible(st, uid, minimum_age):
    return (stat.S_ISREG(st.st_mode) and st.st_uid == uid and st.st_nlink == 1
            and st.st_size >= 4096
            and max(st.st_mtime, st.st_ctime) <= time.time() - minimum_age)


def share_range(source, destination, offset, length):
    data = bytearray(HEADER.pack(offset, length, 1, 0, 0)
                     + INFO.pack(destination, offset, 0, 0, 0))
    fcntl.ioctl(source, FIDEDUPERANGE, data, True)
    _, _, shared, status, _ = INFO.unpack_from(data, HEADER.size)
    if status < 0:
        raise OSError(-status, 'Extent sharing failed')
    return shared, status


class Sharing:
    def __init__(self, database, minimum_age=120, seconds=600, max_entries=250000):
        self.database = database
        self.minimum_age = minimum_age
        self.deadline = time.monotonic() + seconds
        self.max_entries = max_entries
        self.entries = 0
        self.report = dict(libraries=0, unsupportedLibraries=0, files=0, pairs=0,
                           cachedPairs=0, sharedBytes=0, differentRanges=0,
                           errors=0, limited=False)

    def within_budget(self):
        if time.monotonic() >= self.deadline:
            self.report['limited'] = True
            return False
        return True

    def discover(self, accounts, stack):
        roots, seen = [], set()
        slash = os.open('/', os.O_RDONLY | os.O_DIRECTORY)
        stack.callback(os.close, slash)
        for account in accounts:
            uid, home = account['uid'], account['home']
            if uid < 1000 or uid >= 65534 or not home.startswith('/'):
                continue
            try:
                home_fd = open_beneath(slash, home[1:], os.O_RDONLY | os.O_DIRECTORY, True)
            except OSError as e:
                if e.errno == errno.ENOSYS:
                    raise
                continue
            stack.callback(os.close, home_fd)
            if os.fstat(home_fd).st_uid != uid:
                continue
            paths = set()
            for base in LIBRARIES:
                paths.add(home + '/' + base)
                try:
                    config_fd = open_beneath(home_fd, base + '/steamapps/libraryfolders.vdf')
                    with os.fdopen(config_fd, 'rb') as config:
                        st = os.fstat(config.fileno())
                        if st.st_uid != uid or not stat.S_ISREG(st.st_mode) or st.st_size > 65536:
                            continue
                        content = config.read(65537).decode('utf-8')
                    # VDF strings; only absolute Unix paths without escapes qualify.
                    paths.update(re.findall(r'"path"\s*"(/[^"\\\r\n\x00]*)"', content))
                except (OSError, UnicodeError):
                    pass
            for path in sorted(paths):
                try:
                    fd = open_beneath(slash, path.lstrip('/') + '/steamapps/common',
                                      os.O_RDONLY | os.O_DIRECTORY, True)
                except (OSError, ValueError):
                    continue
                st = os.fstat(fd)
                if st.st_uid != uid or (st.st_dev, st.st_ino) in seen:
                    os.close(fd)
                    continue
                seen.add((st.st_dev, st.st_ino))
                stack.callback(os.close, fd)
                self.report['libraries'] += 1
                if filesystem(fd) not in (0x9123683E, 0x58465342):  # Btrfs, XFS
                    self.report['unsupportedLibraries'] += 1
                    continue
                roots.append((fd, uid, path))
        return roots

    def scan(self, root, uid, relative=''):
        if not self.within_budget() or relative.count('/') >= 64:
            self.report['limited'] = True
            return
        fd = os.dup(root) if not relative else open_beneath(root, relative, os.O_RDONLY | os.O_DIRECTORY)
        try:
            with os.scandir(fd) as entries:
                for entry in entries:
                    if not self.within_budget() or self.entries >= self.max_entries:
                        self.report['limited'] = True
                        return
                    self.entries += 1
                    path = relative + '/' + entry.name if relative else entry.name
                    try:
                        st = entry.stat(follow_symlinks=False)
                        if st.st_uid != uid:
                            continue
                        if stat.S_ISDIR(st.st_mode):
                            yield from self.scan(root, uid, path)
                        elif eligible(st, uid, self.minimum_age):
                            yield path, st
                    except OSError:
                        self.report['errors'] += 1
        finally:
            os.close(fd)

    def pair(self, db, source, destination):
        root_a, uid_a, library_a, path, expected_a = source
        root_b, uid_b, library_b, _, expected_b = destination
        key = json.dumps([uid_a, library_a, uid_b, library_b, path])
        metadata = json.dumps([fingerprint(expected_a), fingerprint(expected_b)])
        row = db.execute('SELECT metadata, cursor FROM pairs WHERE key=?', (key,)).fetchone()
        if row and row[0] == metadata and row[1] >= expected_a.st_size:
            self.report['cachedPairs'] += 1
            return
        with ExitStack() as stack:
            a = open_beneath(root_a, path)
            stack.callback(os.close, a)
            b = open_beneath(root_b, path, os.O_RDWR)
            stack.callback(os.close, b)
            before_a, before_b = os.fstat(a), os.fstat(b)
            if (not eligible(before_a, uid_a, self.minimum_age)
                    or not eligible(before_b, uid_b, self.minimum_age)
                    or fingerprint(before_a) != fingerprint(expected_a)
                    or fingerprint(before_b) != fingerprint(expected_b)):
                return
            self.report['pairs'] += 1
            offset = row[1] if row and row[0] == metadata else 0
            def cache(cursor):
                after_a, after_b = os.fstat(a), os.fstat(b)
                # Deduplication may alter ctime. Size/mtime/inode must stay stable.
                if (fingerprint(before_a)[:4] != fingerprint(after_a)[:4]
                        or fingerprint(before_b)[:4] != fingerprint(after_b)[:4]):
                    return False
                metadata = json.dumps([fingerprint(after_a), fingerprint(after_b)])
                db.execute('INSERT OR REPLACE INTO pairs VALUES (?, ?, ?, ?)',
                           (key, metadata, int(time.time()), cursor))
                db.commit()
                return True
            checkpoint = offset
            while offset < before_a.st_size:
                if not self.within_budget():
                    cache(offset)
                    return
                length = min(CHUNK, before_a.st_size - offset)
                shared, status = share_range(a, b, offset, length)
                if status == 1:
                    self.report['differentRanges'] += 1
                    offset += length
                elif status == 0 and 0 < shared <= length:
                    self.report['sharedBytes'] += shared
                    offset += shared  # Filesystems may cap the amount per ioctl.
                else:
                    raise OSError(errno.EIO, 'Unexpected deduplication result')
                if offset - checkpoint >= 16 * CHUNK:
                    if not cache(offset):
                        return
                    checkpoint = offset
            cache(offset)

    def run(self, accounts):
        with ExitStack() as stack:
            roots = self.discover(accounts, stack)
            db = sqlite3.connect(self.database)
            stack.callback(db.close)
            db.execute('CREATE TABLE IF NOT EXISTS pairs (key TEXT PRIMARY KEY, metadata TEXT, seen INTEGER, cursor INTEGER)')
            db.execute('DELETE FROM pairs WHERE seen < ?', (int(time.time()) - 30 * 86400,))
            db.commit()
            groups = {}
            for root, uid, library in roots:
                for path, st in self.scan(root, uid):
                    self.report['files'] += 1
                    groups.setdefault((st.st_dev, path, st.st_size), []).append((root, uid, library, path, st))
            unsupported = set()
            for group in groups.values():
                if len(group) < 2:
                    continue
                for index, destination in enumerate(group[1:], 1):
                    if not self.within_budget():
                        return self.report
                    if destination[0] in unsupported:
                        continue
                    for source in group[:index]:
                        if not self.within_budget():
                            return self.report
                        if destination[1] == source[1] or source[0] in unsupported:
                            continue
                        try:
                            self.pair(db, source, destination)
                        except OSError as e:
                            self.report['errors'] += 1
                            if e.errno in (errno.EOPNOTSUPP, errno.ENOTTY):
                                unsupported.add(destination[0])
                                self.report['unsupportedLibraries'] += 1
                                break
            return self.report


if __name__ == '__main__':
    os.umask(0o077)
    try:
        print(json.dumps(Sharing(sys.argv[1]).run(json.loads(sys.argv[2]))))
    except (OSError, ValueError, sqlite3.Error):
        # Avoid exposing user-controlled paths or Steam configuration in logs.
        print(json.dumps(dict(error='Steam block sharing could not complete; check storage and kernel support.')))
        sys.exit(1)
