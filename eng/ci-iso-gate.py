#!/usr/bin/env python3
"""Build media only when inputs changed since this channel's last published ISO."""
import argparse
import json
import os
import pathlib
import re
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
# Application code, web assets and catalog updates ship in the independent bundle.
# Be conservative about installer, host services, build tooling and native inputs.
PREFIXES = ('os/', 'eng/', 'tools/Xur.Console/', 'tools/Xur.VirtualDisplay/',
            'tools/Xur.Streaming/', 'tools/Xur.Input/')
FILES = {'LICENSE', 'docs/licensing.md', '.github/workflows/installer.yml',
         '.github/workflows/release.yml'}


def changed_inputs(paths):
    return sorted(path for path in paths if path in FILES or path.startswith(PREFIXES))


def latest_installer(repo, channel):
    # A bounded lookup falls back to a rebuild if no recent ISO is found.
    releases = json.loads(subprocess.check_output(
        ['gh', 'api', f'repos/{repo}/releases?per_page=100'], text=True))
    releases = sorted(releases, key=lambda release: release.get('published_at') or '', reverse=True)
    for release in releases:
        if release['draft'] or release['prerelease'] != (channel == 'nightly'):
            continue
        names = {asset['name'] for asset in release['assets']}
        if not any(name.endswith('.iso') for name in names):
            continue
        filename = 'xur-update.json' if 'xur-update.json' in names else 'installer-build.json'
        if filename not in names:
            continue
        with tempfile.TemporaryDirectory(dir=ROOT / '.build') as directory:
            subprocess.run(['gh', 'release', 'download', release['tag_name'], '--repo', repo,
                            '--pattern', filename, '--dir', directory], check=True)
            receipt = json.loads((pathlib.Path(directory) / filename).read_text())
            if filename == 'xur-update.json':
                receipt = receipt['release']['installer']
        commit = receipt.get('commit', '')
        if receipt.get('channel') != channel or not re.fullmatch('[a-f0-9]{40}', commit):
            raise ValueError('Published installer identity is invalid')
        return commit
    return None


def decision(baseline, commit, force=False):
    if force or baseline is None:
        return True, 'Forced build' if force else 'No published installer baseline'
    ancestor = subprocess.run(['git', 'merge-base', '--is-ancestor', baseline, commit], cwd=ROOT,
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if ancestor.returncode:
        return True, 'Installer baseline is unavailable or not an ancestor'
    # --no-renames includes both old and new paths when moving an installer input.
    paths = subprocess.check_output(['git', 'diff', '--no-renames', '--name-only', baseline, commit],
                                    cwd=ROOT, text=True).splitlines()
    inputs = changed_inputs(paths)
    return bool(inputs), ', '.join(inputs) if inputs else 'Application-only changes since the published ISO'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--channel', required=True, choices=['nightly', 'stable'])
    parser.add_argument('--commit', required=True)
    parser.add_argument('--force', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch('[a-f0-9]{40}', args.commit):
        raise SystemExit('Invalid build commit')
    (ROOT / '.build').mkdir(exist_ok=True)
    try:
        baseline = None if args.force else latest_installer(os.environ['GITHUB_REPOSITORY'], args.channel)
    except (subprocess.CalledProcessError, ValueError, KeyError):
        # A failed baseline lookup must not silently skip a needed installer build.
        print('Installer baseline lookup failed; selecting a conservative rebuild.')
        baseline = None
    build, reason = decision(baseline, args.commit, args.force)
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write('build=' + str(build).lower() + '\n')
    print(json.dumps({'buildIso': build, 'baseline': baseline, 'reason': reason}))
