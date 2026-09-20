#!/usr/bin/env python3
"""Operate the installed Updates menu through real virtual keyboard events."""
import argparse,json,pathlib,subprocess,time
from guest import execute
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
def qmp(command,args):
    result=json.loads(subprocess.check_output(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,command,json.dumps(args)]))
    assert 'error' not in result
def key(name):
    qmp('send-key',{'keys':[{'type':'qcode','data':name}]});time.sleep(.3)
def screen():return execute(a.name,['/usr/bin/cat','/dev/vcs3'])['output']
def status():return json.loads(execute(a.name,['/var/lib/xur/app/current/host/os-update','status'])['output'])
key('esc');key('8');key('ret');assert 'Operating system' in screen()
key('2');key('ret')
for _ in range(30):
    if 'Automatic updates: On' in screen():break
    time.sleep(1)
else:raise AssertionError('Installed OS updates menu did not display real status')
qmp('screendump',{'filename':str(vm/'updates.private.ppm')})
# Pause and re-enable through the selected terminal row.
key('3');key('ret')
for _ in range(30):
    if not status()['automatic']:break
    time.sleep(1)
else:raise AssertionError('Terminal pause did not reach the OS updater')
time.sleep(2);key('ret')
for _ in range(30):
    if status()['automatic']:break
    time.sleep(1)
else:raise AssertionError('Terminal enable did not reach the OS updater')
key('esc');time.sleep(6);assert 'Operating system' in screen() and 'Toggle automatic updates' not in screen()
key('esc')
assert 'Status and login' in screen() and 'Toggle automatic updates' not in screen()
receipt={'suite':'UpdatesConsole','result':'Passed','realVirtualKeyboard':True,'installedStatus':True,'pauseAndEnable':True,'escapeReturnsToRoot':True,'updatesSubmenu':True,'backReturnsToParent':True,'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/updates-console.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
