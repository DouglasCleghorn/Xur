#!/usr/bin/env python3
"""Run commands only through the guest agent of a named disposable test VM."""
import argparse,base64,fcntl,json,pathlib,secrets,socket,time

def execute(name,command):
    if not name or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789-' for c in name): raise ValueError('Invalid VM name')
    path=pathlib.Path(__file__).resolve().parents[2]/'.build/vms'/name/'qga.sock'
    with path.with_suffix('.lock').open('a') as lock, socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as sock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        sock.settimeout(30);sock.connect(str(path));stream=sock.makefile('rwb',buffering=0)
        nonce=secrets.randbits(52)
        stream.write(b'\xff'+json.dumps({'execute':'guest-sync-delimited','arguments':{'id':nonce}}).encode()+b'\n')
        while True:
            line=stream.readline()
            if not line:raise ConnectionError('Guest agent disconnected')
            try:
                if json.loads(line.rsplit(b'\xff',1)[-1]).get('return')==nonce:break
            except ValueError:pass
        def call(execute,arguments=None):
            stream.write(json.dumps(dict(execute=execute,arguments=arguments or {})).encode()+b'\n')
            line=stream.readline()
            if not line:raise ConnectionError('Guest agent disconnected')
            result=json.loads(line)
            if 'error' in result:raise RuntimeError(result['error'])
            return result['return']
        pid=call('guest-exec',{'path':command[0],'arg':command[1:],'capture-output':True})['pid']
        while True:
            result=call('guest-exec-status',{'pid':pid})
            if result.get('exited'):break
            time.sleep(.2)
        return dict(code=result.get('exitcode',-1),output=base64.b64decode(result.get('out-data','')).decode(errors='replace'),error=base64.b64decode(result.get('err-data','')).decode(errors='replace'))
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('name');p.add_argument('command',nargs=argparse.REMAINDER);a=p.parse_args()
    print(json.dumps(execute(a.name,a.command)))
