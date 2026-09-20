#!/usr/bin/env python3
"""Real installation from extracted FAT32 USB files, including account handoff."""
import argparse, hashlib, http.cookiejar, json, pathlib, re, subprocess, time, urllib.request
p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('usb',type=pathlib.Path);a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
tests=repo/'tests/Xur.Media.Tests';base='http://127.0.0.1:18081'
def run(script,*args):subprocess.run(['python3',str(tests/script),*map(str,args)],check=True)
def sha(path):
    with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
media=json.loads((vm/'vm-manifest.json').read_text());fixture=media['fileCopyUsb']
assert media['outboundNetworkBlocked'], 'Installation must use the on-media payload'
assert fixture and sha(a.usb)==fixture['usbSha256']
for _ in range(180):
    try:
        if urllib.request.urlopen(base+'/health',timeout=3).status==200:break
    except OSError:pass
    time.sleep(2)
else:raise AssertionError('File-copy USB did not reach the web application')
run('check-live.py','--name',a.name)
jar=http.cookiejar.LWPCookieJar(str(vm/'session.private.cookies'));jar.load(ignore_discard=True,ignore_expires=True)
opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
def get(path):return json.load(opener.open(base+path,timeout=10))
scan=get('/api/installer')['scan']
assert scan['state']=='NoAnswer'
assert any(m['filesystem']=='vfat' for m in scan['mounts'])
usb_disk=next(d for d in get('/api/disks')['disks'] if d['serial']=='XUR-BOOT-USB001')
assert any('Installer boot source' in reason for reason in usb_disk['blocked'])
assert usb_disk['mounts'] and any('Mounted whole disk or child partition' in reason for reason in usb_disk['blocked']), 'USB must be protected through its mounted child, not only its small size'
run('check-live.py','--name',a.name,'--size-swap','--approve')
for i in range(240):
    status=get('/api/installer');stage=status['operation']['stage']
    if stage=='Complete':break
    assert stage!='Failed','Installation failed; inspect private VM logs'
    if i%12==0:print('Installation stage: '+stage,flush=True)
    time.sleep(5)
else:raise AssertionError('Installation did not complete')
assert sha(vm/'data.raw')==(vm/'data-before.sha256').read_text().strip()
# The same browser cookie must remain authorized after a real reboot.
before=get('/api/status')
page=opener.open(base+'/install/progress').read().decode()
token=re.search(r'name="__RequestVerificationToken" value="([^"]+)"',page).group(1)
import urllib.parse
opener.open(base+'/power/reboot',urllib.parse.urlencode({'__RequestVerificationToken':token}).encode()).read()
for _ in range(180):
    try:
        current=get('/api/status')
        if current['mode']=='Installed' and get('/api/system'):break
    except (OSError,ValueError):pass
    time.sleep(2)
else:raise AssertionError('Installed system or authenticated session did not return')
run('check-installed.py',a.name)
home=opener.open(base+'/').read().decode();assert 'href="/install/' not in home
run('qmp.py',a.name,'quit')
assert sha(a.usb)==fixture['usbSha256'],'File-copy USB changed during install or reboot'
assert sha(vm/'data.raw')==(vm/'data-before.sha256').read_text().strip()
receipt={'suite':'FileCopyInstall','result':'Passed','media':media,'fat32ScannedReadOnly':True,'twoDhcpAdapters':True,'rootWebUi':True,'usbParentProtected':True,'exactApproval':True,'diskSizeSwapBlocked':True,'realAnacondaInstall':True,'offlinePayloadInstall':True,'installedBootWithUsbAttached':True,'sameCookieAcrossInstallAndReboot':True,'passwordAccountHandedOff':True,'nonTargetDiskUnchanged':True,'usbUnchanged':True,'windowsRufusGuiExecuted':False,'secureBootTested':False}
(repo/'.build/evidence/file-copy-install.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt))
