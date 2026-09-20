"""Bounded, read-only allocated-space scan. No file contents are opened."""
import json
import os
import stat
import sys
import time


def scan(root, relative, seconds=15, limit=250000):
    if not root.startswith('/') or len(relative) > 4096 or relative.startswith('/') or any(p in ('.', '..') for p in relative.split('/')) or '\0' in relative:
        raise ValueError('Invalid folder path')
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
    fd = os.open('/', flags)
    try:
        for part in (root.strip('/') + '/' + relative).split('/'):
            if part:
                child = os.open(part, flags, dir_fd=fd)
                os.close(fd)
                fd = child
        base = os.readlink('/proc/self/fd/' + str(fd))
        dev = os.fstat(fd).st_dev
        mounts = set()
        with open('/proc/self/mountinfo') as table:
            for line in table:
                target = line.split()[4]
                for a, b in [('\\040', ' '), ('\\011', '\t'), ('\\012', '\n'), ('\\134', '\\')]:
                    target = target.replace(a, b)
                if target != base and target.startswith(base.rstrip('/') + '/'):
                    mounts.add(target)
        deadline = time.monotonic() + seconds
        visited = 0
        seen = set()
        stopped = False
        errors = 0

        def measure(parent, name, path, depth=0):
            nonlocal visited, stopped, errors
            if visited >= limit or time.monotonic() >= deadline or depth >= 256:
                stopped = True
                return None, True, 'folder'
            visited += 1
            try:
                st = os.stat(name, dir_fd=parent, follow_symlinks=False)
                directory = stat.S_ISDIR(st.st_mode)
                kind = 'folder' if directory else 'link' if stat.S_ISLNK(st.st_mode) else 'file'
                if st.st_dev != dev or path in mounts:
                    return None, False, 'mount'
                identity = (st.st_dev, st.st_ino)
                if identity in seen:
                    return 0, False, kind
                seen.add(identity)
                size = st.st_blocks * 512
                partial = False
                if directory:
                    child = os.open(name, flags, dir_fd=parent)
                    try:
                        now = os.fstat(child)
                        if (now.st_dev, now.st_ino) != identity:
                            raise OSError('Folder changed during scan')
                        with os.scandir(child) as entries:
                            for entry in entries:
                                amount, incomplete, _ = measure(child, entry.name, path + '/' + entry.name, depth + 1)
                                size += amount or 0
                                partial |= incomplete
                                if stopped:
                                    break
                    finally:
                        os.close(child)
                return size, partial, kind
            except OSError:
                errors += 1
                return None, True, 'unavailable'

        rows = []
        truncated = False
        with os.scandir(fd) as entries:
            # List only direct children first so the UI remains useful if a large child exhausts the budget.
            for entry in entries:
                if len(rows) >= 2000:
                    truncated = True
                    break
                st = entry.stat(follow_symlinks=False)
                rows.append({'name': entry.name, 'kind': 'folder' if stat.S_ISDIR(st.st_mode) else 'link' if stat.S_ISLNK(st.st_mode) else 'file', 'bytes': None, 'partial': True})
        rows.sort(key=lambda r: (r['kind'] == 'folder', r['name']))
        for row in rows:
            if stopped:
                break
            amount, partial, kind = measure(fd, row['name'], base.rstrip('/') + '/' + row['name'])
            row.update(bytes=amount, partial=partial, kind=kind)
        rows.sort(key=lambda r: (r['bytes'] is None, -(r['bytes'] or 0), r['name']))
        return {'entries': rows, 'bytes': sum(r['bytes'] or 0 for r in rows), 'partial': stopped or truncated or errors > 0, 'errors': errors, 'visited': visited, 'truncated': truncated}
    finally:
        os.close(fd)


if __name__ == '__main__':
    try:
        print(json.dumps(scan(sys.argv[1], sys.argv[2]), ensure_ascii=True))
    except (OSError, ValueError):
        print(json.dumps({'error': 'Folder unavailable or changed. Links are not followed.'}))
        sys.exit(1)
