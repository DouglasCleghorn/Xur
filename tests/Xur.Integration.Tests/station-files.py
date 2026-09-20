import json,os,pathlib,subprocess,tempfile
script=pathlib.Path('src/Xur.Agent/StationFiles.py').read_text()
with tempfile.TemporaryDirectory() as temp:
    root=pathlib.Path(temp)/'home';root.mkdir()
    (root/'.hidden').mkdir();payload=bytes(range(256))*513
    (root/'.hidden'/'steam-测试.log').write_bytes(payload)
    (root/'link').symlink_to('/etc');(root/'filelink').symlink_to('/etc/passwd');os.mkfifo(root/'fifo')
    def run(mode,path='',q='',home=root):
        r=subprocess.run(['python3','-I','-c',script,mode,str(home),path,q],capture_output=True,timeout=5)
        header,_,body=r.stdout.partition(b'\n');return json.loads(header),body,r.returncode
    listing,_,code=run('list');assert code==0 and listing['entries'][0]['name']=='.hidden'
    result,data,code=run('download','.hidden/steam-测试.log');assert code==0 and result['size']==len(payload) and data==payload
    for path in ['../etc/passwd','/etc/passwd','link/passwd','filelink','fifo','.hidden/../filelink','./filelink','']:
        result,body,code=run('download',path);assert code!=0 and not result['ok'] and not body,path
    alias=pathlib.Path(temp)/'alias';alias.symlink_to(root)
    assert not run('list',home=alias)[0]['ok']
    for n in range(502):(root/f'log-{n:04}').write_text('x')
    result,_,_=run('list');assert result['truncated'] and len(result['entries'])==500
    result,_,_=run('list',q='LOG-0501');assert [e['name'] for e in result['entries']]==['log-0501']
print(json.dumps({'suite':'StationFiles','result':'Passed','binaryDownload':True,'symlinkTraversalAndSpecialFilesRejected':True,'hiddenFilesAndBoundedListing':True}))
