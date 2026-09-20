#!/usr/bin/env python3
"""Authenticate from private local-console evidence, check the live ISO, optionally approve the disposable test disk."""
import argparse, hashlib, http.cookiejar, json, pathlib, re, subprocess, time, urllib.request, urllib.error, urllib.parse
p=argparse.ArgumentParser();p.add_argument('--name',default='install');p.add_argument('--approve',action='store_true');p.add_argument('--qr',action='store_true');p.add_argument('--size-swap',action='store_true');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
jar=http.cookiejar.LWPCookieJar(str(vm/'session.private.cookies'))
if pathlib.Path(jar.filename).exists():jar.load(ignore_discard=True,ignore_expires=True)
opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
def request(path,data=None,port=18081):
    try:
        with opener.open(f'http://127.0.0.1:{port}'+path,None if data is None else urllib.parse.urlencode(data).encode(),timeout=10) as r:return r.status,r.read().decode()
    except urllib.error.HTTPError as e:return e.code,e.read().decode()
def token(body):return re.search(r'name="__RequestVerificationToken" value="([^"]+)"',body).group(1)
passed=[]
def check(condition,name):
    if not condition:raise AssertionError(name)
    passed.append(name)
code=request('/')[0];check(code==200,'Live ISO web root returns 200')
check(request('/',port=18082)[0]==200,'Second DHCP adapter serves the same web root')
if request('/api/disks')[0] in (401,403):
    from test_auth import browser_login
    browser_login(vm,request)
    jar.save(ignore_discard=True,ignore_expires=True)
    check(True,'Live login and account setup work')
network=request('/network')[1]
check('10.71.1.15' in network and '10.71.2.15' in network,'Two independent adapters acquired DHCP leases')
inventory=json.loads(request('/api/disks')[1])
media=[d for d in inventory['disks'] if d['path'].startswith('/dev/sr') or d['serial']=='XUR-BOOT-USB001']
check(bool(media) and all(d['blocked'] for d in media),'Installer media is protected')
storage=request('/install/storage')[1]
check('Answer-file discovery' not in storage and 'NoAnswer' not in storage,'Discovery details are absent from the disk selection page')
check(all(f'value="{d["path"]}"' not in storage for d in inventory['disks'] if d['blocked']),'Unavailable disks are not offered for selection')
for _ in range(60):
    status_code,scan_body=request('/api/installer')
    if status_code!=200 or json.loads(scan_body)['scan']['state']!='Starting':break
    time.sleep(1)
if status_code==200:
    scan=json.loads(scan_body)['scan']
    check(scan['state']=='NoAnswer','Read-only answer discovery completed with NoAnswer')
    check(set(d['path'] for d in inventory['disks'] if not re.match(r'^/dev/(zram|ram)\d+$',d['path'])).issubset(set(scan['readOnlyDevices'])),'Every attached persistent whole disk observed block read-only during discovery')
    check(all(set(['ro','nosuid','nodev','noexec']).issubset(set(m['options'].split(','))) and m['blockReadOnly'] for m in scan['mounts']),'Every answer-scan mount uses read-only nosuid,nodev,noexec options')
if (vm/'data.raw').exists():
    with (vm/'data.raw').open('rb') as f:digest=hashlib.file_digest(f,'sha256').hexdigest()
    check(digest==(vm/'data-before.sha256').read_text().strip(),'Non-target disk unchanged after live boot and scan')
    check(request('/install/plan',{'path':'/dev/vda1','__RequestVerificationToken':token(storage)})[0]==400,'Partition path rejected as install target')
    check(request('/install/plan',{'path':'/dev/vda','__RequestVerificationToken':token(storage)})[0]==200,'Real whole-disk plan preview available')
    review=request('/install/review')[1]
    check('XUR-TARGET-000001' in review and 'name="suffix"' not in review,'Review identifies the selected disk without serial entry')
    def hidden(name):return re.search(fr'name="{name}" value="([^"]+)"',review).group(1)
    form={'id':hidden('id'),'digest':hidden('digest'),'__RequestVerificationToken':token(review)}
    check(request('/install/approve',form | {'digest':'changed'})[0]==409,'Changed plan digest blocks release')
    if a.size_swap:
        def resize(size):
            result=subprocess.run(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'block_resize',json.dumps({'device':'target','size':size})],check=True,capture_output=True,text=True)
            assert 'error' not in json.loads(result.stdout)
        original=next(d['bytes'] for d in inventory['disks'] if d['serial']=='XUR-TARGET-000001')
        resize(original+1024**3)
        try:
            for _ in range(20):
                observed=json.loads(request('/api/disks')[1])
                if next(d['bytes'] for d in observed['disks'] if d['serial']=='XUR-TARGET-000001')!=original:break
                time.sleep(1)
            else:raise AssertionError('Guest did not observe resized disposable test disk')
            check(request('/install/approve',form)[0]==409,'Disk size swap after preview blocks exact approval')
        finally:resize(original)
        time.sleep(2)
        check(request('/install/plan',{'path':'/dev/vda','__RequestVerificationToken':token(storage)})[0]==200,'Fresh review required after disk identity change')
        review=request('/install/review')[1]
        form={'id':hidden('id'),'digest':hidden('digest'),'__RequestVerificationToken':token(review)}
    if a.approve:
        status,body=request('/install/approve',form)
        check(status==200 and 'operation-stage' in body and 'name="code"' not in body,'Selected-disk approval opens authenticated progress for real Anaconda')
        # A redirected login page also returns 200. Check the application and
        # protected APIs while Anaconda initializes, using only the same cookie.
        for _ in range(30):
            status,body=request('/api/installer')
            assert status==200,'Session lost authorization during Anaconda startup'
            operation=json.loads(body)['operation']
            assert operation['stage']!='Failed','Anaconda failed during startup'
            progress=request('/install/progress')[1]
            assert 'operation-stage' in progress and 'name="code"' not in progress,'Anaconda startup redirected progress to login'
            time.sleep(1)
        check(True,'Same browser cookie retains progress and API access throughout Anaconda startup')
if a.qr:
    subprocess.run(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'send-key',json.dumps({'keys':[{'type':'qcode','data':'0'}]})],check=True,capture_output=True)
    subprocess.run(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'send-key',json.dumps({'keys':[{'type':'qcode','data':'2'}]})],check=True,capture_output=True)
    subprocess.run(['python3',str(repo/'tests/Xur.Media.Tests/qmp.py'),a.name,'send-key',json.dumps({'keys':[{'type':'qcode','data':'ret'}]})],check=True,capture_output=True)
    page=request('/tailscale')[1]
    found=False
    for _ in range(30):
        time.sleep(1)
        private=(vm/'console.private.log').read_text(errors='replace')
        if 'login.tailscale.com/' in private and any(c in private for c in '▀▄█'):
            found=True;break
    check(found,'Root-menu action rendered real claim QR on visible serial console')
    check(request('/tailscale/start',{'__RequestVerificationToken':token(request('/install/progress')[1])})[0]==200,'Authenticated web-login action remains available while enrollment is pending')
    check(request('/')[0]==200,'Web root responsive while real Tailscale enrollment waits')
receipt={'suite':'LiveISO','passed':passed,'tailscaleEnrollment':'NotRun; requires operator identity','vm':a.name}
if (vm/'vm-manifest.json').exists():receipt['media']=json.loads((vm/'vm-manifest.json').read_text())
path=repo/'.build/evidence'/f'live-{a.name}.json';path.write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
