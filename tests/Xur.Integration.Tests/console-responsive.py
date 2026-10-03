#!/usr/bin/env python3
"""Exercise the real DRM transport on a private socket and hidden input on a PTY."""
import http.server,json,os,pathlib,pty,select,socketserver,subprocess,tempfile,threading,time,termios
repo=pathlib.Path(__file__).resolve().parents[2];evidence=repo/'.build/evidence';evidence.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=evidence,prefix='console-ui-') as directory:
    root=pathlib.Path(directory);binary=pathlib.Path(os.environ['XUR_CONSOLE_CLIENT']) if 'XUR_CONSOLE_CLIENT' in os.environ else root/'client'
    if 'XUR_CONSOLE_CLIENT' not in os.environ:
        flags=subprocess.check_output(['pkg-config','--cflags','--libs','libcurl'],text=True).split()
        subprocess.run(['cc','-O2','-Wall','-Wextra','-Werror',str(repo/'tools/Xur.Console/client.c'),'-o',str(binary),*flags],check=True)
    changed=False;idle=False;wake_label='selected new';reads=[]
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_GET(self):
            reads.append(time.monotonic())
            lines=[' '*20]*3 if idle else ['unchanged row',wake_label if changed else 'selected old','unchanged footer']
            data=('\x1b[H'+''.join(f'\x1b[{row};1H{text.ljust(20)}' for row,text in enumerate(lines,1))).encode()
            self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
    class Server(socketserver.ThreadingUnixStreamServer):daemon_threads=True
    server=Server(str(root/'control.sock'),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
    process=subprocess.Popen([str(binary),str(root/'control.sock')],stdout=subprocess.PIPE)
    captured=b''
    def until(text):
        global captured
        deadline=time.monotonic()+4
        while text not in captured and time.monotonic()<deadline:
            if select.select([process.stdout],[],[],.1)[0]:captured+=os.read(process.stdout.fileno(),65536)
        assert text in captured,captured
    try:
        until(b'selected old');captured=b'';changed=True;start=time.monotonic();until(b'selected new');elapsed=time.monotonic()-start
        assert b'unchanged row' not in captured and b'unchanged footer' not in captured,'Unchanged rows must not repaint'
        assert elapsed<.45,elapsed
        time.sleep(.35);assert len(reads)>=4 and min(b-a for a,b in zip(reads,reads[1:]))<.2
        captured=b'';idle=True;until(b'\x1b[3;1H')
        assert b'selected' not in captured and b'unchanged' not in captured,'Idle transport must replace every visible row with black space'
        captured=b'';wake_label='selected awake';before=len(reads);time.sleep(.35)
        assert len(reads)>before,'The native client must continue polling while idle'
        assert not select.select([process.stdout],[],[],.1)[0],'Background content changes must not repaint the unchanged black frame'
        idle=False;until(b'selected awake')
        assert b'selected old' not in captured and b'selected new' not in captured,'Wake must display the latest complete frame'
    finally:process.terminate();process.wait(timeout=5);server.shutdown();server.server_close()
    sdk=os.environ.get('XUR_DOTNET',str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet'))
    for mode in ['password','cancel']:
        master,slave=pty.openpty()
        process=subprocess.Popen([sdk,str(repo/'tests/Xur.Unit.Tests/bin/Release/net10.0/Xur.Unit.Tests.dll'),'--password-probe',mode],stdin=slave,stdout=slave,stderr=slave)
        captured=b''
        try:
            deadline=time.monotonic()+8
            while b'Ready' not in captured and time.monotonic()<deadline:
                if select.select([master],[],[],.1)[0]:captured+=os.read(master,65536)
            assert b'Ready' in captured
            while termios.tcgetattr(slave)[3]&termios.ECHO and time.monotonic()<deadline:time.sleep(.01)
            os.write(master,b'  correct horseX\x7f \r' if mode=='password' else b'\x1b')
            while b'Matched' not in captured and time.monotonic()<deadline:
                if select.select([master],[],[],.1)[0]:captured+=os.read(master,65536)
            assert b'Matched' in captured and b'correct' not in captured,captured
            assert process.wait(timeout=3)==0
        finally:
            if process.poll() is None:process.kill();process.wait()
            os.close(master);os.close(slave)
print(json.dumps({'suite':'ConsoleResponsive','changedRowsOnly':True,'idleRowsCleared':True,'backgroundIdlePollingWithoutRepaint':True,'wakeShowsLatestFrame':True,'refreshMilliseconds':round(elapsed*1000),'hiddenPasswordAndCancel':True}))
