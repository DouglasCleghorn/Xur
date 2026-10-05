#!/usr/bin/env python3
"""Start the real installed manager against isolated sockets and a read-only agent fixture."""
import http.client,json,os,pathlib,socket,socketserver,ssl,struct,subprocess,tempfile,threading,time

repo=pathlib.Path(__file__).resolve().parents[2]
evidence=repo/'.build/evidence/profile-switcher';evidence.mkdir(parents=True,exist_ok=True)
sdk=os.environ.get('XUR_DOTNET',str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet'))
# Unix socket names are limited to 108 bytes; keep isolated runtime paths short.
with tempfile.TemporaryDirectory(dir=repo/'.build',prefix='pa-') as temp:
 root=pathlib.Path(temp);(root/'catalog').mkdir()
 class Handler(socketserver.StreamRequestHandler):
  def handle(self):
   request=self.rfile.readline().decode().split()
   if len(request)<2:return
   path=request[1]
   while self.rfile.readline().strip():pass
   value={'/workloads':{'generation':'fixture','gpus':[],'instances':[]},'/computer-name':{'name':'fixture','configured':True},'/station-users':[], '/bootstrap-config':{'state':'NoAnswer'}}.get(path)
   body=json.dumps(value or {}).encode();status=b'200 OK' if value is not None else b'404 Not Found'
   self.wfile.write(b'HTTP/1.1 '+status+b'\r\nContent-Type: application/json\r\nContent-Length: '+str(len(body)).encode()+b'\r\nConnection: close\r\n\r\n'+body)
 class Server(socketserver.ThreadingUnixStreamServer):daemon_threads=True
 server=Server(str(root/'agent.sock'),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
 env=dict(os.environ,XUR_RUN=temp,XUR_STATE=temp,XUR_MODE='Installed',XUR_CATALOG=str(root/'catalog'),XUR_PORT='18900',XUR_CONSOLE='stdio')
 with open(evidence/'local-access-host.log','w') as log:
  process=subprocess.Popen([sdk,str(repo/'src/Xur.Control/bin/Release/net10.0/Xur.Control.dll')],env=env,stdout=log,stderr=log,stdin=subprocess.DEVNULL)
  def local(path):
   c=http.client.HTTPConnection('localhost');c.sock=socket.socket(socket.AF_UNIX);c.sock.connect(str(root/'control.sock'));c.request('GET',path);r=c.getresponse();status=r.status;r.read();c.close();return status
  def exact(c,length):
   data=b''
   while len(data)<length:
    part=c.recv(length-len(data))
    if not part:raise AssertionError('Local broker closed without a reply')
    data+=part
   return data
  def desktop():
   with socket.socket(socket.AF_UNIX) as c:
    c.settimeout(5);c.connect(str(root/'profile-switcher/switcher.sock'));body=b'{"action":"state"}';c.sendall(struct.pack('>I',len(body))+body)
    return json.loads(exact(c,struct.unpack('>I',exact(c,4))[0]))
  try:
   for _ in range(100):
    if process.poll() is not None:raise AssertionError('Control host exited; inspect private log')
    try:
     if local('/local/application-health')==200:break
    except OSError:pass
    time.sleep(.1)
   else:raise AssertionError('Control host did not become ready')
   for _ in range(100):
    if (root/'profile-switcher/switcher.sock').exists():break
    time.sleep(.01)
   assert local('/local/profiles')==200
   reply=desktop();assert not reply['ok'] and 'active Xur workstation' in reply['error']
   for path,status in [('/local/profiles',404),('/api/profiles',401),('/settings',302)]:
    c=http.client.HTTPSConnection('localhost',19263,context=ssl._create_unverified_context());c.request('GET',path);r=c.getresponse();assert r.status==status,(path,r.status);r.read();c.close()
   (root/'profile-access.json').write_text(json.dumps({'Mode':'web'}))
   assert local('/local/profiles')==403
   reply=desktop();assert not reply['ok'] and 'disabled' in reply['error']
   print(json.dumps({'suite':'ProfileAccessHost','isolatedStartup':True,'privateConsole':True,'publicTransportAndLogin':True,'inactiveDesktopDenied':True,'livePolicyRevocation':True}))
  finally:
   process.terminate()
   try:process.wait(timeout=5)
   except subprocess.TimeoutExpired:process.kill();process.wait()
   server.shutdown();server.server_close()
