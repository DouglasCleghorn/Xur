#!/usr/bin/env python3
"""Fail closed if build-context inputs differ from the completed publish."""
import hashlib,json,pathlib,sys
context=pathlib.Path(sys.argv[2]).resolve()
receipt=context/'publish-receipt.json'
def observe():
    return {str(p.relative_to(context)):hashlib.file_digest(p.open('rb'),'sha256').hexdigest()
            for p in sorted(context.rglob('*')) if p.is_file() and p!=receipt}
if sys.argv[1]=='create':
    for p in ('rootfs/usr/lib/xur/control/Xur.Control.dll','rootfs/usr/lib/xur/agent/Xur.Agent.dll','rootfs/usr/lib/xur/gateway/Xur.Gateway.dll','installer-rootfs/usr/share/xur/install-template.ks'):
        assert (context/p).is_file(),p
    receipt.write_text(json.dumps({'schema':1,'files':observe()},indent=2)+'\n')
elif sys.argv[1]=='verify':
    assert receipt.is_file(),'No completed publish receipt'
    assert json.loads(receipt.read_text())['files']==observe(),'Build context differs from completed publish'
else:raise SystemExit('Use create or verify')
