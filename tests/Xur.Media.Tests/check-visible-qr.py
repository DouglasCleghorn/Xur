#!/usr/bin/env python3
"""Decode the actual VGA framebuffer privately; retain no QR pixels or claim URL."""
import argparse,json,pathlib,subprocess,time,urllib.request,zxingcpp
from PIL import Image
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args();repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name
qmp=repo/'tests/Xur.Media.Tests/qmp.py'
def command(name,args):
 result=json.loads(subprocess.check_output(['python3',str(qmp),a.name,name,json.dumps(args)]));assert 'error' not in result
for key in ['0','2','ret']:command('send-key',{'keys':[{'type':'qcode','data':key}]})
for _ in range(20):
 time.sleep(1);command('screendump',{'filename':str(vm/'display.private.ppm')})
 decoded=zxingcpp.read_barcodes(Image.open(vm/'display.private.ppm'),try_invert=True)
 if any('login.tailscale.com/' in result.text for result in decoded):break
else:raise AssertionError('No readable real Tailscale QR on visible console')
assert urllib.request.urlopen('http://127.0.0.1:18081/',timeout=5).status==200
receipt={'suite':'VisibleConsoleQR','visibleConsoleQrDecoded':True,'webResponsive':True,'rawQrRetainedInRelease':False,'decoder':json.loads((repo/'.build/qr/receipt.json').read_text()),'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/console-qr.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
