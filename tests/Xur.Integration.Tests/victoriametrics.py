#!/usr/bin/env python3
"""Real pinned VictoriaMetrics round trips, migration and abrupt-restart durability."""
import http.client,json,os,pathlib,shlex,socket,subprocess,time,urllib.parse
repo=pathlib.Path(__file__).resolve().parents[2]
os.chdir(repo)
subprocess.run(['python3','eng/prepare-metrics.py'],check=True)
root=repo/'.build/vm-integration'/str(time.time_ns());root.mkdir(parents=True)
sock=repo/'.build/vm-integration/metrics.sock'
if sock.exists():raise RuntimeError('Test socket exists; another test may be running')
binary=repo/'.build/metrics-runtime/victoria-metrics-prod'
sdk=os.environ.get('XUR_DOTNET',str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet'))
class Unix(http.client.HTTPConnection):
    def connect(self):
        self.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
        self.sock.settimeout(30);self.sock.connect(str(sock))
def get(path):
    c=Unix('localhost',timeout=30)
    try:
        c.request('GET',path);r=c.getresponse();data=r.read()
        if r.status!=200:raise RuntimeError((r.status,data[:500]))
        return data
    finally:c.close()
unit=(repo/'os/bootc/systemd/xur-metrics.service').read_text()
args=shlex.split(next(x.split('=',1)[1] for x in unit.splitlines() if x.startswith('ExecStart=')))
args[0]=str(binary)
args=[f'-httpListenAddr=unix:{sock}' if x.startswith('-httpListenAddr=') else f'-storageDataPath={root}/data' if x.startswith('-storageDataPath=') else x for x in args]
log=(root/'server.log').open('ab')
def start():
    p=subprocess.Popen(args,stdout=log,stderr=log)
    for _ in range(100):
        if p.poll() is not None:raise RuntimeError('VictoriaMetrics startup failed; see '+str(root))
        try:get('/health');return p
        except (OSError,RuntimeError):time.sleep(.1)
    p.terminate();p.wait(timeout=30);raise RuntimeError('VictoriaMetrics did not become ready')
def dump():
    content=get('/api/v1/export?'+urllib.parse.urlencode({'match[]':'{kind=~"gpu|network|storage"}','reduce_mem_usage':'1','start':'0','end':str(time.time()+60)})).decode()
    points={}
    for line in content.splitlines():
        item=json.loads(line);metric=json.dumps(item['metric'],sort_keys=True)
        for at,value in zip(item['timestamps'],item['values']):points[(metric,at)]=value
    return points
process=start()
try:
    with (root/'checks.log').open('w') as out:
        subprocess.run([sdk,'run','--project','tests/Xur.Unit.Tests','-c','Release','--no-build','--','--telemetry-server',str(sock)],stdout=out,stderr=subprocess.STDOUT,check=True)
    # Model-lab tests also write ordinary buffered samples after their migration checks.
    # Explicitly checkpoint those before testing lossless abrupt restart.
    get('/internal/force_flush')
    snapshot=json.loads(get('/snapshot/create'))['snapshot']
    get('/snapshot/delete?'+urllib.parse.urlencode({'snapshot':snapshot}))
    before=dump();assert before,'No stored telemetry'
    def compare(phase):
        after=dump()
        differences=[(key,before.get(key),after.get(key)) for key in before.keys()|after.keys() if before.get(key)!=after.get(key)]
        (root/(phase+'.json')).write_text(json.dumps({'before':len(before),'after':len(after),'differences':differences},indent=2)+'\n')
        assert not differences,'Telemetry changed after '+phase+'; see '+str(root)
    # The codec's migration checkpoints must survive SIGKILL, not merely a graceful stop.
    process.kill();process.wait(timeout=30);process=start()
    compare('abrupt-restart')
    process.terminate();assert process.wait(timeout=30)==0
    process=start();compare('clean-restart')
    result={'passed':True,'scalarSamples':len(before),'abruptRestart':True,'cleanRestart':True,'checks':str(root/'checks.log'),'serviceFlags':args[1:]}
    (root/'result.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
finally:
    if process.poll() is None:process.terminate();process.wait(timeout=30)
    log.close();sock.unlink(missing_ok=True)
