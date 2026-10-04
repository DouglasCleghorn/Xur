#!/usr/bin/env python3
"""Exercise installer transfer capture through a real PTY, without disk writes."""
import importlib.machinery
import importlib.util
import json
import os
import pathlib
import signal
import subprocess
import tempfile
import time

repo = pathlib.Path(__file__).resolve().parents[2]
loader = importlib.machinery.SourceFileLoader('bootc_progress', str(repo / 'os/installer/bootc-progress'))
spec = importlib.util.spec_from_loader(loader.name, loader)
capture = importlib.util.module_from_spec(spec)
loader.exec_module(capture)
evidence = repo / '.build/evidence/installer-byte-progress'
evidence.mkdir(parents=True, exist_ok=True)
passed = []

with tempfile.TemporaryDirectory(prefix='fixture-', dir=evidence) as temp:
    root = pathlib.Path(temp)
    status = root / 'install-download.json'
    progress = capture.Progress(status)
    progress.feed('\x1b[2KFetching layers ▰▰▱ 32/128\r\n └ Fetching ▰ 8.00 MiB/16.00 MiB (2.00 MiB/s) ostree chunk abc')
    assert json.loads(status.read_text()) == {'layerProgress': 'Fetching layers ▰▰▱ 32/128'}
    progress.feed('\r\n')
    assert json.loads(status.read_text())['byteProgress'] == '└ Fetching ▰ 8.00 MiB/16.00 MiB (2.00 MiB/s) ostree chunk abc'
    progress.feed('\x1b[1A\r\x1b[2KFetching layers ▰▰▱ 33/128\r\n')
    assert json.loads(status.read_text()) == {'layerProgress': 'Fetching layers ▰▰▱ 33/128'}, 'New layers must clear the previous layer counters'
    progress.feed('Fetching layers █ 129/128\nFetching layers █ 1/0\nFetching layers █ 1/1000001\n')
    assert json.loads(status.read_text()) == {'layerProgress': 'Fetching layers ▰▰▱ 33/128'}
    progress.feed('x' * 10000)
    assert len(progress.pending) <= 4096
    passed.append('split terminal frames, layer changes and bounded invalid input')

    unavailable = capture.Progress(root / 'missing-directory' / 'progress.json')
    unavailable.feed('Fetching layers █ 0/128\n')
    assert unavailable.disabled
    passed.append('counter write failure does not fail installation')

    # Executable fixture has exactly bootc's upstream terminal progress templates.
    fake = root / 'real-bootc'
    fake.write_text('''#!/usr/bin/python3
import json, os, pathlib, signal, sys, time
pathlib.Path(os.environ['FIXTURE_ARGS']).write_text(json.dumps(sys.argv[1:]))
assert not os.isatty(1), 'stdout must retain the Anaconda pipe'
if sys.argv[1:3] == ['install', 'to-filesystem']:
    assert os.isatty(2), 'download counters require terminal stderr'
    print('layers already present: 0; layers needed: 128 (5.6 GB)', flush=True)
    sys.stderr.write('\\x1b[2KFetching layers █░ 32/128\\r\\n └ Fetching █░ 8.00 MiB/16.00 MiB (2.00 MiB/s) ostree chunk abc\\r\\n')
    sys.stderr.flush()
    time.sleep(0.1)
    if os.environ.get('FIXTURE_WAIT'):
        signal.signal(signal.SIGTERM, signal.SIG_DFL)
        pathlib.Path(os.environ['FIXTURE_READY']).touch()
        time.sleep(30)
    print('original stderr failure details', file=sys.stderr, flush=True)
sys.exit(int(os.environ.get('FIXTURE_EXIT', '0')))
''')
    fake.chmod(0o700)
    (root / 'bin').mkdir()
    wrapper = root / 'bin/bootc'
    wrapper.write_text((repo / 'os/installer/bootc-progress').read_text()
        .replace("BOOTC = '/usr/bin/bootc'", 'BOOTC = ' + repr(str(fake)))
        .replace("pathlib.Path('/run/xur/install-download.json')", 'pathlib.Path(' + repr(str(status)) + ')'))
    wrapper.chmod(0o700)
    args_file = root / 'args.json'
    arguments = ['install', 'to-filesystem', '--source-imgref=registry:ghcr.io/test/os@sha256:' + 'a' * 64,
                 '--target-imgref=ghcr.io/test/os:stable', '--karg=value with spaces;$()', '/fixture/approved-target']
    env = {**os.environ, 'FIXTURE_ARGS': str(args_file), 'TERM': 'dumb'}
    for code in (0, 9):
        status.unlink(missing_ok=True)
        result = subprocess.run([str(wrapper), *arguments], env={**env, 'FIXTURE_EXIT': str(code)}, capture_output=True, text=True, timeout=10)
        assert result.returncode == code, result.stderr
        assert json.loads(args_file.read_text()) == arguments
        assert 'layers needed: 128' in result.stdout and 'original stderr failure details' in result.stderr
        assert '\x1b' not in result.stderr and 'Fetching layers' not in result.stderr
        counters = json.loads(status.read_text())
        assert counters == {'layerProgress': 'Fetching layers █░ 32/128', 'byteProgress': '└ Fetching █░ 8.00 MiB/16.00 MiB (2.00 MiB/s) ostree chunk abc'}
        assert status.stat().st_mode & 0o077 == 0
    passed.append('real PTY capture preserves argv, pipes, logs, privacy and exit status')

    status.unlink()
    result = subprocess.run([str(wrapper), 'status', '--json'], env=env, capture_output=True, timeout=10)
    assert result.returncode == 0 and json.loads(args_file.read_text()) == ['status', '--json'] and not status.exists()
    passed.append('non-install bootc commands pass through without capture')

    ready = root / 'ready'
    process = subprocess.Popen([str(wrapper), *arguments], env={**env, 'FIXTURE_WAIT': '1', 'FIXTURE_READY': str(ready)}, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        deadline = time.monotonic() + 10
        while not ready.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        assert ready.exists(), 'Fixture did not start'
        process.terminate()
        process.communicate(timeout=10)
        assert process.returncode == -signal.SIGTERM
    finally:
        if process.poll() is None:
            process.kill()
            process.communicate()
    passed.append('Anaconda cancellation reaches bootc and retains signal status')

    clock = root / 'clock'
    clock.write_text('#!/bin/sh\nexit 0\n')
    clock.chmod(0o700)
    anaconda = root / 'anaconda'
    anaconda.write_text('''#!/usr/bin/python3
import json, os, subprocess, sys
print('Creating disklabel on fixture target', flush=True)
result = subprocess.run(['bootc', *json.loads(os.environ['FIXTURE_INSTALL_ARGS'])])
print('Configuring installed system', flush=True)
sys.exit(result.returncode)
''')
    anaconda.chmod(0o700)
    for code, capture_enabled in ((0, True), (9, True), (0, False)):
        run = root / ('run-' + str(code) + '-' + str(capture_enabled))
        run.mkdir()
        (run / 'approved.ks').touch()
        (run / 'install-operation.json').touch()
        if not capture_enabled:
            (run / 'anaconda-output.log').mkdir()
        installer = run / 'run-install'
        installer.write_text((repo / 'os/installer/run-install').read_text()
            .replace('/run/xur', str(run))
            .replace('/usr/libexec/xur-check-install-clock', str(clock))
            .replace('/usr/libexec/xur-resolve-install-source', str(clock))
            .replace('/usr/libexec/xur-install-bin', str(root / 'bin'))
            .replace('/usr/bin/anaconda', str(anaconda)))
        result = subprocess.run(['bash', str(installer)], env={**env, 'FIXTURE_EXIT': str(code), 'FIXTURE_INSTALL_ARGS': json.dumps(arguments)}, capture_output=True, text=True, timeout=10)
        assert result.returncode == code, result.stderr
        output = (run / 'anaconda-output.log').read_text() if capture_enabled else result.stdout
        assert 'Creating disklabel on fixture target' in output and 'Configuring installed system' in output
        assert 'original stderr failure details' in output and 'layers needed: 128' in output
        assert json.loads(status.read_text())['layerProgress'] == 'Fetching layers █░ 32/128'
        assert (run / 'install-complete').exists() == (code == 0)
        assert (run / 'install-failed').exists() == (code != 0)
    passed.append('installer captures Anaconda stdout and stderr while preserving success and failure markers, including capture errors')

prepare = (repo / 'eng/prepare-rootfs.py').read_text()
container = (repo / 'os/installer/Containerfile').read_text()
installer = (repo / 'os/installer/run-install').read_text()
assert "('bootc-progress','usr/libexec/xur-install-bin/bootc')" in prepare
assert '/usr/libexec/xur-install-bin/bootc' in container
assert 'PATH="/usr/libexec/xur-install-bin:$PATH" /usr/bin/anaconda' in installer
passed.append('capture is shipped and selected only for Anaconda installation')
print(json.dumps({'suite': 'InstallerProgress', 'passed': passed, 'realDiskWrites': False}))
