#!/usr/bin/env python3
"""Refresh reviewed AG Grid source assets after a Dependabot manifest update."""
import importlib.util
import json
import pathlib
import shutil

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('prepare_npm', ROOT / 'eng/prepare-npm.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
installed = module.prepare('ag-grid') / 'node_modules/ag-grid-community'
output = ROOT / module.PACKAGES['ag-grid']
for source, name in [('dist/ag-grid-community.min.js', 'ag-grid-community.min.js'),
                     ('LICENSE.txt', 'LICENSE.txt')]:
    shutil.copy2(installed / source, output / name)
lock = json.loads((output / 'package-lock.json').read_text())['packages']['node_modules/ag-grid-community']
(output / 'upstream.json').write_text(json.dumps({'package': 'ag-grid-community', 'version': lock['version'],
                                                'url': lock['resolved'], 'integrity': lock['integrity']}, indent=2) + '\n')
print('AG Grid assets and license refreshed; review the diff and run the Files UI checks.')
