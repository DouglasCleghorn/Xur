#!/usr/bin/env python3
"""Exercise missing coverage and manifest/artifact drift without network writes."""
import importlib.util
import json
import pathlib
import sys
import tempfile
import yaml

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'eng'))
spec = importlib.util.spec_from_file_location('coverage', ROOT / 'eng/check-dependency-coverage.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
config = yaml.safe_load((ROOT / '.github/dependabot.yml').read_text())
assert not module.check(ROOT, config)
for ecosystem in ('nuget', 'docker', 'npm', 'pip', 'dotnet-sdk', 'github-actions'):
    missing = config | {'updates': [e for e in config['updates'] if e['package-ecosystem'] != ecosystem]}
    assert any(f'missing {ecosystem}' in e for e in module.check(ROOT, missing)), ecosystem
with tempfile.TemporaryDirectory(dir=ROOT / '.build') as temporary:
    root = pathlib.Path(temporary)
    def write(name, text):
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
    write('docs/development/dependencies.md', 'Manual checks')
    write('eng/new-project/package.json', '{"dependencies":{"example":"1.0.0"}}')
    write('eng/new-project/package-lock.json', '{"packages":{"":{"dependencies":{"example":"2.0.0"}}}}')
    write('eng/new-lock.json', '{}')
    write('catalog/engines/Containerfile', 'FROM mirror.gcr.io/vendor/omni:v1 AS omni\n')
    write('os/engines/fish/Containerfile', 'FROM mirror.gcr.io/vendor/omni:v2\n')
    errors = module.check(root, config)
    assert any('new-project' in e and 'missing npm' in e for e in errors)
    assert any('npm lock does not match' in e for e in errors)
    assert any('new-lock.json' in e for e in errors)
    assert any('same Omni version' in e for e in errors)
print('Missing ecosystems, new unmanaged manifests, undocumented locks and version drift are rejected.')
