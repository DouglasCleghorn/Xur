#!/usr/bin/env python3
"""Exercise monitor power commands through the real native frame client."""
import http.server,json,os,pathlib,select,socketserver,subprocess,tempfile,threading,time

repo=pathlib.Path(__file__).resolve().parents[2]
evidence=repo/'.build/evidence';evidence.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=evidence,prefix='console-power-') as directory:
    root=pathlib.Path(directory)
    binary=pathlib.Path(os.environ['XUR_CONSOLE_CLIENT']) if 'XUR_CONSOLE_CLIENT' in os.environ else root/'client'
    if 'XUR_CONSOLE_CLIENT' not in os.environ:
        flags=subprocess.check_output(['pkg-config','--cflags','--libs','libcurl'],text=True).split()
        subprocess.run(['cc','-O2','-Wall','-Wextra','-Werror',str(repo/'tools/Xur.Console/client.c'),'-o',str(binary),*flags],check=True)
    state={'power':'on','status':200,'row':'menu','truncate':False};reads=[]
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self,*args):pass
        def do_GET(self):
            current=dict(state);reads.append(time.monotonic())
            data=('\x1b[?25l\x1b[1;1H'+current['row']+'\x1b[2;1Hfooter').encode()
            self.send_response(current['status'])
            if current['power'] is not None:self.send_header('x-xur-console-power','\t'+current['power']+' ')
            self.send_header('Content-Length',str(len(data)+(1 if current['truncate'] else 0)))
            self.end_headers();self.wfile.write(data)
    class Server(socketserver.ThreadingUnixStreamServer):daemon_threads=True
    server=Server(str(root/'control.sock'),Handler)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    process=subprocess.Popen([str(binary),str(root/'control.sock')],stdout=subprocess.PIPE)
    captured=b'';on=b'\x1b]xurDpmsOn\x07';off=b'\x1b]xurDpmsOff\x07'
    def read_for(seconds):
        global captured
        deadline=time.monotonic()+seconds
        while time.monotonic()<deadline:
            if select.select([process.stdout],[],[],min(.05,max(0,deadline-time.monotonic())))[0]:
                captured+=os.read(process.stdout.fileno(),65536)
    def until(text):
        deadline=time.monotonic()+4
        while text not in captured and time.monotonic()<deadline:read_for(.05)
        assert text in captured,(text,captured)
    try:
        until(on);until(b'menu');captured=b''
        state.update(power='off',row='black fallback');until(off);until(b'black fallback')
        assert captured.index(b'black fallback')<captured.index(off),'Black fallback must precede sleep'
        captured=b'';read_for(.4);assert on not in captured and off not in captured,'Polling must not repeat power transitions'
        state.update(row='background update');until(b'background update');assert on not in captured,'Background changes must not wake the monitor'
        captured=b'';state.update(power='on',status=503);read_for(.35);assert on not in captured,'A failed frame request must preserve sleep'
        state.update(status=200,truncate=True);read_for(.35);assert on not in captured,'An incomplete response must preserve sleep'
        state.update(truncate=False,power=None);read_for(.35);assert on not in captured,'A missing power header must preserve sleep'
        state.update(power='invalid');read_for(.35);assert on not in captured,'An invalid power header must preserve sleep'
        captured=b'';state.update(power='on');until(on);until(b'footer')
        assert captured.index(on)<captured.index(b'footer'),'Wake must precede a complete menu repaint'
        captured=b'';read_for(7.5)
        assert captured.count(on)==3,'Wake retries must be bounded to one, three and seven seconds'
        assert captured.count(b'footer')==3,'Each wake retry must repaint the entire menu'
        assert b'\x1b]xurDisplayCheck\x07' in captured,'Awake clients must check renderer progress'
        captured=b'';read_for(1.2);assert on not in captured,'Wake retries must stop after the recovery window'
        captured=b'';state.update(power='off');until(off)
        captured=b'';read_for(1.2)
        assert on not in captured and b'\x1b]xurDisplayCheck\x07' not in captured,'Sleep must cancel retries and progress checks'
        assert len(reads)>10
    finally:
        process.terminate();process.wait(timeout=5);server.shutdown();server.server_close()
print(json.dumps({'suite':'ConsolePower','sleepAndWake':True,'pollingPreservesSleep':True,'failedResponsesPreserveSleep':True,'fullWakeRepaint':True,'boundedWakeRetries':True,'rendererProgressChecks':True}))
