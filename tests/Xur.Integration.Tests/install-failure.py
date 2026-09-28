#!/usr/bin/env python3
"""Exercise the real installer wrapper with isolated resolver/Anaconda fixtures."""
import json, pathlib, subprocess, tempfile

repo = pathlib.Path(__file__).resolve().parents[2]
passed = []
for case in ('clock', 'source', 'anaconda', 'onerror-zero', 'post', 'success'):
    with tempfile.TemporaryDirectory(prefix='xur-install-failure-') as temp:
        root = pathlib.Path(temp)
        (root / 'approved.ks').touch()
        (root / 'install-operation.json').touch()
        clock = root / 'clock'
        clock.write_text('#!/bin/bash\nexit ' + ('6' if case == 'clock' else '0') + '\n')
        clock.chmod(0o700)
        resolver = root / 'resolve'
        resolver.write_text('#!/bin/bash\nexit ' + ('7' if case == 'source' else '0') + '\n')
        anaconda = root / 'anaconda'
        script = '#!/bin/bash\ntouch "' + str(root / 'anaconda-called') + '"\n'
        if case in ('post', 'onerror-zero'):
            script += 'touch "' + str(root / 'install-failed') + '"\n'
        if case == 'post':
            script += 'printf post > "' + str(root / 'install-phase') + '"\n'
        script += 'exit ' + ('9' if case in ('post', 'anaconda') else '0') + '\n'
        anaconda.write_text(script)
        resolver.chmod(0o700)
        anaconda.chmod(0o700)
        wrapper = root / 'run-install'
        wrapper.write_text((repo / 'os/installer/run-install').read_text()
            .replace('/run/xur', str(root))
            .replace('/usr/libexec/xur-check-install-clock', str(clock))
            .replace('/usr/libexec/xur-resolve-install-source', str(resolver))
            .replace('/usr/bin/anaconda', str(anaconda)))
        result = subprocess.run(['bash', str(wrapper)], capture_output=True, text=True)
        success = case == 'success'
        assert (result.returncode == 0) == success, (case, result.stderr)
        assert (root / 'install-complete').exists() == success
        assert (root / 'install-failed').exists() != success
        assert (root / 'anaconda-called').exists() == (case not in ('clock', 'source'))
        assert (root / 'install-phase').read_text().strip() == (case if case in ('clock', 'source') else 'post' if case == 'post' else 'anaconda')
        # Neither a service restart nor a failed run may reuse an old disk approval.
        (root / 'anaconda-called').unlink(missing_ok=True)
        retry = subprocess.run(['bash', str(wrapper)], capture_output=True, text=True)
        assert retry.returncode != 0 and not (root / 'anaconda-called').exists()
        passed.append(case)
print(json.dumps({'suite': 'InstallerFailure', 'passed': passed, 'realDiskWrites': False}))
