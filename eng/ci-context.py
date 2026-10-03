#!/usr/bin/env python3
"""Transfer the exact tested publication to the installer job."""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import tarfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def identity(commit, channel):
    if not re.fullmatch('[a-f0-9]{40}', commit) or channel not in ('nightly', 'stable'):
        raise ValueError('Invalid tested-context identity')


def transfer(action, directory, commit, channel):
    identity(commit, channel)
    context = ROOT / '.build/context'
    archive = directory / 'context.tar'
    receipt = directory / 'context-build.json'
    if action == 'export':
        subprocess.run(['python3', str(ROOT / 'eng/context-receipt.py'), 'verify', str(context)], check=True)
        tested = json.loads((ROOT / 'dist/build.json').read_text())
        if tested['commit'] != commit or tested['channel'] != channel or tested['checks'] != 'eng/test-fast.sh passed':
            raise ValueError('Context has no matching successful application checks')
        if sha(ROOT / 'dist/xur-app-x86_64.tar.gz') != tested['sha256']:
            raise ValueError('Tested application archive changed')
        if sha(context / 'publish-receipt.json') != tested['contextSha256']:
            raise ValueError('Publication context differs from the tested context')
        directory.mkdir(parents=True, exist_ok=True)
        with tarfile.open(archive, 'w') as tar:
            tar.add(context, arcname='.')
        receipt.write_text(json.dumps({'schema': 1, 'commit': commit, 'channel': channel,
                                      'sha256': sha(archive), 'contextSha256': tested['contextSha256'],
                                      'checks': tested['checks']}) + '\n')
    else:
        tested = json.loads(receipt.read_text())
        if (tested.get('schema'), tested.get('commit'), tested.get('channel'), tested.get('checks')) != (
                1, commit, channel, 'eng/test-fast.sh passed') or sha(archive) != tested.get('sha256'):
            raise ValueError('Tested context identity or checksum mismatch')
        if context.exists():
            raise ValueError('Refusing to replace an existing publication context')
        context.mkdir(parents=True)
        with tarfile.open(archive) as tar:
            tar.extractall(context, filter='data')
        if sha(context / 'publish-receipt.json') != tested.get('contextSha256'):
            raise ValueError('Publication receipt differs from the tested context')
        subprocess.run(['python3', str(ROOT / 'eng/context-receipt.py'), 'verify', str(context)], check=True)
    print(f'{action}: verified tested context for {channel} {commit}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['export', 'import'])
    parser.add_argument('--directory', type=pathlib.Path, default=ROOT / '.build/ci-context')
    parser.add_argument('--commit', required=True)
    parser.add_argument('--channel', required=True)
    args = parser.parse_args()
    transfer(args.action, args.directory, args.commit, args.channel)
