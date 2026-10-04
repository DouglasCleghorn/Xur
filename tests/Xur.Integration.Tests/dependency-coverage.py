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
    write('src/New.Web/libman.json', '{"version":"1.0","libraries":[]}')
    write('catalog/engines/Containerfile', 'FROM mirror.gcr.io/vllm/vllm-openai:v0.29.0 AS vllm\nFROM mirror.gcr.io/vllm/vllm-omni:v0.28.0 AS omni\n')
    errors = module.check(root, config)
    assert any('new-project' in e and 'missing npm' in e for e in errors)
    assert any('npm lock does not match' in e for e in errors)
    assert any('new-lock.json' in e for e in errors)
    assert any('libman.json' in e and 'manual LibMan' in e for e in errors)
    assert len([e for e in errors if 'mirrored latest channel' in e]) == 2
print('Missing ecosystems, new unmanaged manifests, undocumented locks are rejected.')
