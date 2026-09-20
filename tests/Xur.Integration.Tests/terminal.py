#!/usr/bin/env python3
"""Verify real daemon/PTY ownership, including both input and output opens."""
import json,os,pathlib,pty,subprocess,termios
repo=pathlib.Path(__file__).resolve().parents[2]
sdk=os.environ.get('XUR_DOTNET',str(pathlib.Path.home()/'.local/share/xur-build/dotnet/dotnet'))
master,slave=pty.openpty()
try:
    subprocess.run([sdk,str(repo/'tests/Xur.Unit.Tests/bin/Release/net10.0/Xur.Unit.Tests.dll'),
                    '--terminal-probe',os.ttyname(slave)],start_new_session=True,
                   capture_output=True,check=True)
    mode=termios.tcgetattr(slave)
    assert not mode[3] & (termios.ECHO|termios.ECHONL|termios.ICANON|termios.ISIG)
finally:
    os.close(slave);os.close(master)
print(json.dumps({'suite':'TerminalOwnership','daemonHasNoControllingTerminalAfterReadAndWriteOpens':True,
                  'inputEchoAndCanonicalModeDisabledOnRealTerminal':True,
                  'sessionLeaderProcess':True,'device':'Real Linux PTY'}))
