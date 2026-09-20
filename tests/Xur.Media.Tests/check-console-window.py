#!/usr/bin/env python3
"""Verify separate VT windows and stable QR pixels while the system journal updates."""
import argparse,json,pathlib,subprocess,time,urllib.request,hashlib,zxingcpp
from PIL import Image
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args();repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
qmp=repo/'tests/Xur.Media.Tests/qmp.py'
def command(name,args={}):
 r=json.loads(subprocess.check_output(['python3',str(qmp),a.name,name,json.dumps(args)]));assert 'error' not in r
# Return to the root window and allow status to settle.
command('send-key',{'keys':[{'type':'qcode','data':'0'}]});time.sleep(.2)
command('send-key',{'keys':[{'type':'qcode','data':'ret'}]});time.sleep(6)
def capture():
 command('screendump',{'filename':str(vm/'display.private.ppm')})
 im=Image.open(vm/'display.private.ppm');im.load();return im
menu=capture()
# Check the real VGA pixels: TV margins and a solid selected-row background.
rgb=menu.convert('RGB');width,height=rgb.size
bounds=rgb.getbbox();assert bounds and bounds[0]>=width*.04 and bounds[1]>=height*.04 and bounds[2]<=width*.96 and bounds[3]<=height*.96,'Console must leave TV overscan margins'
assert any(sum(min(rgb.getpixel((x,y)))>=100 for x in range(width))>width*.6 for y in range(height)),'Selected menu line must have a full-width highlight'
menu_hash=hashlib.sha256(menu.tobytes()).hexdigest()
for _ in range(3):
 command('send-key',{'keys':[{'type':'qcode','data':'0'}]});time.sleep(.15)
 command('send-key',{'keys':[{'type':'qcode','data':'ret'}]});time.sleep(.3)
assert hashlib.sha256(capture().tobytes()).hexdigest()==menu_hash,'Menu input must not scroll or echo over the screen'
command('send-key',{'keys':[{'type':'qcode','data':'down'}]});time.sleep(.3)
assert hashlib.sha256(capture().tobytes()).hexdigest()!=menu_hash,'Arrow down must move the selection'
command('send-key',{'keys':[{'type':'qcode','data':'up'}]});time.sleep(.3)
assert hashlib.sha256(capture().tobytes()).hexdigest()==menu_hash,'Arrow up must restore the selection'
assert urllib.request.urlopen('http://127.0.0.1:18081/health',timeout=5).status==200
command('send-key',{'keys':[{'type':'qcode','data':'alt'},{'type':'qcode','data':'f1'}]});time.sleep(1)
assert hashlib.sha256(capture().tobytes()).hexdigest()!=menu_hash,'Boot console must not contain the menu'
command('send-key',{'keys':[{'type':'qcode','data':'alt'},{'type':'qcode','data':'f3'}]});time.sleep(1)
assert hashlib.sha256(capture().tobytes()).hexdigest()==menu_hash,'Menu must occupy its own VT3'
command('send-key',{'keys':[{'type':'qcode','data':'alt'},{'type':'qcode','data':'f2'}]});time.sleep(1)
logs=capture();assert hashlib.sha256(logs.tobytes()).hexdigest()!=menu_hash,'Logs window must be separate'
command('send-key',{'keys':[{'type':'qcode','data':'alt'},{'type':'qcode','data':'f3'}]});time.sleep(1)
assert hashlib.sha256(capture().tobytes()).hexdigest()==menu_hash,'Switching log windows disturbed the menu'
command('send-key',{'keys':[{'type':'qcode','data':'down'}]});time.sleep(.2)
command('send-key',{'keys':[{'type':'qcode','data':'ret'}]})
for _ in range(20):
 time.sleep(1);im=capture();decoded=zxingcpp.read_barcodes(im,try_invert=True)
 if any('login.tailscale.com/' in r.text for r in decoded):break
else:raise AssertionError('QR window is not readable')
first=hashlib.sha256(im.tobytes()).hexdigest();time.sleep(12)
im=capture();assert hashlib.sha256(im.tobytes()).hexdigest()==first,'Asynchronous log/status refresh moved the QR'
assert any('login.tailscale.com/' in r.text for r in zxingcpp.read_barcodes(im,try_invert=True))
# Enter and standalone Escape must leave the QR without canceling enrollment.
for back in ['ret','esc']:
 command('send-key',{'keys':[{'type':'qcode','data':back}]});time.sleep(.6)
 assert hashlib.sha256(capture().tobytes()).hexdigest()==menu_hash,'QR return key must restore the menu'
 command('send-key',{'keys':[{'type':'qcode','data':'down'}]});time.sleep(.2)
 command('send-key',{'keys':[{'type':'qcode','data':'ret'}]});time.sleep(.6)
 assert hashlib.sha256(capture().tobytes()).hexdigest()==first,'Reopening QR must preserve pending enrollment'
assert urllib.request.urlopen('http://127.0.0.1:18081/').status==200
receipt={'suite':'ConsoleWindows','menuVirtualTerminal':3,'bootConsoleIsolated':True,'menuInputDoesNotScroll':True,'arrowNavigationAndEnter':True,'overscanMargins':True,'fullRowHighlight':True,'enterAndEscapeReturnFromQr':True,'qrPreservedWhenReopened':True,'separateLogWindow':True,'menuPreserved':True,'qrStableAcrossLogRefreshes':True,'visibleConsoleQrDecoded':True,'webResponsive':True,'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/console-window.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
