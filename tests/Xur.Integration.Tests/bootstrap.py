#!/usr/bin/env python3
"""Real live-installer host, isolated agent; no disks, network or host state mutated."""
import http.client,json,os,pathlib,signal,socket,socketserver,subprocess,sys,tempfile,threading,time
repo=pathlib.Path(__file__).resolve().parents[2]
passed=[]
def check(value,name):
    assert value,name
    passed.append(name)
with tempfile.TemporaryDirectory(prefix='xur-local-install-') as temp:
    root=pathlib.Path(temp);(root/'bin').mkdir()
    stub=root/'bin/tailscale';stub.write_text('#!/bin/sh\ntouch "'+str(root/'tailscale-called')+'"\nexit 1\n');stub.chmod(0o700)
    env=dict(os.environ,XUR_RUN=temp,XUR_STATE=temp,XUR_PORT='18070',XUR_MODE='Installer',XUR_CONSOLE='stdio',PATH=str(root/'bin')+':'+os.environ['PATH'])
    name=None;operation=None;approvals=0;exports=0
    disk={'path':'/dev/test','stablePath':'/dev/disk/by-id/test','serial':'TEST-001','wwn':'test','model':'Fixture SSD','bytes':68719476736,'layout':'test','mounts':[],'blocked':[]}
    class Handler(socketserver.StreamRequestHandler):
        def handle(self):
            global name,operation,approvals,exports
            method,path,_=self.rfile.readline().decode().split()
            headers={}
            while line:=self.rfile.readline().strip():
                key,value=line.decode().split(':',1);headers[key.lower()]=value.strip()
            data=self.rfile.read(int(headers.get('content-length','0')))
            if headers.get('transfer-encoding')=='chunked':
                while size:=int(self.rfile.readline().strip(),16):
                    data+=self.rfile.read(size);self.rfile.read(2)
                self.rfile.readline()
            if path=='/computer-name' and data:name=json.loads(data)['name']
            if path=='/approve':
                assert json.loads(data)=={'id':'plan','digest':'digest'}
                approvals+=1;operation={'id':'plan','stage':'Installing','message':'Installing fixture disk','updated':'2026-09-21T00:00:00Z'}
            if path=='/installation-logs/usb' and method=='POST':
                assert json.loads(data)=={'id':'usb-id'};exports+=1
            responses={'/installation-logs/usb':{'message':'Saved fixture report on USB.'} if method=='POST' else [{'id':'usb-id','path':'/dev/usb1','label':'Logs','model':'Fixture USB','installerMedia':False}],'/computer-name':{'name':name or 'xur','configured':name is not None},
              '/status':{'scan':{'state':'NoAnswer'},'operation':operation},'/network/settings':{'devices':[],'pending':None},'/disks':{'generation':'test','disks':[disk]},
              '/plan':{'id':'plan','digest':'digest','generation':'test','target':disk,'unaffected':[],'actions':['Erase fixture disk'],'expires':'2099-01-01T00:00:00Z'},
              '/approve':operation}
            body=b'Fixture log line' if path in ('/logs','/installation-logs') else json.dumps(responses.get(path,{})).encode()
            self.wfile.write(b'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: '+str(len(body)).encode()+b'\r\nConnection: close\r\n\r\n'+body)
    server=socketserver.UnixStreamServer(str(root/'agent.sock'),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
    process=subprocess.Popen([str(repo/'.build/context/publish/control/Xur.Control')],env=env,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
    def local(path,body=None):
        c=http.client.HTTPConnection('localhost');c.sock=socket.socket(socket.AF_UNIX);c.sock.connect(str(root/'control.sock'))
        c.request('GET' if body is None else 'POST',path,body=None if body is None else json.dumps(body),headers={'Content-Type':'application/json'})
        r=c.getresponse();result=r.status,r.read().decode();c.close();return result
    def frame():return local('/local/console-frame?columns=140&rows=50')[1]
    def wait(predicate):
        for _ in range(100):
            try:
                if predicate():return
            except OSError:pass
            if process.poll() is not None:raise AssertionError('Host exited')
            time.sleep(.1)
        raise AssertionError('Timed out waiting for console')
    try:
        wait(lambda:local('/local/application-health')[0]==200)
        for port in (18070,18433):
            for address,family in [('127.0.0.1',socket.AF_INET),('::1',socket.AF_INET6)]:
                with socket.socket(family) as c:
                    check(c.connect_ex((address,port))!=0,'Installer has no web listener: '+address+':'+str(port))
        check(not (root/'serve.sock').exists(),'Installer does not create a Tailscale Serve socket')
        for path in ('/','/login','/setup-account','/api/bootstrap','/api/auth/setup','/install/storage','/api/install/approve'):
            check(local(path)[0]==404 and local(path,{})[0]==404,'Web setup unavailable even on local socket: '+path)
        check(local('/local/setup/account',{'username':'admin','password':'password'})[0]==404,'Live console cannot create administrator credentials')
        check(local('/local/setup/plan',{'path':'/dev/test'})[0]==409,'Server name is required before planning')
        wait(lambda:'Server name' in frame())
        diagnostic_mode='--diagnostics' in sys.argv
        def snapshot():
            status,body=local('/local/diagnostics/console')
            assert status==200,(status,body)
            return json.loads(body)
        def act(option=None,text=None,confirm=False):
            current=snapshot()
            body={'revision':current['revision']}
            if option is not None:body['option']=option
            if text is not None:body['text']=text
            if confirm:body['confirmErase']=True
            status,result=local('/local/diagnostics/console/action',body)
            assert status==200,(status,result,current)
            return body,json.loads(result)
        if diagnostic_mode:
            current=snapshot()
            check(current['screen']=='computer-name' and current['acceptsText'],'Diagnostics exposes the real physical naming screen')
            action,next_screen=act(text='living-room')
            check(next_screen['screen']=='network-list','Diagnostic name submission advances the visible flow')
            check(local('/local/diagnostics/console/action',action)[0]==409,'Repeated action revision is rejected')
        else:
            process.stdin.write(b'living-room\n');process.stdin.flush()
        wait(lambda:name=='living-room' and 'Continue to disk selection' in frame())
        check('Step 2 of 4' in frame(),'Naming advances to networking without returning to the menu')
        check(local('/local/qr',{})[0]==409,'Tailscale enrollment is rejected even after naming')
        check(json.loads(local('/local/status')[1])['urls']==[],'Installer does not advertise inactive management URLs')
        if diagnostic_mode:
            # The rejected Tailscale probe displays its error; reopen the shared setup screen.
            act(option=ord('i'))
            act(option=ord('c'))
            current=snapshot()
            check(current['screen']=='setup-disks' and any(o['id']==256 for o in current['options']),'Diagnostics exposes eligible disk choices')
            act(option=256)
            check('TEST-001' in snapshot()['body'],'Remote disk review exposes exact disk identity')
            act(option=ord('y'))
            body={'revision':snapshot()['revision'],'option':ord('y')}
            check(local('/local/diagnostics/console/action',body)[0]==409 and approvals==0,'Remote Yes without explicit erase consent is rejected')
            body['confirmErase']=True
            check(local('/local/diagnostics/console/action',body)[0]==200,'Explicit remote erase approval starts the fixture installation')
            check(local('/local/diagnostics/console/action',body)[0]==409 and approvals==1,'Remote approval cannot be replayed')
        else:
            # The dedicated CLI must use the same local disk review and Yes/No approval.
            cli=subprocess.run([str(repo/'.build/context/publish/control/Xur.Control'),'setup'],input=b'1\n1\n2\n2\n0\n',env=env,capture_output=True,timeout=15,check=True)
            check(approvals==1 and b'TEST-001' in cli.stdout and b'Installing fixture disk' in cli.stdout,'xur setup reviews identity and sends exactly one explicit disk approval')
        operation=operation|{'stage':'Failed','message':'Fixture installation failure'}
        cli=subprocess.run([str(repo/'.build/context/publish/control/Xur.Control'),'setup'],input=b'1\n0\n2\n1\n0\n0\n',env=env,capture_output=True,timeout=15,check=True)
        check(exports==1 and approvals==1 and b'Fixture log line' in cli.stdout and b'Saved fixture report' in cli.stdout,'Failed installation exposes logs and USB export without another disk approval')
        if diagnostic_mode:
            act(option=ord('0'))
            process.stdin.write(b'4\n')
        else:process.stdin.write(b'0\n/cancel\n4\n')
        process.stdin.flush();wait(lambda:'Scroll logs' in frame())
        process.stdin.write(b'0\n');process.stdin.flush();wait(lambda:'Setup and installation' in frame() and 'Scroll logs' not in frame())
        check(True,'Logs return to the local setup menu')
        check(not (root/'tailscale-called').exists(),'Live installer never invokes Tailscale')
        check(not (root/'manager-account.json').exists(),'Administrator account remains unconfigured until installed browser setup')
    finally:
        os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=5);server.shutdown();server.server_close()
print(json.dumps({'suite':'LocalInstallerProcess','passed':passed}))
