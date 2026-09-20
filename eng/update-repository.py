#!/usr/bin/env python3
"""Publish signed Xur bundles or serve the public repository on all interfaces."""
import argparse,fcntl,functools,hashlib,http.server,json,os,pathlib,socket,subprocess,tarfile,time
ROOT=pathlib.Path(__file__).resolve().parents[1]
PUBLIC=ROOT/'.build/update-repository'
KEY=pathlib.Path(os.environ.get('XUR_UPDATE_SIGNING_KEY',pathlib.Path.home()/'.local/share/xur-updates/signing-key.pem'))
PUB=ROOT/'os/bootc/application-update-key.pem'
def keys():
    KEY.parent.mkdir(parents=True,exist_ok=True);KEY.parent.chmod(0o700)
    if not KEY.exists():
        subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(KEY)],check=True);KEY.chmod(0o600)
    public=subprocess.check_output(['openssl','pkey','-in',str(KEY),'-pubout'])
    if PUB.exists() and PUB.read_bytes()!=public:raise RuntimeError('Signing key differs from the trusted ISO key')
    PUB.write_bytes(public)
def publish(version):
    context_lock=(ROOT/".build/context.lock").open("w");fcntl.flock(context_lock,fcntl.LOCK_SH)
    keys();PUBLIC.mkdir(parents=True,exist_ok=True)
    bundle=ROOT/'.build/context/rootfs/usr/share/xur/app-bundle'
    meta=json.loads((bundle/'bundle.json').read_text())
    for name,digest in meta['files'].items():assert hashlib.file_digest((bundle/name).open('rb'),'sha256').hexdigest()==digest,name
    archive=PUBLIC/(meta['id']+'.tar.gz');temp=archive.with_suffix('.partial')
    with tarfile.open(temp,'w:gz',dereference=True,compresslevel=1) as tar:tar.add(bundle,arcname='.')
    temp.replace(archive)
    entry={'schema':1,'hostAbi':1,'dataSchema':1,'id':meta['id'],'version':version,'sequence':int(time.time()),'file':archive.name,'bytes':archive.stat().st_size,'sha256':hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()}
    # Versioned descriptors avoid a manifest/signature race during publication.
    descriptor=PUBLIC/(meta['id']+'.json');descriptor.write_text(json.dumps(entry,sort_keys=True)+'\n')
    subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(KEY),'-rawin','-in',str(descriptor),'-out',str(descriptor)+'.sig'],check=True)
    tmp=PUBLIC/'latest.tmp';tmp.write_text(meta['id']);tmp.replace(PUBLIC/'latest')
    (PUBLIC/'application-update-key.pem').write_bytes(PUB.read_bytes())
    print(json.dumps(entry))
class Handler(http.server.SimpleHTTPRequestHandler):
    def list_directory(self,path):self.send_error(404);return None
    def log_message(self,*args):pass
    def end_headers(self):self.send_header('Cache-Control','no-store');super().end_headers()
class Server(http.server.ThreadingHTTPServer):
    address_family=socket.AF_INET6
    def server_bind(self):self.socket.setsockopt(socket.IPPROTO_IPV6,socket.IPV6_V6ONLY,0);super().server_bind()
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('action',choices=['keys','publish','serve']);p.add_argument('--version',default=time.strftime('%Y.%m.%d.%H%M',time.gmtime()));p.add_argument('--port',type=int,default=8088);a=p.parse_args()
    if a.action=='keys':keys()
    elif a.action=='publish':publish(a.version)
    else:
        PUBLIC.mkdir(parents=True,exist_ok=True)
        with Server(('::',a.port),functools.partial(Handler,directory=PUBLIC)) as server:
            print(f'Xur update repository listening on IPv4 and IPv6 port {a.port}',flush=True);server.serve_forever()
