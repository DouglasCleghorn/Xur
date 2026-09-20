#!/usr/bin/env python3
"""Run unpublished settings/probe code only in an explicitly disposable VM."""
import functools,http.server,json,pathlib,tarfile,threading
from guest import execute
repo=pathlib.Path(__file__).resolve().parents[2];name='editor-dev'
assert json.loads((repo/'.build/vms'/name/'vm-manifest.json').read_text()).get('developmentVm')
folder=repo/'.build/settings-smoke-transfer';folder.mkdir(exist_ok=True)
with tarfile.open(folder/'test.tar.gz','w:gz') as tar:tar.add(repo/'.build/settings-smoke',arcname='test')
class Quiet(http.server.SimpleHTTPRequestHandler):
 def log_message(self,*args):pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Quiet,directory=folder));threading.Thread(target=server.serve_forever,daemon=True).start()
script=f'''set -eu
mkdir /run/xur-vm-settings-test
trap 'rm -rf /run/xur-vm-settings-test' EXIT
cd /run/xur-vm-settings-test
printf disposable > disposable
curl --fail --silent http://10.71.1.2:{server.server_port}/test.tar.gz -o test.tar.gz
tar -xzf test.tar.gz
./test/Xur.Unit.Tests --workload-settings-smoke
'''
try:
 r=execute(name,['bash','-lc',script]);assert r['code']==0,r['error']+'\n'+r['output']
 report=json.loads(r['output']);out=repo/'.build/fast/workload-settings-vm.json';out.write_text(json.dumps(report,indent=2)+'\n')
 print(json.dumps({k:v for k,v in report.items() if k!='graphics'}))
finally:server.shutdown();server.server_close()
