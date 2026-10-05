#!/usr/bin/env python3
"""Run boot selection and installer profile handoff with isolated command fixtures."""
import json,os,pathlib,subprocess,tempfile
repo=pathlib.Path(__file__).resolve().parents[2]
root=repo/'.build/evidence';root.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory(dir=root,prefix='network-startup-') as directory:
    temp=pathlib.Path(directory);bin_dir=temp/'bin';bin_dir.mkdir();log=temp/'calls.jsonl'
    static='00000000-0000-4000-8000-000000000010';pending='00000000-0000-4000-8000-000000000020';dhcp='00000000-0000-4000-8000-000000000030'
    path_prefix='/org/freedesktop/NetworkManager/Settings/'
    static_path,pending_path,dhcp_path=[path_prefix+suffix for suffix in ('10','20','30')]
    fixture=bin_dir/'nmcli'
    fixture.write_text('''#!/usr/bin/python3
import json,os,sys,time
a=sys.argv[1:]
assert a[:2]==['--wait','5'], 'Every nmcli request must be bounded'
a=a[2:];text=' '.join(a)
mode=os.environ['NETWORK_TEST_MODE']
with open(os.environ['NETWORK_TEST_LOG'],'a') as f:f.write(json.dumps(a)+'\\n')
static,pending,dhcp=os.environ['NETWORK_TEST_UUIDS'].split(',')
foreign='00000000-0000-4000-8000-000000000099'
path_prefix='/org/freedesktop/NetworkManager/Settings/'
identity={path_prefix+'10':static,path_prefix+'20':pending,path_prefix+'30':dhcp}.get(a[-1],a[-1])
if 'DEVICE,TYPE' in a:
 if mode=='enumeration-error':sys.exit(8)
 if mode=='enumeration-hung':time.sleep(30)
 print(('malformed:ethernet:extra\\n' if mode=='malformed-device' else '')+'eno1:ethernet\\neno2:ethernet\\ntailscale0:tun')
elif text=='device set eno1 managed yes' and mode=='missing-device':sys.exit(10)
elif 'GENERAL.CON-UUID' in a and a[-1]=='eno1' and mode=='active-error':sys.exit(8)
elif 'GENERAL.CON-UUID' in a and a[-1]=='eno1' and mode=='malformed-active':print('invalid')
elif 'CONNECTIONS.AVAILABLE-CONNECTION-PATHS' in a and a[-1]=='eno1' and mode=='inventory-error':sys.exit(8)
elif 'CONNECTIONS.AVAILABLE-CONNECTION-PATHS' in a and a[-1]=='eno1' and mode.startswith('malformed-inventory'):
 print({'malformed-inventory':'not a path',
        'malformed-inventory-suffix':path_prefix+'10,'+path_prefix+'30junk',
        'malformed-inventory-partial':path_prefix+'10,bogus,'+path_prefix+'30',
        'malformed-inventory-newline':path_prefix+'10\\n'+path_prefix+'99',
        'malformed-inventory-empty-member':path_prefix+'10,,'+path_prefix+'30' }[mode])
elif 'connection.autoconnect' in a and identity==static and mode=='profile-error':sys.exit(10)
elif 'connection.autoconnect' in a and identity==foreign and mode=='uuid-label-missing':sys.exit(10)
elif 'connection.autoconnect-priority' in a and identity==static and mode=='ordering-error':sys.exit(8)
elif 'connection.autoconnect-priority' in a and identity==static and mode=='malformed-ordering':print('08')
elif 'GENERAL.CON-UUID' in a:print(static if os.environ['NETWORK_TEST_MODE']=='active' else '--')
elif 'CONNECTIONS.AVAILABLE-CONNECTION-PATHS' in a:
 if mode=='single-profile':print(path_prefix+'10')
 elif mode=='empty-marker':print('--')
 elif mode not in ('empty','dhcp-inspection-error','dhcp-add-error','dhcp-disabled'):print(','.join(path_prefix+suffix for suffix in ('20','30','10')))
elif 'CONNECTIONS.AVAILABLE-CONNECTIONS' in a:
 # Display labels deliberately contain identities of saved but unavailable profiles.
 # These values must never be used for identity enumeration or profile queries.
 label={'uuid-label':'Backup '+foreign,
        'uuid-label-delimiters':'Backup, | : '+foreign+' | {10,99}',
        'uuid-label-newline':'Backup\\n'+foreign+' | forged entry',
        'uuid-label-missing':'Backup '+foreign}.get(mode,'static')
 if mode not in ('empty','empty-marker','dhcp-inspection-error','dhcp-add-error','dhcp-disabled'):print(pending+' | pending,'+dhcp+' | DHCP,'+static+' | '+label)
elif 'connection.autoconnect' in a and 'show' in a:print('no' if identity==pending or mode=='dhcp-disabled' else 'yes')
elif 'connection.autoconnect-priority' in a and 'show' in a:print(999 if mode=='tie' or identity in (pending,static,foreign) else -999)
elif 'connection.timestamp' in a and 'show' in a:print(999 if identity==foreign else 200 if mode=='tie' and identity==dhcp else 100)
elif text.startswith('connection show xur-dhcp-'):
 sys.exit(8 if mode=='dhcp-inspection-error' else 0 if mode=='dhcp-disabled' else 10)
elif 'connection.autoconnect' in a and mode=='dhcp-disabled':print('no')
elif 'connection' in a and 'add' in a and mode=='dhcp-add-error':sys.exit(8)
''');fixture.chmod(0o755)
    env=dict(os.environ,PATH=str(bin_dir)+':'+os.environ['PATH'],NETWORK_TEST_LOG=str(log),NETWORK_TEST_UUIDS=','.join([static,pending,dhcp]))
    def run(mode):
        log.write_text('');result=subprocess.run(['bash',str(repo/'os/bootc/xur-network')],env=env|{'NETWORK_TEST_MODE':mode},check=True,capture_output=True,text=True,timeout=20)
        if mode not in ('saved','active','empty','empty-marker','tie','single-profile','uuid-label','uuid-label-delimiters','uuid-label-newline','uuid-label-missing'):assert 'xur-network:' in result.stderr, f'{mode}: failure must produce a diagnostic'
        return [json.loads(line) for line in log.read_text().splitlines()]
    calls=run('saved');ups=[c for c in calls if 'up' in c]
    assert len(ups)==2 and all(static_path in up for up in ups) and all(pending_path not in c for c in ups)
    assert not any('add' in c for c in calls),'A saved static profile must not be replaced with DHCP'
    calls=run('tie');ups=[c for c in calls if 'up' in c]
    assert len(ups)==2 and all(dhcp_path in c for c in ups),'Timestamp resolves equal priorities'
    calls=run('active');assert not any('up' in c or 'add' in c for c in calls),'Boot must preserve an active static profile'
    calls=run('empty');assert any('add' in c and 'auto' in c for c in calls),'An unconfigured adapter still gets DHCP'
    calls=run('empty-marker');assert any('add' in c and 'auto' in c for c in calls),'An empty marker still allows DHCP'
    for mode in ('single-profile','uuid-label','uuid-label-delimiters','uuid-label-newline','uuid-label-missing'):
        calls=run(mode);ups=[c for c in calls if 'up' in c]
        assert len(ups)==2 and all(c==['--wait','0','connection','up','path',static_path,'ifname',device] for c,device in zip(ups,('eno1','eno2'))),f'{mode}: activate only the available static profile'
        assert not any('add' in c or '00000000-0000-4000-8000-000000000099' in c or 'CONNECTIONS.AVAILABLE-CONNECTIONS' in c for c in calls),f'{mode}: display labels must not affect identities or trigger DHCP'
    for mode in ('missing-device','active-error','malformed-active','inventory-error','malformed-inventory','malformed-inventory-suffix','malformed-inventory-partial','malformed-inventory-newline','malformed-inventory-empty-member','malformed-device'):
        calls=run(mode)
        assert any('up' in c and 'eno2' in c for c in calls),f'{mode}: subsequent NIC must be processed'
        if mode!='malformed-device':assert not any('up' in c and 'eno1' in c for c in calls),f'{mode}: uncertain NIC must not activate'
        assert not any('add' in c for c in calls),f'{mode}: uncertainty must not replace static settings with DHCP'
    for mode in ('profile-error','ordering-error','malformed-ordering','dhcp-inspection-error','dhcp-add-error','dhcp-disabled'):
        calls=run(mode)
        assert not any('up' in c for c in calls),f'{mode}: uncertain/unconfirmed profile must not activate'
    assert not run('enumeration-hung')[1:],'Hung enumeration must be bounded'
    assert not run('enumeration-error')[1:],'Enumeration failure must stop safely'
    agent=(repo/'os/bootc/systemd/xur-agent.service').read_text()
    assert 'Wants=xur-network.service' in agent
    assert 'After=local-fs.target NetworkManager.service' in agent, 'Answer-file application must wait for NetworkManager readiness'
    assert not any('xur-network.service' in line for line in agent.splitlines() if line.startswith(('Requires=','After='))), 'Agent must not wait for optional network activation'
    # Exercise the actual handoff commands on disposable paths, with no chroot or host edits.
    source=temp/'source';source.mkdir();target=temp/'target';target.mkdir()
    profile=source/'static.nmconnection';profile.write_text('[connection]\nid=static\nautoconnect=true\n[ipv4]\nmethod=manual\naddress1=192.0.2.10/24\n');profile.chmod(0o644)
    wifi=source/'wifi.nmconnection';wifi.write_text('[connection]\nid=xur-wifi\nuuid=00000000-0000-4000-8000-000000000040\ntype=wifi\nautoconnect=true\n[wifi]\nssid=Test network\nmac-address=02:11:22:33:44:55\nmode=infrastructure\n[wifi-security]\nkey-mgmt=wpa-psk\npsk=fixture-password-only\npsk-flags=0\n[ipv4]\nmethod=auto\n[ipv6]\nmethod=auto\n');wifi.chmod(0o600)
    script=(repo/'os/installer/install-manager').read_text().split('# Copy saved NetworkManager profiles,',1)[1].split('# Carry the explicitly chosen computer name',1)[0]
    script=script[script.index('mkdir -p'):].replace('chroot "$target" restorecon -RF /etc/NetworkManager','true')
    script=script.replace('/etc/NetworkManager/system-connections/.',str(source)+'/.').replace('if test -d /etc/NetworkManager/system-connections;',f'if test -d "{source}";')
    subprocess.run(['bash','-euc','target="$1"\n'+script,'test',str(target)],check=True)
    saved=target/'etc/NetworkManager/system-connections/static.nmconnection'
    assert saved.read_bytes()==profile.read_bytes() and saved.stat().st_mode&0o777==0o600
    saved_wifi=target/'etc/NetworkManager/system-connections/wifi.nmconnection'
    assert saved_wifi.read_bytes()==wifi.read_bytes() and saved_wifi.stat().st_mode&0o777==0o600
    assert saved_wifi.parent.stat().st_mode&0o777==0o700
    token_source=temp/'bootstrap-token';token_source.write_text('ABCDEF')
    token_script=next(line for line in (repo/'os/installer/install-template.ks').read_text().splitlines() if '/run/xur/bootstrap-token' in line).replace('/run/xur/bootstrap-token',str(token_source))
    (target/'var/lib/xur').mkdir(parents=True)
    subprocess.run(['bash','-euc','target="$1"\n'+token_script,'test',str(target)],check=True)
    saved_token=target/'var/lib/xur/bootstrap-token'
    assert saved_token.read_bytes()==token_source.read_bytes() and saved_token.stat().st_mode&0o777==0o600
    assert 'tailscaled.state' not in (repo/'os/installer/install-template.ks').read_text()
    name_source=temp/'computer-name';name_source.write_text('living-room\n');(target/'etc/xur').mkdir(parents=True)
    handoff=(repo/'os/installer/install-manager').read_text().split('# Carry the explicitly chosen computer name',1)[1].split('mkdir -p "$target/etc/systemd/system.conf.d"',1)[0]
    handoff=handoff[handoff.index('if test'):].replace('chroot "$target" restorecon /etc/hostname','true')
    handoff=handoff.replace('if test -f /etc/xur/computer-name;',f'if test -f "{name_source}";').replace('install -m 600 /etc/xur/computer-name',f'install -m 600 "{name_source}"').replace('install -m 644 /etc/xur/computer-name',f'install -m 644 "{name_source}"')
    subprocess.run(['bash','-euc','target="$1"\n'+handoff,'test',str(target)],check=True)
    assert (target/'etc/hostname').read_text()=='living-room\n' and (target/'etc/xur/computer-name').read_text()=='living-room\n'
print(json.dumps({'suite':'NetworkStartup','savedStaticPreserved':True,'unconfirmedCandidateExcluded':True,'dhcpFallback':True,'installerProfileHandoff':True,'wifiCredentialsAutoconnectAndMacPreserved':True,'serverNameHandoff':True,'transientFailuresContained':True,'uncertainProfilesNeverReplaced':True,'agentStartupIndependent':True,'profileLabelsExcludedFromIdentities':True,'malformedPathInventoriesContained':True}))
