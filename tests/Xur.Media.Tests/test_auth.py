"""Private credentials for disposable test VMs; supports old and new app bundles."""
import ssl,json,os,pathlib,re,secrets,urllib.request,urllib.error

def credentials(vm):
 path=pathlib.Path(vm)/'account.private.json'
 if path.exists():return json.loads(path.read_text())
 value={'username':'owner','password':secrets.token_urlsafe(24)}
 fd=os.open(path,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
 with os.fdopen(fd,'w') as f:json.dump(value,f)
 return value

def console_code(vm):
 codes=re.findall(r'Access code: ([0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3})',(pathlib.Path(vm)/'console.private.log').read_text(errors='replace'))
 if not codes:raise AssertionError('No test console access code')
 return codes[-1]

def api_session(vm,code=None,base='https://127.0.0.1:18443'):
 def post(path,body,token=None):
  headers={'Content-Type':'application/json'}
  if token:headers['Authorization']='Bearer '+token
  with urllib.request.urlopen(urllib.request.Request(base+path,data=json.dumps(body).encode(),headers=headers),timeout=15,context=ssl._create_unverified_context()) as response:return json.load(response)
 if (pathlib.Path(vm)/'account.private.json').exists():
  try:return post('/api/auth/login',credentials(vm))['accessToken']
  except urllib.error.HTTPError as e:
   if e.code not in (401,404):raise
 result=post('/api/bootstrap',{'token':code or console_code(vm)})
 if result.get('setupRequired'):result=post('/api/auth/setup',credentials(vm),result['accessToken'])
 return result['accessToken']

def browser_login(vm,request,code=None):
 def csrf(body):return re.search(r'name="__RequestVerificationToken" value="([^"]+)"',body).group(1)
 status,body=request('/login');assert status==200
 if 'Create your account' not in body:
  fields=credentials(vm) if 'name="password"' in body else {'code':code or console_code(vm),'user':'xur'}
  status,body=request('/auth/login',fields|{'__RequestVerificationToken':csrf(body)});assert status==200
 if 'Create your account' in body:
  status,body=request('/auth/setup',credentials(vm)|{'__RequestVerificationToken':csrf(body)});assert status==200
 assert 'name="code"' not in body and 'name="password"' not in body,'Test login did not reach the manager'
