#!/usr/bin/env python3
"""Exercise monthly numbering, remote tags and failure without publication."""
import importlib.util
import pathlib
import subprocess
import tempfile

repo = pathlib.Path(__file__).resolve().parents[2]
script = repo / 'eng/release-version.py'
spec = importlib.util.spec_from_file_location('release_version', script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

refs = '\n'.join('a' * 40 + '\trefs/tags/' + tag for tag in [
    'v26.09.1', 'v26.09.9', 'v26.09.9^{}', 'v26.08.70',
    'nightly-26.09.099', 'nightly-26.09.100', 'nightly-26.09.002',
    'v2026.09.21.30.1', 'nightly-2026.09.21.27.1', 'v26.09.10-extra',
])
assert module.next_version('stable', '26.09', refs) == '26.09.10'
assert module.next_version('nightly', '26.09', refs) == '26.09.101'
assert module.next_version('stable', '26.10', refs) == '26.10.1'
assert module.next_version('nightly', '27.01', refs) == '27.01.001'
assert module.next_version('nightly', '26.09', 'a refs/tags/nightly-26.09.999') == '26.09.1000'
for channel, month in [('local', '26.09'), ('stable', '26.13'), ('stable', '2026.09')]:
    try:
        module.next_version(channel, month, '')
    except ValueError:
        pass
    else:
        raise AssertionError((channel, month))

with tempfile.TemporaryDirectory() as directory:
    remote = pathlib.Path(directory) / 'remote'
    subprocess.run(['git', 'init', '-q', str(remote)], check=True)
    def git(*args):
        return subprocess.check_output(['git', '-C', str(remote), *args], text=True)
    git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
        'commit', '-q', '--allow-empty', '-m', 'fixture')
    for tag in ['v26.09.2', 'v26.09.7', 'nightly-26.09.009']:
        git('tag', tag)
    before = git('show-ref', '--tags')
    def command(channel, path=remote):
        return ['python3', str(script), '--channel', channel, '--month', '26.09', '--remote', str(path)]
    # A shallow checkout or retry sees the same remote tags and does not reserve
    # a number just by running the lookup.
    for _ in range(2):
        assert subprocess.check_output(command('stable'), text=True).strip() == '26.09.8'
    assert subprocess.check_output(command('nightly'), text=True).strip() == '26.09.010'
    assert git('show-ref', '--tags') == before
    failed = subprocess.run(command('stable', remote / 'missing'), capture_output=True, text=True)
    assert failed.returncode != 0 and not failed.stdout.strip()

print('Monthly release versions passed: counters, rollover, padding, retries and remote failure')
