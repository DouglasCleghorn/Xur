#!/usr/bin/env python3
"""Resolve the configured Fedora release tag once for this installer build."""
import datetime
import json
import pathlib
import re
import subprocess
import sys


def resolve(containerfile, run=subprocess.check_output):
    matches = re.findall(r'^ARG XUR_INSTALLER_BASE=(quay\.io/fedora/fedora-bootc:([0-9]+))$', containerfile, re.M)
    if len(matches) != 1:
        raise ValueError('Expected one Fedora release tag in the installer Containerfile')
    reference, release = matches[0]
    info = json.loads(run(['skopeo', 'inspect', '--override-arch', 'amd64', 'docker://' + reference], text=True, timeout=90))
    digest = info.get('Digest', '')
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', digest):
        raise ValueError('Invalid Fedora installer base digest')
    if info.get('Architecture') != 'amd64' or info.get('Os') != 'linux':
        raise ValueError('Fedora installer base must be Linux amd64')
    labels = info.get('Labels') or {}
    version = labels.get('org.opencontainers.image.version', '')
    if version != release and not version.startswith(release + '.'):
        raise ValueError('Fedora installer base version does not match the selected release')
    return {'reference': reference, 'resolvedReference': reference.rsplit(':', 1)[0] + '@' + digest,
            'digest': digest, 'architecture': 'amd64', 'version': version,
            'kernel': labels.get('ostree.linux', ''),
            'resolvedAt': datetime.datetime.now(datetime.timezone.utc).isoformat()}


if __name__ == '__main__':
    result = resolve(pathlib.Path(sys.argv[1]).read_text())
    pathlib.Path(sys.argv[2]).write_text(json.dumps(result, indent=2) + '\n')
    print(result['resolvedReference'])
