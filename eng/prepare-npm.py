#!/usr/bin/env python3
"""Install checked-in npm manifests into ignored build directories."""
import argparse
import hashlib
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
PACKAGES = {'browser': 'eng/browser', 'website': 'website'}


def prepare(name):
    source = ROOT / PACKAGES[name]
    target = ROOT / '.build' / name
    target.mkdir(parents=True, exist_ok=True)
    files = {file: (source / file).read_bytes() for file in ('package.json', 'package-lock.json')}
    fingerprint = hashlib.sha256(b'\n'.join(files.values())).hexdigest()
    stamp = target / 'manifest.sha256'
    if stamp.exists() and stamp.read_text() == fingerprint and (target / 'node_modules/.package-lock.json').exists():
        return target
    for file, data in files.items():
        (target / file).write_bytes(data)
    subprocess.run(['npm', 'ci', '--ignore-scripts', '--no-audit', '--no-fund'], cwd=target, check=True)
    stamp.write_text(fingerprint)
    return target


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', choices=PACKAGES)
    print(prepare(parser.parse_args().package))
