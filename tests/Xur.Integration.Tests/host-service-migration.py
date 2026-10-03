#!/usr/bin/env python3
"""Disposable unit files and mocked commands only; never calls host systemd/SELinux."""
import importlib.machinery,importlib.util,json,os,pathlib,subprocess,tempfile,types
repo=pathlib.Path(__file__).resolve().parents[2]
loader=importlib.machinery.SourceFileLoader('migration',str(repo/'os/bootc/host-service-migrate'))
spec=importlib.util.spec_from_loader(loader.name,loader);m=importlib.util.module_from_spec(spec);loader.exec_module(m)
evidence=repo/'.build/evidence';evidence.mkdir(parents=True,exist_ok=True)
current=(repo/'os/bootc/systemd/xur-agent.service').read_text().replace('/usr/bin/xur-agent','/var/lib/xur/app/current/agent/Xur.Agent').replace('/usr/lib/xur/agent','/var/lib/xur/app/current/agent')
legacy=current.replace('After=local-fs.target NetworkManager.service','After=local-fs.target xur-network.service').replace('Wants=xur-network.service','Requires=xur-network.service')
with tempfile.TemporaryDirectory(dir=evidence,prefix='host-service-migration-') as directory:
    root=pathlib.Path(directory);unit=root/'xur-agent.service';commands=[]
    def run(argv):
        assert argv in (['restorecon',str(unit)],['systemctl','daemon-reload']),argv
        commands.append(argv)
    def migrate():return m.migrate(unit,run,os.getuid())
    def write(value):
        unit.unlink(missing_ok=True);unit.write_text(value);unit.chmod(0o640)
    customized=legacy.replace('RestartSec=3','RestartSec=7')+'\n# Administrator annotation\n'
    write(customized);before=unit.stat();dropins=root/'xur-agent.service.d';dropins.mkdir();dropin=dropins/'custom.conf';dropin.write_text('[Service]\nEnvironment=ADMIN_SETTING=1\n')
    assert migrate()=='migrated'
    expected=customized.replace('After=local-fs.target xur-network.service','After=local-fs.target NetworkManager.service').replace('Requires=xur-network.service','Wants=xur-network.service')
    assert unit.read_text()==expected and unit.stat().st_mode&0o777==0o640
    assert (unit.stat().st_uid,unit.stat().st_gid)==(before.st_uid,before.st_gid)
    assert dropin.read_text()=='[Service]\nEnvironment=ADMIN_SETTING=1\n'
    first=unit.read_bytes();assert m.migrate(unit,lambda argv:(_ for _ in ()).throw(AssertionError('Completed migration invoked a command')),os.getuid())=='current' and unit.read_bytes()==first
    assert commands==[['restorecon',str(unit)],['systemctl','daemon-reload']], 'Completed boot must not need SELinux/systemd commands'
    marker=unit.with_name('.xur-agent-network-v1.completed')
    assert marker.stat().st_mode&0o777==0o600 and marker.stat().st_uid==os.getuid()
    marker.unlink();commands.clear();assert migrate()=='current'
    assert len(commands)==2, 'Interrupted repair without durable marker must reload'
    unit.write_text(expected+'# changed after completion\n');commands.clear();assert migrate()=='current'
    assert len(commands)==2, 'Completion marker must bind exact unit bytes'
    # Refuse masks/symlinks and unknown service/dependency definitions.
    for value in (legacy.replace('ExecStart=','ExecStart=/custom '),legacy.replace('After=local-fs.target','After=custom.service local-fs.target'),legacy+'\n[Unit]\nAfter=custom.service\n',legacy.replace('Requires=xur-network.service','Requires=xur-network.service other.service')):
        write(value);commands.clear();assert migrate()=='custom' and unit.read_text()==value and not commands
    write(legacy);unit.chmod(0o660);commands.clear();assert migrate()=='custom' and not commands
    unit.unlink();target=root/'external';target.write_text(legacy);unit.symlink_to(target);assert migrate()=='custom' and target.read_text()==legacy
    unit.unlink();unit.symlink_to('/dev/null');assert migrate()=='custom'
    unit.unlink();assert migrate()=='absent'
    write(legacy);commands.clear();assert m.migrate(unit,run,os.getuid()+1)=='custom' and not commands
    # Label/reload failures restore exact original bytes and metadata.
    for failure in ('restorecon','systemctl'):
        write(customized);calls=[];failed=False
        def fail_once(argv):
            global failed
            calls.append(argv)
            if argv[0]==failure and not failed:
                failed=True;raise subprocess.TimeoutExpired(argv,10)
        try:m.migrate(unit,fail_once,os.getuid())
        except subprocess.TimeoutExpired:pass
        else:raise AssertionError('Migration command failure was swallowed')
        assert unit.read_text()==customized and unit.stat().st_mode&0o777==0o640
        assert calls[-2:]==[['restorecon',str(unit)],['systemctl','daemon-reload']]
    # Failure after unit replacement but during directory fsync must roll back.
    write(customized);real_fsync=m.os.fsync;failed=False
    def fsync_fail_once(fd):
        global failed
        import stat
        if stat.S_ISDIR(os.fstat(fd).st_mode) and not failed:
            failed=True;raise OSError('Injected post-rename directory fsync failure')
        return real_fsync(fd)
    m.os.fsync=fsync_fail_once;commands.clear()
    try:
        try:migrate()
        except OSError:pass
        else:raise AssertionError('Post-rename directory sync failure was swallowed')
    finally:m.os.fsync=real_fsync
    assert failed and unit.read_text()==customized and unit.stat().st_mode&0o777==0o640
    assert commands==[['restorecon',str(unit)],['systemctl','daemon-reload']]
    # Never overwrite an administrator change made during migration failure.
    write(customized)
    def changed_then_failed(argv):
        if argv[0]=='systemctl':
            replacement=unit.with_suffix('.admin');replacement.write_text('administrator replacement\n');replacement.replace(unit)
            raise OSError('Injected failure after concurrent administrator edit')
    try:m.migrate(unit,changed_then_failed,os.getuid())
    except OSError:pass
    else:raise AssertionError('Injected reload failure was swallowed')
    assert unit.read_text()=='administrator replacement\n'
    # Verify the shipped command runner bounds all real operations; mock subprocess.
    original_run=m.subprocess.run
    def bounded(argv,**kwargs):assert kwargs['timeout']==10 and kwargs['check'] is True
    m.subprocess.run=bounded
    try:m.call(['systemctl','daemon-reload'])
    finally:m.subprocess.run=original_run
    # Exercise actual PRE-fix updater activation, without giving it a new hook.
    old_source=(repo/'tests/Xur.Integration.Tests/fixtures/app-update-before-agent-migration.py').read_text()
    old=types.ModuleType('old_updater');exec(compile(old_source,'old-app-update','exec'),old.__dict__)
    old.ROOT=root/'app';old.ROOT.mkdir();old.TX=old.ROOT/'transaction.json';old.BLOCK=old.ROOT/'maintenance';old.STATE=old.ROOT/'update.json';old.OP='fixture'
    previous='a'*64;new='b'*64
    for identity in (previous,new):
        release=old.ROOT/'releases'/identity;release.mkdir(parents=True);(release/'bundle.json').write_text(json.dumps({'id':identity,'hostAbi':1}));(release/'host').mkdir()
    (old.ROOT/'current').symlink_to('releases/'+previous)
    old.local=lambda *args:{'active':0,'profileBusy':False,'busy':False}
    write(legacy);commands.clear();events=[]
    def old_call(argv,timeout=60):
        events.append(argv)
        if argv[:2]==['systemctl','start']:
            # Old updater starts the NEW agent after link(). New agent startup
            # invokes the bundle helper before exposing application-health.
            assert old.current()['id']==new
            assert migrate()=='migrated'
        return b''
    old.call=old_call
    old.healthy=lambda identity:identity==new and unit.read_text()==current
    old.activate({'id':new})
    assert old.current()['id']==new and old.read(old.STATE)['stage']=='Complete'
    assert not any('migrate' in ' '.join(argv) for argv in events),'Old updater must not need knowledge of migration'
    # Forward-compatible unit relaxation survives app rollback/recovery: neither
    # old updater link nor recover writes the service file or restarts networking.
    events.clear();old.call=lambda argv,timeout=60:events.append(argv) or b''
    old.healthy=lambda identity:identity==previous
    old.atomic(old.TX,{'previous':previous,'target':new,'operation':'fixture'});old.recover()
    assert old.current()['id']==previous and unit.read_text()==current
    assert all('daemon-reload' not in argv and 'xur-network' not in argv for argv in events)
    program=(repo/'src/Xur.Agent/Program.cs').read_text()
    assert program.index('if(!installer && File.Exists(hostServiceMigration))')<program.index('app.MapGet("/application-health"')
    assert 'Processes.Run("/usr/bin/python3",[hostServiceMigration],45)' in program
    assert 'if(migrated.ExitCode!=0)throw' in program and 'HostServiceMigration' in program
    assert "'host-service-migrate'" in (repo/'eng/prepare-rootfs.py').read_text()
print(json.dumps({'suite':'HostServiceMigration','result':'Passed','firstUpgradeWithOldUpdater':True,'customizationsPreserved':True,'unknownAndUnsafeUnitsSkipped':True,'failureRestoresOriginal':True,'idempotent':True,'forwardCompatibleRollback':True,'boundedCommands':True,'completedBootNeedsNoCommands':True,'postRenameSyncFailureRestored':True,'administratorRacePreserved':True,'portableCompatibilityFixture':True}))
