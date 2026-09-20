#!/usr/bin/env python3
import json,pathlib,socket,sys
root=pathlib.Path(__file__).resolve().parents[2]/'.build/vms'/sys.argv[1]
sock=socket.socket(socket.AF_UNIX);sock.connect(str(root/'qmp.sock'));io=sock.makefile('rwb',buffering=0)
json.loads(io.readline())
def command(name,args=None):
    io.write((json.dumps({'execute':name,'arguments':args or {}})+'\n').encode())
    while True:
        result=json.loads(io.readline())
        if 'return' in result or 'error' in result:return result
command('qmp_capabilities')
print(json.dumps(command(sys.argv[2],json.loads(sys.argv[3]) if len(sys.argv)>3 else {})))
