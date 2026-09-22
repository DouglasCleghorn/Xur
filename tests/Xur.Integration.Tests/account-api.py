#!/usr/bin/env python3
"""Real host account provisioning API; all credentials remain in memory."""
import ssl,concurrent.futures,http.client,json,os,pathlib,secrets,signal,socket,subprocess,tempfile,time,urllib.request,urllib.error
repo=pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='xur-account-api-') as tmp:
 root=pathlib.Path(tmp);(root/'bin').mkdir();stub=root/'bin/tailscale';stub.write_text('#!/bin/sh\necho \'{"BackendState":"NeedsLogin"}\'\n');stub.chmod(0o700)
 fixed="".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(6));(root/"bootstrap-token").write_text(fixed);(root/"bootstrap-token").chmod(0o600)
 env=dict(os.environ,XUR_RUN=tmp,XUR_PORT='18089',XUR_MODE='Installed',XUR_STATE=tmp,PATH=str(root/'bin')+':'+os.environ['PATH'],XUR_CONSOLE='stdio')
 process=subprocess.Popen([str(repo/'.build/context/publish/control/Xur.Control')],env=env,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
 def request(path,body=None,token=None):
  headers={'Content-Type':'application/json'}
  if token:headers['Authorization']='Bearer '+token
  try:
   with urllib.request.urlopen(urllib.request.Request('https://127.0.0.1:18452'+path,data=None if body is None else json.dumps(body).encode(),headers=headers),timeout=5,context=ssl._create_unverified_context()) as r:
    payload=r.read();return r.status,json.loads(payload) if payload else None
  except urllib.error.HTTPError as error:return error.code,None
 try:
  for _ in range(100):
   try:
    if request('/health')[0]==200:break
   except OSError:pass
   time.sleep(.1)
  else:raise AssertionError('Host did not start')
  c=http.client.HTTPConnection('localhost');c.sock=socket.socket(socket.AF_UNIX);c.sock.connect(str(root/'control.sock'));c.request('GET','/local/login');code=c.getresponse().read().decode().split('Access code: ')[1].splitlines()[0].strip();c.close()
  assert code.replace('-','')==fixed
  for path in ('/api/benchmarks','/api/model-lab/targets','/api/benchmarks/not-an-id/export'):
   assert request(path)[0]==401
  status,data=request('/api/bootstrap',{'token':code});assert status==200 and data['setupRequired'];setup=data['accessToken']
  password=secrets.token_urlsafe(24);account={'username':'owner','password':password}
  assert request('/api/auth/setup',account)[0]==401
  assert request('/api/disks',token=setup)[0]==403
  assert request('/api/auth/setup',{'username':'owner','password':'short'},setup)[0]==400
  with concurrent.futures.ThreadPoolExecutor(2) as pool:
   results=list(pool.map(lambda _:request('/api/auth/setup',account,setup),range(2)))
  assert sorted(r[0] for r in results) in ([200,401],[200,409])
  assert not (root/'bootstrap-token').exists()
  manager=next(r[1]['accessToken'] for r in results if r[0]==200)
  status,created=request('/api/api-keys',{'name':'Diagnostics assistant','scope':'diagnostics','days':30},manager)
  assert status==201
  key=created['token'];key_id=created['key']['id']
  assert request('/api/status',token=key)[0]==200
  assert request('/api/api-keys',token=key)[0]==403
  assert request('/api/power/reboot',{},key)[0]==403
  assert request('/api/files/download?user=owner&path=.ssh/id_rsa',token=key)[0]==403
  assert request('/api/api-keys',{'name':'Escalation'},key)[0]==403
  assert request('/api/api-keys',{'name':'Invalid scope','scope':'root'},manager)[0]==400
  assert request('/api/status',token=key[:-1]+('0' if key[-1]!='0' else '1'))[0]==401
  status,listed=request('/api/api-keys',token=manager)
  assert status==200 and listed[0]['requests']==1 and listed[0]['lastPath']=='/api/status'
  assert key not in json.dumps(listed) and key not in (root/'api-keys.json').read_text()
  assert request('/api/api-keys/'+key_id+'/revoke',{},manager)[0]==200
  assert request('/api/status',token=key)[0]==401
  assert request('/api/auth/setup',account,manager)[0]==409
  assert request('/api/bootstrap',{'token':code})[0]==401
  assert request('/api/auth/login',account)[0]==200
  assert request('/api/auth/login',{'username':'owner','password':'wrong'})[0]==401
  assert password not in (root/'manager-account.json').read_text()
 finally:
  os.killpg(process.pid,signal.SIGTERM);process.wait(timeout=5)
print(json.dumps({'suite':'AccountApi','result':'Passed','scopedBootstrap':True,'authenticatedSetup':True,'concurrentSetupHasSingleWinner':True,'passwordLogin':True,'tokenDisabledAfterSetup':True}))
