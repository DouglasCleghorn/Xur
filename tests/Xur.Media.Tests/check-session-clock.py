#!/usr/bin/env python3
"""Exercise JWT cookies and API sessions while changing only a disposable VM's clock."""
import argparse, http.cookiejar, json, pathlib, re, time, urllib.error, urllib.request
from guest import execute

p=argparse.ArgumentParser(description=__doc__);p.add_argument('name');a=p.parse_args()
assert re.fullmatch('[a-z0-9-]+',a.name)
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
media=json.loads((vm/'vm-manifest.json').read_text())
assert media['firmware']=='UEFI OVMF' and (vm/'target.raw').is_file()
assert media.get('testTransport',{}).get('isoModified') is False

def guest(*args):
    result=execute(a.name,list(args))
    assert result['code']==0, 'Disposable VM command failed: '+args[0]
    return result['output'].strip()

jar=http.cookiejar.LWPCookieJar(str(vm/'session.private.cookies'))
jar.load(ignore_discard=True,ignore_expires=True)
browser=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
base='http://127.0.0.1:18081'
from test_auth import api_session
bearer=api_session(vm)

def request(path,api=False):
    try:
        open_request=urllib.request.urlopen if api else browser.open
        req=urllib.request.Request(base+path,headers={'Authorization':'Bearer '+bearer} if api else {})
        with open_request(req,timeout=10) as response:
            return response.status,response.read().decode()
    except urllib.error.HTTPError as error:return error.code,error.read().decode()

def authenticated():
    assert request('/api/system')[0]==200, 'Browser cookie rejected after clock change'
    assert request('/api/system',api=True)[0]==200, 'API bearer rejected after clock change'
    status,body=request('/')
    assert status==200 and 'name="code"' not in body, 'Dashboard redirected back to login'

def restart_manager():
    guest('/usr/bin/systemctl','restart','xur-control.service')
    for _ in range(60):
        try:
            if urllib.request.urlopen(base+'/health',timeout=2).status==200:return
        except OSError:pass
        time.sleep(1)
    raise AssertionError('Manager did not restart')

authenticated()
chrony=execute(a.name,['/usr/bin/systemctl','is-active','chronyd.service'])['code']==0
guest('/usr/bin/systemctl','stop','chronyd.service')
baseline=int(guest('/usr/bin/date','+%s'));started=time.monotonic()
passed=[]
try:
    guest('/usr/bin/date','--set=@'+str(baseline-6*3600))
    authenticated();passed.append('Browser and API sessions survive a six-hour backward clock correction')
    restart_manager()
    authenticated();passed.append('Same sessions survive a manager restart with the corrected clock')
    guest('/usr/bin/date','--set=@'+str(baseline+3600))
    authenticated();passed.append('Unexpired sessions survive a forward clock correction')
    guest('/usr/bin/date','--set=@'+str(baseline+9*3600))
    assert request('/api/system')[0]==401 and request('/api/system',api=True)[0]==401
    assert 'name="code"' in request('/')[1]
    passed.append('Signed session expiry still rejects expired browser and API credentials')
finally:
    guest('/usr/bin/date','--set=@'+str(baseline+int(time.monotonic()-started)))
    if chrony:guest('/usr/bin/systemctl','start','chronyd.service')
    # The restart under the shifted clock generated a local access code there;
    # renew that code under normal time for subsequent tests. Keep both sessions.
    restart_manager()
authenticated()
assert guest('/usr/sbin/getenforce')=='Enforcing'
receipt={'suite':'SessionClock','result':'Passed','passed':passed,'media':media,'hostClockChanged':False,'guestClockRestored':True}
(repo/'.build/evidence/session-clock.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
