#!/usr/bin/env python3
"""Exercise scheduled updates with a fake clock, deployments and desktop sessions."""
import contextlib, copy, datetime, fcntl, json, os, pathlib, pwd, runpy, shutil, subprocess, tempfile, time
from unittest.mock import patch

pathlib.Path('.build/evidence/update-schedule').mkdir(parents=True, exist_ok=True)
m = runpy.run_path('os/bootc/update-schedule', run_name='test_update_schedule')
g = m['automatic'].__globals__
channel = m['CHANNEL']
schedule = dict(time='03:00', days=list(range(7)), warningMinutes=15)
def stamp(hour, minute=0): return datetime.datetime(2026, 10, 5, hour, minute).timestamp()
def deployment(digest): return dict(version=digest, digest=digest, image=channel)

@contextlib.contextmanager
def fixture(available=True, saved_schedule=schedule):
    with tempfile.TemporaryDirectory(dir='.build/evidence/update-schedule') as temp:
        root = pathlib.Path(temp)
        if saved_schedule is not None:
            m['save'](root / 'settings.json', dict(automatic=True, schedule=saved_schedule))
        clock = [stamp(2, 45)]
        state = dict(current=deployment('old'), pending=None, previous=None, available=deployment('new') if available else None, rollbackQueued=False, busy=False, operation=None, logs='')
        checks, stages, notices = [], [], []
        def native(action):
            if action == 'check': checks.append(action)
            if action == 'stage': stages.append(clock[0]); state['pending'] = deployment('new')
            return copy.deepcopy(state) if action == 'status' else None
        with patch.dict(g, STATE=root, native=native, notify_workstations=lambda window:notices.append(window.copy())), \
             patch.object(time, 'time', side_effect=lambda:clock[0]):
            yield root, clock, state, checks, stages, notices

def load(root, name): return json.loads((root / (name + '.json')).read_text())
def tick(): m['automatic']()
def rejects(action):
    try: action()
    except RuntimeError: return
    raise AssertionError('Invalid request was accepted')

original_tz = os.environ.get('TZ')
try:
    os.environ['TZ'] = 'UTC'; time.tzset()
    with fixture(saved_schedule=None) as (root, clock, state, checks, stages, notices):
        sunday = datetime.datetime(2026, 10, 11, 3).timestamp()
        assert m['next_window'](m['settings'](), clock[0]) == sunday, 'An unconfigured host defaults to Sunday at 03:00'
        for day in range(6):
            clock[0] = stamp(2, 45) + day * 86400; tick()
        clock[0] = sunday - 960; tick()
        assert not checks and not stages and not notices, 'The default never runs Monday through Saturday or before the Sunday warning'
        clock[0] = sunday - 900; tick()
        assert checks == ['check'] and len(notices) == 1 and load(root, 'window')['startsAt'] == sunday
        clock[0] = sunday; tick()
        assert stages == [sunday], 'The default stages the available update on Sunday morning'
    with fixture() as (root, clock, state, checks, stages, notices):
        assert m['settings']()['schedule'] == schedule, 'Explicitly saved daily schedules remain unchanged'
    with fixture() as (root, clock, state, checks, stages, notices):
        clock[0] = stamp(2, 44); tick()
        assert not checks and not stages and not notices, 'Nothing runs outside the warning/window'
        clock[0] = stamp(2, 45); tick()
        window = load(root, 'window')
        assert checks == ['check'] and not stages
        assert window['startsAt'] == stamp(3) and window['state'] == 'Waiting' and len(notices) == 1
        assert m['observe']()['window']['id'] == window['id']
        clock[0] = stamp(2, 59); tick(); assert not stages
        clock[0] = stamp(3); tick(); assert stages == [stamp(3)]
        assert load(root, 'window')['state'] == 'Complete' and m['observe']()['window'] is None
        tick(); assert len(stages) == 1, 'The same window must never run twice'
    with fixture() as (root, clock, state, checks, stages, notices):
        tick(); window = load(root, 'window')
        rejects(lambda:m['configure']('skip', {'windowId':'stale'}))
        m['configure']('skip', {'windowId':window['id']})
        clock[0] = stamp(3); tick(); assert not stages
        assert m['settings']()['automatic'] and load(root, 'window')['state'] == 'Skipped'
        # A new host-script process sees the durable skip and keeps the schedule.
        fresh = runpy.run_path('os/bootc/update-schedule', run_name='test_restart')
        with patch.dict(fresh['automatic'].__globals__, STATE=root, native=lambda action:stages.append('bad')):
            fresh['automatic']()
        assert not stages and m['next_window'](m['settings'](), clock[0]) == stamp(3) + 86400
        clock[0] += 86400 - 900; tick(); assert load(root, 'window')['state'] == 'Waiting'
    with fixture(available=False) as (root, clock, state, checks, stages, notices):
        tick(); clock[0] = stamp(3); tick()
        assert not notices and not stages and len(checks) == 1
    with fixture() as (root, clock, state, checks, stages, notices):
        clock[0] = stamp(2, 55); tick()
        assert load(root, 'window')['startsAt'] == stamp(3, 10), 'A late check still leaves a full warning'
        clock[0] = stamp(3, 13); tick(); assert not stages and load(root, 'window')['state'] == 'Missed'
        rejects(lambda:m['configure']('skip', {'windowId':str(int(stamp(3)))}))
    with fixture() as (root, clock, state, checks, stages, notices):
        tick(); m['configure']('disable', {})
        clock[0] = stamp(3); tick(); assert not stages and m['observe']()['window'] is None
        assert m['settings']()['schedule'] == schedule
        m['configure']('enable', {}); tick(); assert not stages, 'Enabling does not resurrect a cancelled window'
    for action, request in [('disable', {}), ('schedule', {'schedule':schedule})]:
        with fixture() as (root, clock, state, checks, stages, notices):
            original_native=g['native']
            def during_check(operation):
                if operation == 'check': m['configure'](action, request)
                return original_native(operation)
            with patch.dict(g,native=during_check): tick()
            assert not notices and load(root, 'window')['state'] == 'Cancelled', 'Setting changes cancel an in-flight check'
    with fixture() as (root, clock, state, checks, stages, notices):
        state['pending'] = deployment('pending'); tick()
        assert not checks and not stages and not notices, 'Queued deployments must not be replaced'
    with fixture() as (root, clock, state, checks, stages, notices):
        with (root/'lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX);tick()
            assert not checks and not stages and not notices, 'Native deployment operations defer automatic work'
        tick();window=load(root,'window')
        with (root/'lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX)
            m['configure']('skip',{'windowId':window['id']})
        assert load(root,'window')['state']=='Skipped', 'Skipping does not wait behind native operations'
    with fixture() as (root, clock, state, checks, stages, notices):
        tick(); clock[0] = stamp(3)
        original_native=g['native']
        def rejected_stage(operation):
            if operation == 'stage':raise RuntimeError('Signature failure')
            return original_native(operation)
        with patch.dict(g,native=rejected_stage): rejects(tick)
        assert load(root, 'window')['state'] == 'Failed' and load(root, 'window')['message'] == 'Signature failure'
        rejects(lambda:m['configure']('skip', {'windowId':str(int(stamp(3)))}))
    for invalid in [dict(schedule,time='25:00'), dict(schedule,days=[]), dict(schedule,days=[True]), dict(schedule,warningMinutes=0)]:
        rejects(lambda:m['validate_schedule'](invalid))
    with fixture() as (root, clock, state, checks, stages, notices):
        m['save'](root/'settings.json', {'automatic':False})
        assert not m['settings']()['automatic'] and m['settings']()['schedule'] == dict(time='03:00', days=[6], warningMinutes=15), 'Legacy pause settings receive the Sunday default without enabling updates'
        m['configure']('schedule', {'schedule':dict(schedule,days=[0,4],time='22:30',warningMinutes=30)})
        assert not m['settings']()['automatic']
        assert m['next_window'](dict(automatic=True,schedule=m['settings']()['schedule']),stamp(23)) == datetime.datetime(2026,10,9,22,30).timestamp()
    # The real desktop-notice path converts only the explicit skip action into
    # a privileged setting change, with the existing user's device-limited slice.
    for selected in ('skip', ''):
        with fixture() as (root, clock, state, checks, stages, notices):
            tick(); window = load(root,'window'); calls=[]
            account=pwd.struct_passwd(('xuruser', 'x', 1001, 1001, '', '/var/home/xuruser', '/bin/bash'))
            def notification(args, **kwargs):
                calls.append(args);return subprocess.CompletedProcess(args,0,selected,'')
            with patch.dict(g,workstation_users=lambda:{1001:account}), \
                 patch.object(subprocess,'check_output',return_value='WAYLAND_DISPLAY=wayland-0\n'), \
                 patch.object(subprocess,'run',side_effect=notification):
                m['desktop_notice'](window['id'],1001)
            assert '--property=User=xuruser' in calls[0] and '--property=Slice=user-1001.slice' in calls[0]
            assert '--action=skip=Skip this window' in calls[0]
            assert load(root,'window')['state'] == ('Skipped' if selected=='skip' else 'Waiting')
    # A DST gap is skipped and a repeated clock time maps to one durable window.
    os.environ['TZ']='America/Denver';time.tzset()
    times=m['occurrences'](dict(schedule,time='02:30'),datetime.datetime(2026,3,8,0).timestamp())
    assert not any(datetime.datetime.fromtimestamp(value).date()==datetime.date(2026,3,8) for value in times)
    times=m['occurrences'](dict(schedule,time='01:30'),datetime.datetime(2026,11,1,0).timestamp())
    assert sum(datetime.datetime.fromtimestamp(value).date()==datetime.date(2026,11,1) for value in times)==1
    repeated=next(value for value in times if datetime.datetime.fromtimestamp(value).date()==datetime.date(2026,11,1))
    assert time.localtime(repeated).tm_isdst==1, 'Fall-back uses the earlier occurrence deterministically'
    # Persistent units from an older installation receive the new cadence once.
    with tempfile.TemporaryDirectory(dir='.build/evidence/update-schedule') as temp:
        override=pathlib.Path(temp)/'schedule.conf';calls=[]
        with patch.dict(g,TIMER_OVERRIDE=override),patch.object(subprocess,'run',side_effect=lambda args,**kwargs:calls.append(args)):
            m['initialize_timer']();m['initialize_timer']()
        assert len(calls)==2 and 'OnUnitInactiveSec=1min' in override.read_text() and 'RandomizedDelaySec=0' in override.read_text()
        override.unlink()
        with patch.dict(g,TIMER_OVERRIDE=override),patch.object(subprocess,'run',side_effect=subprocess.CalledProcessError(1,['systemctl'])):
            try:m['initialize_timer']()
            except subprocess.CalledProcessError:pass
            else:raise AssertionError('Timer setup failures must propagate')
        assert not override.exists(), 'A failed timer upgrade must be retried on the next initialization'
    # Keep the legacy host path while routing actual deployment and power work
    # to xurutil. The scheduler receives complete JSON arguments without a shell.
    with tempfile.TemporaryDirectory(dir='.build/evidence/update-schedule') as temp:
        host=pathlib.Path(temp);wrapper=host/'os-update';shutil.copyfile('os/bootc/os-update',wrapper)
        helper=host/'update-schedule';helper.write_text('import json,sys\nprint(json.dumps(dict(component="schedule",arguments=sys.argv[1:])))\n')
        utility=host/'xurutil';utility.write_text('#!/usr/bin/python3\nimport json,sys\nprint(json.dumps(dict(component="native",arguments=sys.argv[1:])))\n');utility.chmod(0o755)
        for action in ('check','stage','rollback','power-status'):
            result=json.loads(subprocess.check_output(['bash',str(wrapper),action],text=True))
            assert result==dict(component='native',arguments=['os-update',action])
        payload=json.dumps(dict(schedule=schedule))
        for action in ('auto','status','schedule','skip','initialize','notify','enable','disable'):
            result=json.loads(subprocess.check_output(['bash',str(wrapper),action,payload],text=True))
            assert result==dict(component='schedule',arguments=[action,payload])
finally:
    if original_tz is None: os.environ.pop('TZ',None)
    else: os.environ['TZ']=original_tz
    time.tzset()
print('Scheduled update checks passed: Sunday-only defaults, saved daily/weekly windows, full notice, no-update silence, durable skip, pause/change races, missed windows, deployment guards, failures, desktop actions, legacy settings and DST')
