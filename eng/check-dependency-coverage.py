#!/usr/bin/env python3
"""Reject supported manifests missing from Dependabot and stale copied versions."""
import fnmatch
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
import yaml
from source_files import source_files

ROOT = pathlib.Path(__file__).resolve().parents[1]


def check(root, config):
    errors = []
    updates = config.get('updates', [])
    def covered(ecosystem, directory):
        return any(entry['package-ecosystem'] == ecosystem and
                   any(fnmatch.fnmatchcase(directory, pattern.rstrip('/') or '/')
                       for pattern in entry.get('directories', [entry.get('directory', '')]))
                   for entry in updates)
    def require(ecosystem, path):
        directory = '/' + str(path.parent.relative_to(root)) if path.parent != root else '/'
        if not covered(ecosystem, directory):
            errors.append(f'{path.relative_to(root)}: missing {ecosystem} Dependabot directory {directory}')
    for path in source_files(root):
        if path.suffix == '.csproj' and ET.parse(path).findall('.//PackageReference'):
            require('nuget', path)
        elif path.name == 'package.json':
            require('npm', path)
            lock = path.with_name('package-lock.json')
            if not lock.exists():
                errors.append(f'{path.relative_to(root)}: missing package-lock.json')
            else:
                manifest = json.loads(path.read_text())
                installed = json.loads(lock.read_text())['packages']['']
                for field in ('dependencies', 'devDependencies'):
                    if manifest.get(field, {}) != installed.get(field, {}):
                        errors.append(f'{path.relative_to(root)}: npm lock does not match {field}')
        elif path.name == 'libman.json':
            if str(path.relative_to(root)) not in (root / 'docs/development/dependencies.md').read_text():
                errors.append(f'{path.relative_to(root)}: document manual LibMan dependency checks in docs/development/dependencies.md')
        elif path.name in ('requirements.txt', 'requirements.in', 'pyproject.toml', 'Pipfile'):
            require('pip', path)
        elif re.search(r'dockerfile|containerfile', path.name, re.I):
            images = re.findall(r'^FROM (\S+)', path.read_text(), re.M)
            if any('/' in image and not image.startswith('localhost/') for image in images):
                require('docker', path)
        elif path.parent.name == 'workflows' and path.suffix in ('.yml', '.yaml'):
            require('github-actions', root / 'workflow.yml')
    if (root / 'global.json').exists():
        require('dotnet-sdk', root / 'global.json')
    manual = (root / 'docs/development/dependencies.md').read_text()
    for path in source_files(root):
        if (path.name.endswith('lock.json') and path.name not in ('packages.lock.json', 'package-lock.json')) or path.name == 'font-source.json':
            if str(path.relative_to(root)) not in manual:
                errors.append(f'{path.relative_to(root)}: document custom dependency checks in docs/development/dependencies.md')
    engines = root / 'catalog/engines/Containerfile'
    if engines.exists():
        for image, alias in (('vllm-openai', 'vllm'), ('vllm-omni', 'omni')):
            if not re.search(r'^FROM mirror\.gcr\.io/vllm/' + image + r':latest AS ' + alias + r'$', engines.read_text(), re.M):
                errors.append(f'{engines.relative_to(root)}: {image} must use the mirrored latest channel')
    sdk = root / 'global.json'
    toolchain = root / 'eng/toolchain-lock.json'
    if sdk.exists() and toolchain.exists():
        if json.loads(sdk.read_text())['sdk']['version'] != json.loads(toolchain.read_text())['dotnetSdk']['version']:
            errors.append('Refresh the custom SDK archive URL/checksum in eng/toolchain-lock.json to match global.json')
    return errors


if __name__ == '__main__':
    errors = check(ROOT, yaml.safe_load((ROOT / '.github/dependabot.yml').read_text()))
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        raise SystemExit(1)
    print('Supported dependency manifests are covered by Dependabot; copied versions agree.')
