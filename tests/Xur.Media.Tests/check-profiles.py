#!/usr/bin/env python3
"""Exercise installed profile API, a real pinned model, continuity and restart."""
import argparse,json,pathlib,re,threading,time,urllib.request,urllib.error
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name;base='http://127.0.0.1:18081'
def call(path,data=None,auth=True,timeout=15):
 headers={'Content-Type':'application/json'}
 if auth:headers['Authorization']='Bearer '+token
 req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers=headers)
 try:
  with urllib.request.urlopen(req,timeout=timeout) as r:return r.status,r.read().decode()
 except urllib.error.HTTPError as e:return e.code,e.read().decode()
from test_auth import api_session
token=api_session(vm)
assert call('/api/profiles',auth=False)[0]==401
assert call('/api/profiles',{'id':'bad'},False)[0]==401
recipe=next(r for r in json.loads(call('/api/recipes')[1]) if r['id']=='smollm2-135m-cpu')
chat={'id':'chat','name':'Chat','recipe':recipe,'gpus':[],'route':'chat'}
aux={**chat,'id':'aux','name':'Second model','route':'aux'}
def save(id,name,workloads,revision=0):
 status,body=call('/api/profiles',{'id':id,'name':name,'revision':revision,'workloads':workloads});assert status==200,(status,body);return json.loads(body)
def state():
 status,body=call('/api/profiles');assert status==200,(status,body);return json.loads(body)
def apply(id):
 status,body=call('/api/profiles/'+id+'/preview',{});assert status==200,(status,body);plan=json.loads(body)
 assert call('/api/profiles/apply',{'id':plan['id'],'digest':'wrong'})[0]==409
 status,body=call('/api/profiles/apply',{'id':plan['id'],'digest':plan['digest']});assert status==200,(status,body)
 for n in range(900):
  s=state();op=s['operation']
  if op['stage']=='Complete':return s
  assert op['stage']!='Failed',op
  if n%15==0:print('Profile '+id+': '+str(op['completed'])+'/'+str(op['total']),flush=True)
  assert call('/health',auth=False)[0]==200
  time.sleep(1)
 raise AssertionError('Profile change timed out')
save('chat-only','Chat',[chat]);save('two-models','Two models',[chat,aux]);save('idle','Idle',[])
s=apply('chat-only');original=s['runtime']['instances'][0];assert original['pid']>0 and original['state']=='running'
request={'model':'smollm2','messages':[{'role':'user','content':'What is the capital of France?'}],'max_tokens':24,'temperature':0}
status,body=call('/inference/chat/v1/chat/completions',request,timeout=60);assert status==200,body
assert json.loads(body)['choices'][0]['message']['content'].strip()
stream_started=threading.Event();stream_done=threading.Event();stream_errors=[];chunks=[]
def streaming():
 try:
  req=urllib.request.Request(base+'/inference/chat/completion',data=json.dumps({'prompt':'Once upon a time','n_predict':1024,'ignore_eos':True,'stream':True,'temperature':0.5}).encode(),headers={'Content-Type':'application/json','Authorization':'Bearer '+token})
  with urllib.request.urlopen(req,timeout=120) as response:
   for line in response:
    if line.startswith(b'data: '):
     chunks.append(json.loads(line[6:]));stream_started.set()
 except Exception as e:stream_errors.append(type(e).__name__)
 finally:stream_done.set()
thread=threading.Thread(target=streaming,daemon=True);thread.start();assert stream_started.wait(30),'No streamed model output'
assert not stream_done.is_set(),'Stream finished before transition'
for target in ['two-models','chat-only']:
 s=apply(target);current=next(i for i in s['runtime']['instances'] if i['id']=='chat')
 assert all(current[k]==original[k] for k in ['instanceId','pid','bootId','fingerprint','endpoint']),current
assert stream_done.wait(120),'Stream did not complete';assert not stream_errors,stream_errors
assert len(chunks)>10 and chunks[-1].get('stop') is True,'SSE response ended without the engine completion record'
status,body=call('/inference/chat/v1/chat/completions',request,timeout=60);assert status==200
# Check saved revisions survive a host reboot and Podman restarts the selected set.
boot=json.loads(call('/api/status',auth=False)[1])['bootId'];assert call('/api/power/reboot',{})[0]==202
for _ in range(180):
 try:
  if json.loads(call('/api/status',auth=False)[1])['bootId']!=boot:
   s=state()
   if s['active']['id']=='chat-only' and any(i['id']=='chat' and i['state']=='running' for i in s['runtime']['instances']):break
 except (OSError,ValueError):pass
 time.sleep(2)
else:raise AssertionError('Saved profile did not restart after boot')
for _ in range(90):
 try:
  status,body=call('/inference/chat/v1/chat/completions',request,timeout=15)
  if status==200:break
 except OSError:pass
 time.sleep(1)
else:raise AssertionError('Inference route did not recover after reboot')
s=apply('idle');assert not s['runtime']['instances']
receipt={'suite':'InstalledProfiles','result':'Passed','browserApiAuthentication':True,'realPinnedModelInference':True,'profileSwitch':True,'unchangedPid':original['pid'],'unchangedContainer':original['instanceId'],'activeModelStreamSurvives':True,'streamChunks':len(chunks),'savedProfileRestartsAfterBoot':True,'jwtSurvivesSecondReboot':True,'allWorkloadsStop':True,'media':json.loads((vm/'vm-manifest.json').read_text())}
(repo/'.build/evidence/profile-media.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
