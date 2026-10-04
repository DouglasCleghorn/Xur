#!/usr/bin/env python3
"""Run a frozen previous updater against fixtures; all system commands are mocked."""
import importlib.machinery,importlib.util,json,pathlib,subprocess,sys

repo=pathlib.Path(__file__).resolve().parents[2]
root=pathlib.Path(sys.argv[1]);unit=pathlib.Path(sys.argv[2]);sdk=sys.argv[3];assembly=sys.argv[4]
loader=importlib.machinery.SourceFileLoader('old_updater',str(repo/'tests/Xur.Integration.Tests/fixtures/app-update-before-xurutil.py'))
spec=importlib.util.spec_from_loader(loader.name,loader);old=importlib.util.module_from_spec(spec);loader.exec_module(old)
old.ROOT=root/'app';old.ROOT.mkdir();old.TX=old.ROOT/'transaction.json';old.BLOCK=old.ROOT/'maintenance';old.STATE=old.ROOT/'update.json';old.OP='fixture'
previous='a'*64;new='b'*64
for identity in (previous,new):
    release=old.ROOT/'releases'/identity;release.mkdir(parents=True);(release/'bundle.json').write_text(json.dumps({'id':identity,'hostAbi':1}));(release/'host').mkdir()
(old.ROOT/'current').symlink_to('releases/'+previous)
legacy=unit.read_text();current=legacy.replace('After=local-fs.target xur-network.service','After=local-fs.target NetworkManager.service').replace('Requires=xur-network.service','Wants=xur-network.service')
old.local=lambda *args:{'active':0,'profileBusy':False,'busy':False}
events=[]
def call(argv,timeout=60):
    events.append(argv)
    if argv[:2]==['systemctl','start']:
        assert old.current()['id']==new
        result=subprocess.run([sdk,*([assembly] if assembly else []),'--migration-fixture',str(unit)],check=True,capture_output=True,text=True,timeout=30)
        assert json.loads(result.stdout)['result']=='migrated'
    return b''
old.call=call
old.healthy=lambda identity:identity==new and unit.read_text()==current
old.activate({'id':new})
assert old.current()['id']==new and old.read(old.STATE)['stage']=='Complete'
assert not any('migrate' in ' '.join(argv) for argv in events), 'Old updater must not need a new migration hook'
events.clear();old.call=lambda argv,timeout=60:events.append(argv) or b''
old.healthy=lambda identity:identity==previous
old.atomic(old.TX,{'previous':previous,'target':new,'operation':'fixture'});old.recover()
assert old.current()['id']==previous and unit.read_text()==current
assert all('daemon-reload' not in argv and 'xur-network' not in argv for argv in events)
print(json.dumps({'suite':'FirstUpgradeWithOldUpdater','result':'Passed','oldUpdaterUnchanged':True,'migrationBeforeHealth':True,'rollbackPreservesForwardRepair':True}))
