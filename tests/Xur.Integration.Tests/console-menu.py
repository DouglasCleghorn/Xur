#!/usr/bin/env python3
"""Exercise the real interactive CLI against a root-socket fixture; never power the host."""
import http.server,json,os,pathlib,socketserver,subprocess,tempfile,threading

repo=pathlib.Path(__file__).resolve().parents[2]
sdk=os.environ.get('XUR_DOTNET',str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet'))
binary=repo/'src/Xur.Control/bin/Release/net10.0/Xur.Control.dll'
evidence=repo/'.build/evidence';evidence.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=evidence,prefix='console-') as temp:
    posts=[]
    deployment={'version':'1','digest':'old','image':'upstream','downloadOnly':False}
    os_status={'current':deployment,'available':deployment|{'version':'2','digest':'new'},'previous':None,'pending':None,'rollbackQueued':False,'automatic':True,'busy':False,'operation':None,'logs':''}
    app_status={'server':'https://updates.invalid','current':{'id':'old','version':'1'},'available':{'id':'new','version':'2'},'previous':None,'busy':False,'channel':'nightly'}
    all_status={'busy':False,'operation':None}
    network_status={'devices':[{'interface':'eno1','macAddress':'02:00:00:00:00:10','state':'connected','addresses':['192.0.2.1/24'],'connection':'fixture','ipv4':{'method':'auto'},'ipv6':{'method':'auto'},'editable':True}],'pending':None}
    network_payload=None
    installer=False
    polling=False;poll_reads=0;refreshed=threading.Event()
    class Handler(http.server.BaseHTTPRequestHandler):
        # Match Kestrel: keep framed responses alive instead of closing the Unix
        # socket after every HTTP/1.0 response (which races client reads in CI).
        protocol_version="HTTP/1.1"
        def log_message(self,*args):pass
        def do_GET(self):
            global poll_reads
            if polling and self.path=='/local/update-all':
                poll_reads+=1
                if poll_reads>=2:
                    all_status['operation']={'id':'poll','stage':'Complete','message':'Background refresh observed','updated':0,'results':[]}
                    refreshed.set()
            body={'/local/network/settings':network_status,'/local/status':{'installer':installer},'/local/updates':os_status,'/local/application-updates':app_status,'/local/update-all':all_status}.get(self.path)
            self.reply(200 if body is not None else 404,body or {})
        def do_POST(self):
            global network_payload
            data=self.rfile.read(int(self.headers.get('Content-Length',0)))
            if self.headers.get('Transfer-Encoding')=='chunked':
                while size:=int(self.rfile.readline().split(b';')[0],16):
                    data+=self.rfile.read(size);self.rfile.read(2)
                self.rfile.readline()
            posts.append(self.path)
            if self.path=='/local/network/settings':
                network_payload=json.loads(data)
                network_status['pending']={'id':'network-fixture','interface':'eno1','candidate':'candidate','previous':'previous','checkpoint':'checkpoint','expires':'2099-01-01T00:00:00Z','stage':'Confirm','message':'Keep settings','addresses':['192.0.2.20/24']}
            if self.path=='/local/network/keep':network_status['pending']['stage']='Kept'
            if self.path=='/local/application-updates/update':
                self.reply(409,{'error':'Signature verification failed.'});return
            if self.path=='/local/updates/disable':os_status['automatic']=False
            if self.path=='/local/update-all/start':
                all_status['operation']={'id':'test','stage':'Complete','message':'Update checks finished.','updated':0,'results':[{'name':'Xur','stage':'Complete','message':'Up to date.'}]}
            self.reply(202,{})
        def reply(self,status,body):
            data=json.dumps(body).encode();self.send_response(status)
            self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
    class Server(socketserver.ThreadingUnixStreamServer):daemon_threads=True
    server=Server(str(pathlib.Path(temp)/'control.sock'),Handler)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    transcript=repo/'.build/fast/console-menu-transcript.log'
    transcript.parent.mkdir(parents=True,exist_ok=True)
    transcript.write_text('')
    def run(lines='',args=()):
        result=subprocess.run([sdk,str(binary),*args],env=dict(os.environ,XUR_RUN=temp),input=lines,text=True,capture_output=True,timeout=25)
        with transcript.open('a') as log:
            log.write(f'Arguments: {args!r}; input: {lines!r}; exit: {result.returncode}\n{result.stdout}\n{result.stderr}\n')
        assert result.returncode==0,result.stderr
        return result.stdout
    try:
        output=run('6\n1\n0\n7\n1\n0\n2\n2\n0\n0\n')
        assert 'Update All' in output and 'nightly' in output and 'Update checks finished.' in output, output
        assert 'Confirm reboot' in output and 'Confirm shut down' in output, output
        assert posts==['/local/update-all/start','/local/poweroff'],posts
        posts.clear()
        output=run('6\n3\n3\n0\n2\n2\n0\n0\n0\n')
        assert 'Enable automatic updates' in output and 'Signature verification failed.' in output, output
        assert posts==['/local/updates/disable','/local/application-updates/update'],posts
        output=run(args=('update-all','status','--json'));assert json.loads(output)['operation']['stage']=='Complete'
        run(args=('update-all','start'));assert posts[-1]=='/local/update-all/start'
        polling=True
        process=subprocess.Popen([sdk,str(binary)],env=dict(os.environ,XUR_RUN=temp),stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            process.stdin.write('6\n');process.stdin.flush()
            assert refreshed.wait(12),'Status did not refresh while the text menu waited for input'
            output,error=process.communicate('0\n0\n',timeout=10)
            assert process.returncode==0,error
            assert 'Background refresh observed' in output, output
        finally:
            if process.poll() is None:process.kill();process.communicate()
        polling=False
        posts.clear()
        output=run('3\n1\n1\n2\n192.0.2.20/24\n0\n3\n1\n0\n0\n')
        assert posts==['/local/network/settings','/local/network/keep'],posts
        assert network_payload['interface']=='eno1' and network_payload['macAddress']=='02:00:00:00:00:10'
        assert network_payload['ipv4']['addresses']==['192.0.2.20/24'] and network_payload['ipv6']['method']=='auto'
        assert 'Keep settings' in output, output
        installer=True;posts.clear()
        output=run('6\n1\n0\n0\n0\n')
        assert '6. Power' in output and 'Updates' not in output and 'Confirm reboot' in output and not posts, output
    finally:server.shutdown();server.server_close()
print(json.dumps({'suite':'ConsoleMenu','realCli':True,'updateAll':True,'powerConfirmationAndCancellation':True,'failureFeedback':True,'backgroundRefreshWhileReadingInput':True,'installerMode':True,'staticNetworkTextEntryAndKeep':True}))
