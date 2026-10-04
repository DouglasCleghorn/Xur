#!/usr/bin/env python3
"""Compression activation fixtures; never calls host journald or changes host logs."""
import importlib.machinery,importlib.util,json,pathlib,subprocess,tempfile

repo=pathlib.Path(__file__).resolve().parents[2]
loader=importlib.machinery.SourceFileLoader('log_compression',str(repo/'os/bootc/log-compression'))
spec=importlib.util.spec_from_loader(loader.name,loader);module=importlib.util.module_from_spec(spec);loader.exec_module(module)
evidence=repo/'.build/evidence';evidence.mkdir(parents=True,exist_ok=True)

with tempfile.TemporaryDirectory(dir=evidence,prefix='log-compression-') as temporary:
    root=pathlib.Path(temporary)
    commands=[]
    paths=[root/'etc/systemd'/name for name in module.FILES]
    pending=root/'etc/systemd/journald.conf.d/.xur-log-compression.pending'
    expected=[['restorecon',*[str(path) for path in paths]],
              ['systemctl','daemon-reload'],
              ['systemctl','restart','systemd-journald.service'],
              ['journalctl','--rotate']]
    def run(argv):
        assert pending.exists(), 'Activation must remain retryable until rotation succeeds'
        assert all(path.read_bytes()==(module.SOURCE/name).read_bytes() for path,name in zip(paths,module.FILES))
        commands.append(argv)
    assert module.ensure(root,runner=run,activate=False)=='installed'
    assert pending.exists() and not commands, 'Offline installation must not touch live journald'
    stamps=[path.stat().st_mtime_ns for path in paths]
    assert module.ensure(root,runner=run)=='activated'
    assert commands==expected and not pending.exists()
    assert [path.stat().st_mtime_ns for path in paths]==stamps
    assert all(path.stat().st_mode&0o777==0o644 for path in paths)
    def forbidden(argv):raise AssertionError('Unchanged startup invoked '+str(argv))
    assert module.ensure(root,runner=forbidden)=='current'
    assert [path.stat().st_mtime_ns for path in paths]==stamps, 'No configuration writes on later starts'
    # Upgrade from the previous default threshold without republishing the codec.
    paths[0].write_text('[Journal]\nCompress=yes\n');commands.clear()
    codec_stamp=paths[1].stat().st_mtime_ns
    assert module.ensure(root,runner=run)=='activated' and commands==expected
    assert 'Compress=128\n' in paths[0].read_text()
    assert paths[1].stat().st_mtime_ns==codec_stamp
    stamps=[path.stat().st_mtime_ns for path in paths]
    assert module.ensure(root,runner=forbidden)=='current'
    assert [path.stat().st_mtime_ns for path in paths]==stamps
    # Separate administrator overrides survive activation.
    local=paths[0].with_name('99-local.conf');local.write_text('[Journal]\nSystemMaxUse=1G\n')
    for failure in range(len(expected)):
        paths[0].write_text('[Journal]\nCompress=no\n');commands.clear()
        def fail(argv):
            run(argv)
            if len(commands)==failure+1:raise subprocess.TimeoutExpired(argv,10)
        try:module.ensure(root,runner=fail)
        except subprocess.TimeoutExpired:pass
        else:raise AssertionError('Activation failure was hidden')
        assert pending.exists(), 'Incomplete activation must retry on the next start'
        stamps=[path.stat().st_mtime_ns for path in paths];commands.clear()
        assert module.ensure(root,runner=run)=='activated' and commands==expected
        assert [path.stat().st_mtime_ns for path in paths]==stamps
        assert local.read_text()=='[Journal]\nSystemMaxUse=1G\n'
    # An interruption between publishing the two settings must also recover.
    paths[1].unlink();commands.clear();real_write=module.write
    def interrupted(path,content):
        if path==paths[1]:raise OSError('Injected configuration publication failure')
        real_write(path,content)
    module.write=interrupted
    try:
        try:module.ensure(root,runner=run)
        except OSError:pass
        else:raise AssertionError('Publication failure was hidden')
    finally:module.write=real_write
    assert pending.exists() and not commands
    assert module.ensure(root,runner=run)=='activated' and commands==expected
    # The executable runner applies a bound to every real host command.
    real_run=module.subprocess.run
    def bounded(argv,**kwargs):assert kwargs['timeout']==10 and kwargs['check'] is True
    module.subprocess.run=bounded
    try:module.call(['journalctl','--rotate'])
    finally:module.subprocess.run=real_run

print(json.dumps({'suite':'LogCompression','result':'Passed','offlineInstallation':True,
                  'thresholdUpgradeActivatesOnce':True,
                  'unchangedStartupWritesNothing':True,'failedActivationRetries':True,
                  'interruptedPublicationRetries':True,'administratorOverridesPreserved':True}))
