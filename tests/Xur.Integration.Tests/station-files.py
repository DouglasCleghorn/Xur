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

# Archive contents, mutation boundaries and immediate-child size preloading.
import io,zipfile
with tempfile.TemporaryDirectory() as temp:
    root=pathlib.Path(temp)/'home';root.mkdir();folder=root/'folder';folder.mkdir();(folder/'child').mkdir()
    (folder/'child'/'payload.bin').write_bytes(payload);(folder/'empty').mkdir();(folder/'outside').symlink_to('/etc');os.mkfifo(folder/'fifo')
    def run(mode,path='',q=''):
        r=subprocess.run(['python3','-I','-c',script,mode,str(root),path,q],capture_output=True,timeout=20)
        header,_,body=r.stdout.partition(b'\n');return json.loads(header),body,r.returncode
    header,body,code=run('download','folder');assert code==0 and header['size'] is None
    with zipfile.ZipFile(io.BytesIO(body)) as z:
        assert z.read('folder/child/payload.bin')==payload
        assert 'folder/empty/' in z.namelist() and not any('outside' in n or 'fifo' in n for n in z.namelist())
        assert z.getinfo('folder/child/payload.bin').compress_type==zipfile.ZIP_DEFLATED
    (root/'archive.zip').write_bytes(body)
    header,stored,code=run('stored','archive.zip');assert code==0
    with zipfile.ZipFile(io.BytesIO(stored)) as z:
        assert z.read('folder/child/payload.bin')==payload and all(e.compress_type==zipfile.ZIP_STORED for e in z.infolist())
    header,compressed,code=run('compressed','archive.zip');assert code==0
    with zipfile.ZipFile(io.BytesIO(compressed)) as z:
        assert z.read('folder/child/payload.bin')==payload and all(e.compress_type==zipfile.ZIP_DEFLATED for e in z.infolist())
    data,_,code=run('size','folder');assert code==0 and set(data['sizes'])=={'folder','folder/child','folder/empty'}
    assert data['sizes']['folder/child']['bytes']>=len(payload) and 'entries' not in data
    # Listing is a new scan on every request.
    (root/'fresh').write_text('new');assert 'fresh' in [e['name'] for e in run('list')[0]['entries']]
    assert not run('delete','folder')[0]['ok'] and folder.exists()
    assert not run('move','folder','../escape')[0]['ok']
    assert not run('move','folder','fresh')[0]['ok'] and (root/'fresh').read_text()=='new'
    assert run('move','fresh','folder/renamed')[0]['ok'] and (folder/'renamed').read_text()=='new'
    assert not run('delete','','confirm')[0]['ok']
    assert not run('delete','folder/outside/passwd','confirm')[0]['ok']
    assert run('delete','folder','confirm')[0]['ok'] and not folder.exists()
    # A large archive must produce bytes before completion with bounded resident memory.
    big=root/'big';big.mkdir()
    with (big/'data').open('wb') as f:f.truncate(128*1024*1024)
    p=subprocess.Popen(['python3','-I','-c',script,'stored',str(root),'big',''],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    try:
        assert json.loads(p.stdout.readline())['ok'];assert p.stdout.read(65536).startswith(b'PK')
        rss=int(next(line for line in pathlib.Path(f'/proc/{p.pid}/status').read_text().splitlines() if line.startswith('VmRSS:')).split()[1])
        assert rss<50000,rss
        # The writer is still streaming, blocked by backpressure rather than buffering 128 MiB.
        assert p.poll() is None
    finally:p.terminate();p.communicate(timeout=5)
print(json.dumps({'suite':'FileOperations','result':'Passed','streamedZipAndStoredZip':True,'boundedMemoryAndBackpressure':True,'confirmedDeleteAndNoOverwriteMove':True,'sizePreloadWithoutFileLists':True}))
