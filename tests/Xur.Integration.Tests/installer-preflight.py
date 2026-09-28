#!/usr/bin/env python3
"""Clock and image-path checks with fake commands/files; never set host time."""
import datetime, importlib.machinery, importlib.util, json, pathlib, subprocess, tempfile, types
repo=pathlib.Path(__file__).resolve().parents[2]
def load(name,path):
    loader=importlib.machinery.SourceFileLoader(name,str(repo/path));spec=importlib.util.spec_from_loader(name,loader)
    module=importlib.util.module_from_spec(spec);loader.exec_module(module);return module
clock=load('clock','os/installer/check-clock');runtime=load('runtime','os/installer/check-runtime')
now=datetime.datetime(2026,9,28,1,2,3,tzinfo=datetime.timezone.utc)
passed=[]
for failure in (None,'service','offline','step','burst','wait','write','read','stale','malformed','timeout','missing'):
    calls=[]
    def run(args,**kwargs):
        calls.append(args)
        assert 0<kwargs['timeout']<=35 and kwargs['capture_output'] and kwargs['check'] is False
        stage=('service' if args[0]=='systemctl' else {'online':'offline','makestep':'step','burst':'burst','waitsync':'wait'}.get(args[1]) if args[0]=='chronyc' else 'write' if '--systohc' in args else 'read')
        if failure=='timeout' and stage=='wait':raise subprocess.TimeoutExpired(args,35)
        if failure=='missing' and stage=='service':raise FileNotFoundError()
        output='2026-09-28 01:02:03.000000+00:00\n' if stage=='read' else ''
        if failure=='stale' and stage=='read':output='2024-02-25 17:35:00.000000+00:00\n'
        if failure=='malformed' and stage=='read':output='not a timestamp'
        return types.SimpleNamespace(returncode=1 if failure==stage else 0,stdout=output)
    try:clock.sync_clock(run,has_rtc=True,now=lambda:now)
    except clock.ClockError:assert failure is not None
    else:assert failure is None
    if failure in ('service','offline','step','burst','wait','timeout','missing'):assert not any(c[0]=='hwclock' for c in calls)
    if failure is None:
        assert calls[-2:]==[['hwclock','--systohc','--utc'],['hwclock','--show','--utc']]
        assert ['chronyc','waitsync','15','1','0','2'] in calls
    passed.append('clock-'+str(failure))
calls=[]
clock.sync_clock(lambda args,**kw:(calls.append(args) or types.SimpleNamespace(returncode=0,stdout='')),has_rtc=False)
assert not any(c[0]=='hwclock' for c in calls);passed.append('no-RTC VM keeps synchronized system time')
with tempfile.TemporaryDirectory() as temp:
    root=pathlib.Path(temp);(root/'usr/bin').mkdir(parents=True);(root/'usr/sbin').symlink_to('bin')
    for name in runtime.PROGRAMS:
        path=root/'usr/bin'/name;path.write_text('#!/bin/sh\n');path.chmod(0o755)
    runtime.check(root)
    (root/'usr/sbin').unlink();(root/'usr/sbin').mkdir()
    try:runtime.check(root)
    except RuntimeError as error:assert 'symlink' in str(error)
    else:raise AssertionError('Overlay replacing /usr/sbin was accepted')
    (root/'usr/sbin').rmdir();(root/'usr/sbin').symlink_to('bin')
    for name in ('wpa_supplicant','chronyd','hwclock'):
        path=root/'usr/bin'/name;path.chmod(0o644)
        try:runtime.check(root)
        except RuntimeError as error:assert name in str(error)
        else:raise AssertionError('Missing executable accepted: '+name)
        path.chmod(0o755)
    passed.append('merged-sbin and executable validation')
prepare=(repo/'eng/prepare-rootfs.py').read_text();container=(repo/'os/installer/Containerfile').read_text()
assert "('tailscale/tailscaled','usr/bin/tailscaled')" in prepare and "'usr/sbin/" not in prepare
assert 'python3 /usr/libexec/xur-check-installer-runtime' in container and 'ln -s /usr/bin/ldconfig' not in container
assert container.index('COPY rootfs/ /')<container.index('xur-check-installer-runtime')
print(json.dumps({'suite':'InstallerPreflight','passed':passed,'hostClockChanged':False}))
