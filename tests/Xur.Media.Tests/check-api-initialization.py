#!/usr/bin/env python3
"""Install in UEFI QEMU using only the JSON API and a private answer token."""
import argparse,hashlib,json,os,pathlib,secrets,subprocess,time,urllib.request,urllib.error
p=argparse.ArgumentParser();p.add_argument('iso',type=pathlib.Path);p.add_argument('--name',required=True);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];os.chdir(repo);os.umask(0o077)
tests=repo/'tests/Xur.Media.Tests';fixture=repo/'.build/fixtures'/a.name;fixture.mkdir(parents=True,exist_ok=False)
token=''.join(secrets.choice('0123456789ABCDEFGHJKMNPQRSTVWXYZ') for _ in range(6))
(fixture/'token.private').write_text(token)
files=fixture/'files';files.mkdir();(files/'xur.yaml').write_text('schemaVersion: 1\nbootstrapToken: '+token[:3]+'-'+token[3:]+'\n')
disk=fixture/'answer.raw';fs=fixture/'answer.ext4'
with fs.open('wb') as f:f.truncate(64*1024**2)
subprocess.run(['mkfs.ext4','-q','-F','-d',str(files),str(fs)],check=True)
with disk.open('wb') as f:f.truncate(128*1024**2)
subprocess.run(['sfdisk','--no-reread',str(disk)],input=b'label: gpt\nstart=2048,size=131072,type=0FC63DAF-8483-4772-8E79-3D69D8477DE4\n',stdout=subprocess.DEVNULL,check=True)
with disk.open('r+b') as out,fs.open('rb') as source:out.seek(1024**2);out.write(source.read())
def sha(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
answer_before=sha(disk)
def run(script,*args):subprocess.run(['python3',str(tests/script),*map(str,args)],check=True)
run('start-vm.py',a.iso,'--name',a.name,'--usb-boot','--extra-disk',disk)
vm=repo/'.build/vms'/a.name;base='http://127.0.0.1:18081';bearer=None
def request(path,data=None,authenticated=True):
    headers={'Content-Type':'application/json'}
    if authenticated and bearer:headers['Authorization']='Bearer '+bearer
    req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers=headers)
    try:
        with urllib.request.urlopen(req,timeout=10) as r:return r.status,r.read().decode()
    except urllib.error.HTTPError as error:return error.code,error.read().decode()
for _ in range(120):
    try:
        if request('/health')[0]==200:break
    except OSError:pass
    time.sleep(2)
else:raise AssertionError('Live server did not start')
page=request('/')[1];assert 'name="code"' in page and '<nav' not in page and 'CONTROL CENTER' not in page
assert request('/api/install/plan',{'path':'/dev/vda'},False)[0]==401
for _ in range(60):
    status,body=request('/api/bootstrap',{'token':token},False)
    if status in (401,429,503):time.sleep(30);continue
    assert status==200,'Answer-token API login failed'
    bearer=json.loads(body)['accessToken'];break
else:raise AssertionError('Read-only answer discovery did not finish')
assert request('/api/bootstrap',{'token':token},False)[0]==200,'Initialization token must support repeated login'
if json.loads(body).get('setupRequired'):
    from test_auth import credentials
    status,account=request('/api/auth/setup',credentials(vm));assert status==200
    bearer=json.loads(account)['accessToken']
    assert request('/api/bootstrap',{'token':token},False)[0]==401,'Token must be disabled after account setup'

state=json.loads(request('/api/installer')[1]);assert state['scan']['state']=='AnswerFound' and state['operation'] is None
assert all(m['blockReadOnly'] and set(['ro','nosuid','nodev','noexec']).issubset(m['options'].split(',')) for m in state['scan']['mounts'])
inventory=json.loads(request('/api/disks')[1])
target=next(d for d in inventory['disks'] if d['serial']=='XUR-TARGET-000001')
config=next(d for d in inventory['disks'] if d['serial']=='XUR-CONFIG-000000')
assert config['blocked'] and request('/api/install/plan',{'path':config['path']})[0]==400
assert request('/api/install/plan',{'path':target['path']+'1'})[0]==400
status,body=request('/api/install/plan',{'path':target['path']});assert status==200
plan=json.loads(body);assert plan['target']==target and any(d['serial']=='XUR-DATA-KEEP001' for d in plan['unaffected'])
assert request('/api/install/approve',{'id':plan['id'],'digest':'changed'})[0]==409
assert json.loads(request('/api/installer')[1])['operation'] is None
assert request('/api/install/approve',{'id':plan['id'],'digest':plan['digest']})[0]==200
for attempt in range(180):
    state=json.loads(request('/api/installer')[1]);stage=state['operation']['stage']
    if stage=='Complete':break
    assert stage!='Failed','Real API-driven install failed'
    if attempt%6==0:print('API install: '+stage,flush=True)
    time.sleep(5)
else:raise AssertionError('API-driven install timed out')
assert state['operation']['id']==plan['id']
before=(vm/'data-before.sha256').read_text().strip();assert sha(vm/'data.raw')==before and sha(disk)==answer_before
assert token not in request('/api/installer')[1] and token not in request('/api/logs')[1]
assert request('/api/power/reboot',{})[0]==202
run('check-installed.py',a.name)
assert request('/api/disks')[0]==200,'Signed API session must remain valid after reboot'
run('qmp.py',a.name,'quit')
receipt={'suite':'ApiInitialization','result':'Passed','answerTokenUsed':True,'noLiveConsoleSecretRequired':True,
 'reusableToken':True,'anonymousDashboardHidden':True,'answerScannedReadOnly':True,'configDiskProtected':True,
 'exactPlanApprovalWithoutSerialEntry':True,'realInstallAndReboot':True,'sessionValidAfterReboot':True,
 'dataSha256Before':before,'dataSha256After':sha(vm/'data.raw'),'answerSha256Before':answer_before,'answerSha256After':sha(disk),
 'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/api-initialization.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
