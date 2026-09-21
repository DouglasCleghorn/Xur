#!/usr/bin/env python3
"""Exercise self-contained signed descriptors over HTTP without root activation."""
import functools,hashlib,http.server,importlib.machinery,importlib.util,json,pathlib,subprocess,tempfile,threading,urllib.error
repo=pathlib.Path(__file__).resolve().parents[2]
def load(name,path):
 loader=importlib.machinery.SourceFileLoader(name,str(path));spec=importlib.util.spec_from_loader(name,loader);module=importlib.util.module_from_spec(spec);loader.exec_module(module);return module
u=load('compact_updater',repo/'os/bootc/app-update');publisher=load('compact_publisher',repo/'eng/update-repository.py');verifier=load('compact_verifier',repo/'eng/verify-release.py')
with tempfile.TemporaryDirectory() as directory:
 root=pathlib.Path(directory);public=root/'public';public.mkdir();u.ROOT=root/'state';u.ROOT.mkdir();u.CONFIG=root/'config';u.KEY=root/'key.pub'
 key=root/'key';subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(key)],check=True,capture_output=True)
 u.KEY.write_bytes(subprocess.check_output(['openssl','pkey','-in',str(key),'-pubout']))
 identity='a'*64;payload=public/(identity+'.tar.gz');payload.write_bytes(b'payload'*200000);original_payload=payload.read_bytes()
 entry=dict(schema=1,hostAbi=1,dataSchema=1,id=identity,version='1.0',sequence=10,channel='development',file=payload.name,bytes=payload.stat().st_size,sha256=hashlib.file_digest(payload.open('rb'),'sha256').hexdigest())
 iso=root/'fixture.iso';iso.write_bytes(b'iso')
 descriptor=publisher.compact(public,entry,key,{'iso':{'sha256':hashlib.sha256(b'iso').hexdigest(),'bytes':3}});original=descriptor.read_bytes();(public/'current').write_text(identity)
 assert verifier.verify(descriptor,u.KEY,payload,iso)['version']=='1.0'
 iso.write_bytes(b'bad')
 try:verifier.verify(descriptor,u.KEY,payload,iso)
 except ValueError:pass
 else:raise AssertionError('Corrupt ISO accepted')
 requests=[]
 class Handler(http.server.SimpleHTTPRequestHandler):
  def log_message(self,*args):pass
  def do_GET(self):requests.append(self.path);super().do_GET()
 server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Handler,directory=public));thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
 try:
  u.select_channel('local','http://127.0.0.1:'+str(server.server_port),u.KEY.read_text())
  stage=root/'stage';stage.mkdir();checked=u.check(stage)
  assert checked['schema']==2 and checked['installer']['iso']['bytes']==3
  assert descriptor.stat().st_size<2048
  assert requests==['/current','/'+identity+'.update.json'],'Check downloaded payload'
  assert u.download_bundle(stage,checked).read_bytes()==original_payload
  assert requests[-1]=='/'+payload.name
  # Tampering, bounded metadata, truncation, and payload integrity.
  altered=json.loads(original);altered['release']['sha256']='c'*64
  for changed in [json.dumps(altered).encode(),b'{}',b'x'*65537,original[:20]]:
   descriptor.write_bytes(changed)
   try:u.check(stage)
   except (ValueError,subprocess.CalledProcessError):pass
   else:raise AssertionError('Malformed or unsigned descriptor accepted')
  payload.write_bytes(original_payload[:-1]+b'!')
  try:u.download_bundle(stage,checked)
  except ValueError as error:assert 'hash mismatch' in str(error)
  else:raise AssertionError('Corrupt payload accepted')
  payload.write_bytes(original_payload+b'!')
  try:u.download_bundle(stage,checked)
  except ValueError as error:assert 'allowed size' in str(error)
  else:raise AssertionError('Oversized payload accepted')
  descriptor.write_bytes(original);u.remember_sequence({**checked,'sequence':20},u.sequence_scope())
  try:u.check(stage)
  except ValueError as error:assert 'older release' in str(error)
  else:raise AssertionError('Replay accepted')
  # Invalid descriptors do not silently fall back to legacy trust/discovery.
  descriptor.write_bytes(b'{}');u.check_legacy=lambda stage:(_ for _ in ()).throw(AssertionError('Downgrade fallback'))
  try:u.check(stage)
  except ValueError:pass
  else:raise AssertionError('Invalid compact descriptor accepted')
 finally:server.shutdown();server.server_close();thread.join()
 # Both official channels resolve once to immutable metadata and payload.
 for channel,tag in [('nightly','nightly-1.0'),('stable','v1.0')]:
  u.select_channel(channel);entry['channel']=channel;entry['sequence']=30;descriptor=publisher.compact(public,entry,key,archive_name=u.ARCHIVE)
  calls=[]
  def fetch(url,path,limit):
   calls.append(url)
   if url==u.GITHUB+'/download/'+channel+'/current':path.write_text(tag)
   elif url==u.GITHUB+'/download/'+tag+'/'+u.DESCRIPTOR:path.write_bytes(descriptor.read_bytes())
   elif url==u.GITHUB+'/download/'+tag+'/'+u.ARCHIVE:path.write_bytes(original_payload)
   else:raise AssertionError(url)
  u.fetch=fetch;selected=u.check(stage);assert selected['channel']==channel;assert len(calls)==2
  # A pointer change after check cannot redirect the app download.
  assert u.download_bundle(stage,selected).read_bytes()==original_payload
  entry['channel']='development';publisher.compact(public,entry,key)
  try:u.check(stage)
  except ValueError as error:assert 'selected channel' in str(error)
  else:raise AssertionError('Wrong channel accepted')
 with tempfile.TemporaryDirectory() as fallback_directory:
  stage=pathlib.Path(fallback_directory);u.check_legacy=lambda stage:'legacy';u.select_channel('stable')
  for status in [404,403,500]:
   def fail(url,path,limit):raise urllib.error.HTTPError(url,status,'fixture',{},None)
   u.fetch=fail
   if status==404:assert u.check(stage)=='legacy'
   else:
    try:u.check(stage)
    except urllib.error.HTTPError as error:assert error.code==status
    else:raise AssertionError('HTTP failure silently fell back to legacy')
print(json.dumps({'suite':'CompactUpdate','result':'Passed','checkBytesUnder':2048,'payloadOnlyOnUpdate':True,'signatureBoundsReplayAndRace':True,'bothChannelsPinned':True,'isoVerification':True}))
