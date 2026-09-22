#!/usr/bin/env python3
"""Choose the next monthly release number from remote tags, without creating it."""
import argparse
import datetime
import re
import subprocess


def next_version(channel, month, refs):
    if channel not in ('stable', 'nightly'):
        raise ValueError('Expected stable or nightly channel')
    if not re.fullmatch(r'[0-9]{2}\.(?:0[1-9]|1[0-2])', month):
        raise ValueError('Expected month as YY.MM')
    prefix = 'v' if channel == 'stable' else 'nightly-'
    pattern = re.compile(r'refs/tags/' + re.escape(prefix + month + '.') + r'([0-9]+)')
    numbers = []
    for line in refs.splitlines():
        fields = line.split()
        match = pattern.fullmatch(fields[-1]) if fields else None
        if match:
            numbers.append(int(match[1]))
    number = max(numbers, default=0) + 1
    suffix = str(number) if channel == 'stable' else f'{number:03d}'
    return f'{month}.{suffix}'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--channel', required=True, choices=['stable', 'nightly'])
    parser.add_argument('--month', default=datetime.datetime.now(datetime.UTC).strftime('%y.%m'))
    parser.add_argument('--remote', default='origin')
    args = parser.parse_args()
    # Validate before constructing the remote ref filter. Never fall back to .1
    # if GitHub cannot be reached: doing so could reuse an existing version.
    next_version(args.channel, args.month, '')
    prefix = 'v' if args.channel == 'stable' else 'nightly-'
    refs = subprocess.check_output(
        ['git', 'ls-remote', '--tags', '--refs', '--', args.remote,
         f'refs/tags/{prefix}{args.month}.*'], text=True, timeout=60)
    print(next_version(args.channel, args.month, refs))


if __name__ == '__main__':
    main()
