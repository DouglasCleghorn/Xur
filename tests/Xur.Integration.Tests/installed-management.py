#!/usr/bin/env python3
import ssl
"""Runs the real control process. A waiting child client is a test-only fixture.
No bootstrap code or cookie is emitted into test output or saved to disk.
"""
import threading, socketserver, secrets, ipaddress, http.cookiejar, http.client, socket, json, os, pathlib, re, signal, subprocess, tempfile, time
import urllib.request, urllib.parse, urllib.error
def tls_open(*args,**kwargs):return urllib.request.urlopen(*args,context=ssl._create_unverified_context(),**kwargs)
repo=pathlib.Path(__file__).resolve().parents[2]
passed=[]
with tempfile.TemporaryDirectory(prefix='xur-test-') as temp:
    root=pathlib.Path(temp); (root/'bin').mkdir()
    (root/'run').mkdir(mode=0o700)
    (root/'run/administrator.json').write_text(json.dumps({'login':'test-admin@example.invalid'}))
    client=root/'bin/tailscale'
    client.write_text('#!/bin/sh\nif [ "$1" = status ]; then echo \'{"BackendState":"NeedsLogin"}\'; else echo https://login.tailscale.com/a/test-only-fixture; sleep 120; fi\n')
    client.chmod(0o700)
    env=dict(os.environ,XUR_RUN=str(root/'run'),XUR_PORT='18070',XUR_MODE='Installed',XUR_STATE=str(root/'run'),XUR_CONSOLE='stdio',PATH=str(root/'bin')+':'+os.environ['PATH'])
    fixed=''.join(secrets.choice('0123456789ABCDEFGHJKMNPQRSTVWXYZ') for _ in range(6))
    operation=None
    discovery_done=False
    server_name=None
    ntp={'enabled':True,'active':True,'synchronized':True,'servers':[],'details':''}
    timezone={'current':'UTC','zones':['UTC','America/Denver','Asia/Kolkata']}
    class ConfigHandler(socketserver.StreamRequestHandler):
        def handle(self):
            global server_name
            path=self.rfile.readline().decode().split(' ')[1]
            headers={}
            while line:=self.rfile.readline().strip():
                key,value=line.decode().split(':',1);headers[key.lower()]=value.strip()
            data=self.rfile.read(int(headers.get('content-length','0')))
            if headers.get('transfer-encoding')=='chunked':
                while size:=int(self.rfile.readline().split(b';')[0].strip(),16):
                    data+=self.rfile.read(size);self.rfile.read(2)
                self.rfile.readline()
            if path=='/computer-name' and data:server_name=json.loads(data)['name']
            if path=='/ntp' and data:ntp.update(json.loads(data))
            if path=='/timezone' and data:timezone['current']=json.loads(data)['zone']
            disk={'path':'/dev/test','stablePath':'/dev/disk/by-id/test','serial':'TEST-001','wwn':'','model':'Test disk','bytes':34359738368,'layout':'test','mounts':[],'blocked':[]}
            body=json.dumps({'/bootstrap-config':{'state':'AnswerFound' if discovery_done else 'Starting','token':fixed if discovery_done else None},
                '/computer-name':{'name':server_name or 'xur','configured':server_name is not None,'message':'Server name saved'},
                '/timezone':timezone,'/ntp':ntp,
                '/status':{'scan':{'state':'NoAnswer' if discovery_done else 'Starting'},'operation':operation},
                '/disks':{'generation':'test','disks':[disk,disk|{'path':'/dev/blocked','model':'Hidden disk','blocked':['Boot media']}]}
                }.get(path,{})).encode()
            self.wfile.write((b'HTTP/1.1 404 Not Found' if body==b'{}' else b'HTTP/1.1 200 OK')+b'\r\nContent-Type: application/json\r\nContent-Length: '+str(len(body)).encode()+b'\r\nConnection: close\r\n\r\n'+body)
    server=socketserver.UnixStreamServer(str(root/'run/agent.sock'),ConfigHandler)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    proc=subprocess.Popen([str(repo/'.build/context/publish/control/Xur.Control')],cwd=repo/'.build/context/publish/control',env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,start_new_session=True)
    try:
        code=None
        for _ in range(200):
            line=proc.stdout.readline().decode()
            if re.fullmatch(r'Access code: [0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3}',line.strip()): code=line.split(':',1)[1].strip();break
            if proc.poll() is not None: raise RuntimeError('Control process failed to start')
        if not code:
            proc.wait(timeout=5)
            raise RuntimeError("Control startup failed: "+proc.stderr.read().decode())
        assert len(code)==7
        jar=http.cookiejar.CookieJar(); opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar),urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
        def request(path,data=None):
            try:
                with opener.open('https://127.0.0.1:18433'+path, None if data is None else urllib.parse.urlencode(data).encode(),timeout=5) as r: return r.status,r.read().decode()
            except urllib.error.HTTPError as e: return e.code,e.read().decode()
        def check(condition,name):
            if not condition: raise AssertionError(name)
            passed.append(name)
        status,body=request('/')
        check(status==200 and 'name="code"' in body and "CONTROL CENTER" not in body and "Primary navigation" not in body and "Appliance status" not in body,'Anonymous root shows only the access-code prompt')
        check(code not in body,'Anonymous root does not expose the code')
        check('Not case-sensitive.' not in body and 'Expires after 30 minutes.' not in body,'Login prompt omits the access-code policy note')
        check(request('/setup.css')[0]==200,'Stylesheet is served')
        check(request('/api/bootstrap',{'token':code})[0]==415,'API token exchange rejects form content')
        check(request('/api/install/plan',{})[0]==404,'API disk plan requires authentication')
        check(request('/api/disks')[0]==401,'Anonymous disk inventory denied')
        for route in ['/api/files/roots','/api/files/list','/api/files/download','/api/ntp']:
            check(request(route)[0]==401,'Anonymous access denied: '+route)
        check(request('/settings/ntp',{})[0]==401,'Anonymous NTP changes denied')
        check(request('/api/timezone')[0]==401,'Anonymous timezone inventory denied')
        check(request('/api/timezone',{})[0]==401,'Anonymous timezone changes denied')
        check(request('/api/workstations/1/graphics')[0]==401,'Anonymous workstation graphics probes denied')
        check(request('/api/gpu-power')[0]==401,'Anonymous GPU power inventory denied')
        check(request('/api/gpu-power',{})[0]==401,'Anonymous GPU power mutation denied')
        check(request('/local/console-frame')[0]==404,'Console frames cannot be read over LAN')
        check(request('/api/profiles')[0]==401,'Anonymous profile inventory denied')
        check('name="code"' in request('/settings')[1], 'Settings requires authentication')
        check(request('/install/approve',{})[0]==404,'Anonymous install mutation denied')
        check(request('/local/login')[0]==404,'Local secret endpoint unavailable over TCP')
        for path in ['/local/setup/account','/local/setup/disks','/local/setup/status','/local/setup/plan','/local/setup/approve']:
            check(request(path)[0]==404 and request(path,{})[0]==404,'Console setup endpoints cannot be reached over LAN: '+path)
        def unix_request(name,path,headers=None):
            connection=http.client.HTTPConnection('localhost')
            connection.sock=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM)
            connection.sock.connect(str(root/'run'/name))
            connection.request('GET',path,headers=headers or {})
            response=connection.getresponse();result=response.status,response.read().decode();connection.close();return result
        check(unix_request('control.sock','/local/login')[0]==200,'Private control socket serves local CLI requests')
        server_name='living-room'
        proc.stdin.write(b'5\n');proc.stdin.flush()
        for _ in range(100):
            if 'Scroll logs' in unix_request('control.sock','/local/console-frame?columns=100&rows=40')[1]:break
            time.sleep(.05)
        check(request('/health')[0]==200,'Management remains reachable while the console displays logs')
        proc.stdin.write(b'0\n');proc.stdin.flush()
        for _ in range(100):
            if 'Status and login' in unix_request('control.sock','/local/console-frame?columns=100&rows=40')[1]:break
            time.sleep(.05)
        check('Status and login' in unix_request('control.sock','/local/console-frame?columns=100&rows=40')[1],'Logs return to the menu without switching the input terminal')

        check(unix_request('serve.sock','/local/console-frame')[0]==404,'Tailscale cannot expose console frames')
        check(unix_request('serve.sock','/local/login')[0]==404,'Tailscale proxy cannot expose local secret endpoints')
        check(unix_request('serve.sock','/local/setup/account')[0]==404,'Tailscale proxy cannot expose console account setup')
        check(unix_request('serve.sock','/network',{'Tailscale-User-Login':'test-admin@example.invalid'})[0]==302,'Account setup is required before tailnet access')
        spoof=urllib.request.Request('https://127.0.0.1:18433/api/disks',headers={'Tailscale-User-Login':'test-admin@example.invalid'})
        try:spoof_status=opener.open(spoof).status
        except urllib.error.HTTPError as error:spoof_status=error.code
        check(spoof_status==401,'Forged Tailscale header on TCP cannot authenticate')
        cli=subprocess.run([str(repo/'.build/context/publish/control/Xur.Control'),'status','--json'],env=env,capture_output=True,check=True)
        check(json.loads(cli.stdout)['diskWrites']=='ApprovalRequired','System.CommandLine status --json reaches the running host')
        menu=subprocess.run([str(repo/'.build/context/publish/control/Xur.Control')],input=b'1\n',env=env,capture_output=True,check=True)
        check(code.encode() in menu.stdout and proc.poll() is None,'Second root-menu invocation reuses the host and bootstrap identity')
        status,body=request('/login')
        check(status==200 and code not in body,'Login page does not expose the code')
        token=re.search(r'name="__RequestVerificationToken" value="([^"]+)"',body).group(1)
        check('The sign-in form expired' in request('/auth/login',{'user':'xur','code':code})[1],'Login rejects missing antiforgery token with a fresh sign-in form')
        check('Invalid or expired access code.' in request('/auth/login',{'user':'xur','code':'wrong','__RequestVerificationToken':token})[1],'Wrong code rejected by running server')
        check(request('/auth/login',{'user':'xur','code':code,'__RequestVerificationToken':token})[0]==200,'Console code logs into the running application')
        status,setup_page=request('/')
        check('Create your account' in setup_page and 'Install Xur' not in setup_page,'Token login leads only to account creation')
        check(request('/api/disks')[0]==403,'Setup session cannot inspect disks')
        csrf=re.search(r'name="__RequestVerificationToken" value="([^"]+)"',setup_page).group(1)
        password=secrets.token_urlsafe(24)
        check(request('/auth/setup',{'username':'owner','password':password})[0]==400,'Account setup requires CSRF')
        status,body=request('/auth/setup',{'username':'owner','password':password,'__RequestVerificationToken':csrf})
        check(status==200 and 'Control panel' in body,'Browser creates account and receives manager session')
        check('Access code:' not in unix_request('control.sock','/local/login')[1],'Local CLI hides token after account setup')
        check(unix_request('serve.sock','/network',{'Tailscale-User-Login':'test-admin@example.invalid'})[0]==200,'Confirmed tailnet identity works after account setup')
        check(password not in (root/'run/manager-account.json').read_text(),'Only a password hash is persisted')
        check((root/'run/manager-account.json').stat().st_mode & 0o777==0o600,'Account file permissions are private')
        check("Control panel" in request('/')[1], 'Installed dashboard is available after authentication')
        check(request('/install/storage')[0]==404 and request('/api/install/approve',{})[0]==404,'Device installation routes are removed from the web manager')
        check(request('/network')[0]==200,'Authenticated network page available')
        settings=request('/settings')[1]
        assigned=json.loads(subprocess.check_output(['ip','-j','address','show']))
        check(all(address['local'] in settings for interface in assigned for address in interface['addr_info'] if not ipaddress.ip_address(address['local']).is_loopback), 'Settings lists every observed non-loopback IPv4 and IPv6 address')
        check('name="zone"' in settings and 'Asia/Kolkata' in settings and 'action="/settings/timezone"' in settings,'Settings renders the available system timezones')
        check(request('/settings/timezone',{'zone':'Asia/Kolkata'})[0]==400,'Timezone form requires CSRF')
        zone_csrf=re.search(r'name="__RequestVerificationToken" value="([^"]+)"',settings).group(1)
        zone_status,zone_page=request('/settings/timezone',{'zone':'Asia/Kolkata','__RequestVerificationToken':zone_csrf})
        check(zone_status==200 and 'Timezone saved.' in zone_page and json.loads(request('/api/timezone')[1])['current']=='Asia/Kolkata','Authenticated timezone form forwards the selected zone and displays confirmation')
        check(request('/settings/ntp',{'enabled':'true','servers':'time.example'})[0]==400,'NTP form requires CSRF')
        ntp_status,ntp_page=request('/settings/ntp',{'enabled':'true','servers':'time.example\n192.0.2.1','__RequestVerificationToken':zone_csrf})
        check(ntp_status==200 and 'NTP settings saved.' in ntp_page and ntp['servers']==['time.example','192.0.2.1'],'NTP form forwards enabled state and preferred servers')
        check('127.0.0.1' not in settings and '<code>::1</code>' not in settings, 'Settings excludes loopback addresses')
        check('http-equiv="refresh"' not in settings, 'Settings does not reset the timezone form while editing')
        server_name=None
        status,body=request('/tailscale'); token=re.search(r'name="__RequestVerificationToken" value="([^"]+)"',body).group(1)
        check('Save server name' in body and 'Sign in to Tailscale' not in body,'Web Tailscale setup requires a saved server name')
        rejected_enrollment=request('/tailscale/start',{'__RequestVerificationToken':token})[1]
        check('Save the server name' in rejected_enrollment and 'test-only-fixture' not in rejected_enrollment,'A direct enrollment POST cannot bypass the server-name requirement')
        check(request('/settings/computer-name',{'name':'living-room'})[0]==400,'Browser server-name changes require CSRF')
        named=request('/settings/computer-name',{'name':'living-room','__RequestVerificationToken':token})
        check(named[0]==200 and server_name=='living-room' and 'Sign in to Tailscale' in named[1],'Saving the server name enables Tailscale setup in the browser')
        check(request('/tailscale/start',{'__RequestVerificationToken':token})[0]==200,'Authenticated web-login action accepted')
        time.sleep(.3)
        tail=request('/tailscale')[1]
        check('href="https://login.tailscale.com/a/test-only-fixture"' in tail and 'Show QR on the machine' not in tail,'Authenticated Tailscale page offers the real client authorization link')
        anonymous=urllib.request.build_opener(urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
        check('test-only-fixture' not in anonymous.open('https://127.0.0.1:18433/tailscale').read().decode(),'Tailscale claim link is hidden from anonymous callers')
        start=time.monotonic(); status,body=request('/')
        check(status==200 and time.monotonic()-start<2,'Web host responds while child enrollment process waits')
        check(request('/install/approve',{})[0]==404,'Authenticated users cannot invoke removed web installation')
        # IPv6 reaches the same running dual-stack listener.
        direct=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
        with direct.open('https://[::1]:18433/health',timeout=5) as r: check(r.status==200,'IPv6 listener responds')
        api_login=urllib.request.Request('https://127.0.0.1:18433/api/auth/login',data=json.dumps({'username':'owner','password':password}).encode(),headers={'Content-Type':'application/json'})
        with tls_open(api_login,timeout=5) as r:manager=json.load(r)['accessToken']
        persisted=urllib.request.Request('https://127.0.0.1:18433/api/status',headers={'Authorization':'Bearer '+manager})
        os.killpg(proc.pid,signal.SIGTERM);proc.wait(timeout=5)
        proc=subprocess.Popen([str(repo/'.build/context/publish/control/Xur.Control')],cwd=repo/'.build/context/publish/control',env=env,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE,start_new_session=True)
        for _ in range(100):
            try:
                with tls_open(persisted,timeout=2) as response:
                    if response.status==200:break
            except OSError:pass
            time.sleep(.1)
        else:raise AssertionError('Account session did not survive restart')
        check('Access code:' not in unix_request('control.sock','/local/login')[1],'Restart does not display a new token')
        check('Control panel' in request('/')[1],'Browser manager cookie survives restart')
        anon=urllib.request.build_opener(urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
        login_page=anon.open('https://127.0.0.1:18433/login').read().decode()
        check('name="password"' in login_page and 'name="code"' not in login_page,'Returning login requests username and password')
        with tls_open(api_login,timeout=5) as response:check(response.status==200,'Password API login survives restart')
        if os.environ.get('XUR_CAPTURE')=='1':
            subprocess.run(['node','tests/Xur.Media.Tests/capture-ui.cjs','https://127.0.0.1:18433'],cwd=repo,check=True)
    finally:
        server.shutdown();server.server_close()
        if proc.poll() is None: os.killpg(proc.pid,signal.SIGTERM)
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: os.killpg(proc.pid,signal.SIGKILL); proc.wait()
print(json.dumps({'suite':'ControlProcess','passed':passed,'fixture':'Test-only waiting Tailscale child; real client checked on the ISO'}))
