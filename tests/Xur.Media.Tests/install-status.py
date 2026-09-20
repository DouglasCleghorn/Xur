#!/usr/bin/env python3
"""Print only authenticated structured status and agent-redacted logs."""
import argparse,http.cookiejar,json,pathlib,urllib.request
p=argparse.ArgumentParser();p.add_argument('--name',default='install');p.add_argument('--logs',action='store_true');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2]
j=http.cookiejar.LWPCookieJar(str(repo/'.build/vms'/a.name/'session.private.cookies'));j.load(ignore_discard=True,ignore_expires=True)
o=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(j))
status=json.load(o.open('http://127.0.0.1:18081/api/installer',timeout=10))
print(json.dumps({'installer':status['installer'],'operation':status['operation']}))
if a.logs:print(o.open('http://127.0.0.1:18081/api/logs',timeout=10).read().decode())
