#!/usr/bin/env python3
"""Exercise streaming restart policy with disposable processes in the local user manager."""
import json,pathlib,re,subprocess,time,uuid
root=pathlib.Path(__file__).resolve().parents[2]
source=(root/'src/Xur.Agent/StationStreaming.cs').read_text()
policy=re.search(r'public static string\[\] RecoveryPolicy\(\)=>\[(.*?)\];',source,re.S).group(1)
properties=re.findall(r'"(--property=[^"]+)"',policy)
assert properties and '..RecoveryPolicy()' in source
results=[]
for mode in ('segv','exit'):
 unit='xur-stream-policy-test-'+uuid.uuid4().hex+'.service'
 script="import ctypes,os,signal;ctypes.CDLL(None).prctl(4,0,0,0,0);os.kill(os.getpid(),signal.SIGSEGV)" if mode=='segv' else 'raise SystemExit(42)'
 try:
  subprocess.run(['systemd-run','--user','--quiet','--unit='+unit,'--property=LimitCORE=0',*properties,'/usr/bin/python3','-c',script],check=True)
  deadline=time.monotonic()+30
  while time.monotonic()<deadline:
   text=subprocess.check_output(['systemctl','--user','show',unit,'--property=ActiveState,Result,NRestarts'],text=True)
   state=dict(line.split('=',1) for line in text.splitlines())
   if state['ActiveState']=='failed':break
   time.sleep(.25)
  else:raise AssertionError('Restart policy did not reach a failed state')
  if mode=='segv':
   time.sleep(6) # longer than RestartSec, without permitting a crash loop
   state=dict(line.split('=',1) for line in subprocess.check_output(['systemctl','--user','show',unit,'--property=ActiveState,Result,NRestarts'],text=True).splitlines())
   assert state['ActiveState']=='failed' and state['NRestarts']=='0' and state['Result'] in ('signal','core-dump'),state
  else:
   assert state['Result'] in ('start-limit-hit','exit-code') and 1<=int(state['NRestarts'])<=3,state
   time.sleep(6)
   after=dict(line.split('=',1) for line in subprocess.check_output(['systemctl','--user','show',unit,'--property=ActiveState,Result,NRestarts'],text=True).splitlines())
   assert after==state,'Failed service restarted after the configured limit'
  results.append({'process':mode,**state})
 finally:
  subprocess.run(['systemctl','--user','stop',unit],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  subprocess.run(['systemctl','--user','reset-failed',unit],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
print(json.dumps({'suite':'StreamingRecovery','result':'Passed','observations':results}))
