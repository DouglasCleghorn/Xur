#!/usr/bin/env python3
"""Run boot selection and installer profile handoff with isolated command fixtures."""
import json,os,pathlib,subprocess,tempfile
repo=pathlib.Path(__file__).resolve().parents[2]
root=repo/'.build/evidence';root.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=root,prefix='network-startup-') as directory:
    temp=pathlib.Path(directory);bin_dir=temp/'bin';bin_dir.mkdir();log=temp/'calls.jsonl'
    static='00000000-0000-4000-8000-000000000010';pending='00000000-0000-4000-8000-000000000020';dhcp='00000000-0000-4000-8000-000000000030'
    fixture=bin_dir/'nmcli'
    fixture.write_text('''#!/usr/bin/python3
import json,os,sys
a=sys.argv[1:];text=' '.join(a)
with open(os.environ['NETWORK_TEST_LOG'],'a') as f:f.write(json.dumps(a)+'\\n')
static,pending,dhcp=os.environ['NETWORK_TEST_UUIDS'].split(',')
if 'DEVICE,TYPE' in a:print('eno1:ethernet\\ntailscale0:tun')
elif 'GENERAL.CON-UUID' in a:print(static if os.environ['NETWORK_TEST_MODE']=='active' else '--')
elif 'CONNECTIONS.AVAILABLE-CONNECTIONS' in a:
 if os.environ['NETWORK_TEST_MODE']!='empty':print(pending+' | pending,'+dhcp+' | DHCP,'+static+' | static')
elif 'connection.autoconnect' in a and 'show' in a:print('no' if a[-1]==pending else 'yes')
elif 'connection.autoconnect-priority' in a:print(999 if a[-1] in (pending,static) else -999)
elif 'connection.timestamp' in a:print(100)
elif text=='connection show xur-dhcp-eno1':sys.exit(10)
''');fixture.chmod(0o755)
    env=dict(os.environ,PATH=str(bin_dir)+':'+os.environ['PATH'],NETWORK_TEST_LOG=str(log),NETWORK_TEST_UUIDS=','.join([static,pending,dhcp]))
    def run(mode):
        log.write_text('');subprocess.run(['bash',str(repo/'os/bootc/xur-network')],env=env|{'NETWORK_TEST_MODE':mode},check=True)
        return [json.loads(line) for line in log.read_text().splitlines()]
    calls=run('saved');ups=[c for c in calls if 'up' in c]
    assert len(ups)==1 and static in ups[0] and all(pending not in c for c in ups)
    assert not any('add' in c for c in calls),'A saved static profile must not be replaced with DHCP'
    calls=run('active');assert not any('up' in c or 'add' in c for c in calls),'Boot must preserve an active static profile'
    calls=run('empty');assert any('add' in c and 'auto' in c for c in calls),'An unconfigured adapter still gets DHCP'
    # Exercise the actual handoff commands on disposable paths, with no chroot or host edits.
    source=temp/'source';source.mkdir();target=temp/'target';target.mkdir()
    profile=source/'static.nmconnection';profile.write_text('[connection]\nid=static\nautoconnect=true\n[ipv4]\nmethod=manual\naddress1=192.0.2.10/24\n');profile.chmod(0o644)
    script=(repo/'os/installer/install-manager').read_text().split('# Copy saved NetworkManager profiles,',1)[1].split('mkdir -p "$target/etc/systemd/system.conf.d"',1)[0]
    script=script[script.index('mkdir -p'):].replace('chroot "$target" restorecon -RF /etc/NetworkManager','true')
    script=script.replace('/etc/NetworkManager/system-connections/.',str(source)+'/.').replace('if test -d /etc/NetworkManager/system-connections;',f'if test -d "{source}";')
    subprocess.run(['bash','-euc','target="$1"\n'+script,'test',str(target)],check=True)
    saved=target/'etc/NetworkManager/system-connections/static.nmconnection'
    assert saved.read_bytes()==profile.read_bytes() and saved.stat().st_mode&0o777==0o600
print(json.dumps({'suite':'NetworkStartup','savedStaticPreserved':True,'unconfirmedCandidateExcluded':True,'dhcpFallback':True,'installerProfileHandoff':True}))
