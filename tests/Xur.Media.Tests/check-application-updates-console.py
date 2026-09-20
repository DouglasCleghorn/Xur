#!/usr/bin/env python3
import argparse,json,pathlib,subprocess,time
from guest import execute
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args();repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name

def keys(*names):
 r=json.loads(subprocess.check_output(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'send-key',json.dumps({'keys':[{'type':'qcode','data':n} for n in names]})]));assert 'error' not in r;time.sleep(.3)
def screen():return execute(a.name,['/usr/bin/cat','/dev/vcs3'])['output']
def status():return json.loads(execute(a.name,['/usr/bin/python3','/var/lib/xur/app/current/host/app-update','status'])['output'])
keys('ctrl','alt','f3');keys('esc');keys('6');keys('ret')
assert 'Xur application' in screen() and 'Operating system' in screen()
keys('2');keys('ret')
for _ in range(30):
 if 'Xur updates' in screen() and 'Server:' in screen():break
 time.sleep(1)
else:raise AssertionError('Application update menu not visible')
previous=status()['operation']['id'];keys('ret')
for _ in range(30):
 s=status()
 if s['operation']['id']!=previous and not s['busy'] and s['operation']['stage']=='Complete':break
 time.sleep(1)
else:raise AssertionError('Terminal check did not execute')
keys('esc');time.sleep(6);assert 'Operating system' in screen() and 'Server:' not in screen()
keys('esc');assert 'Status and login' in screen()
receipt={'suite':'ApplicationUpdatesConsole','result':'Passed','realVirtualKeyboard':True,'repositoryCheck':True,'escapeReturnsToRoot':True,'updatesSubmenu':True,'backReturnsToParent':True,'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/application-updates-console.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
