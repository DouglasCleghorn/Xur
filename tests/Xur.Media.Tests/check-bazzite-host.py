#!/usr/bin/env python3
"""Observe the real installed Bazzite host, including updates with Xur stopped."""
import argparse,json,pathlib,time
from guest import execute
p=argparse.ArgumentParser();p.add_argument('name');a=p.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2];vm=repo/'.build/vms'/a.name

def run(*command):
    result=execute(a.name,list(command))
    if result['code']!=0:raise RuntimeError('Guest operation failed: '+command[0]+' '+result['error'][:500])
    return result['output']
raw=json.loads(run('/usr/bin/bootc','status','--json'))
current=raw['status']['booted']['image'];assert current['image']['image']=='ghcr.io/ublue-os/bazzite-nvidia-open:stable'
assert 'bazzite' in run('/usr/bin/cat','/etc/os-release').lower()
assert run('/usr/bin/systemctl','get-default').strip()=='multi-user.target'
hook='/var/lib/xur/app/current/host/hardware-hooks/reboot'
assert 'PATH=/var/lib/xur/app/current/host/hardware-hooks:' in run('/usr/bin/systemctl','show','bazzite-hardware-setup.service','--property=Environment')
assert run('/usr/bin/env','PATH=/var/lib/xur/app/current/host/hardware-hooks:/usr/bin','/usr/bin/bash','-c','command -v reboot').strip()==hook
assert 'Hardware configuration staged.' in run(hook)
assert 'bluetooth.disable_ertm=1' in run('/usr/bin/cat','/proc/cmdline')
for unit in ('sddm.service','gdm.service','plasmalogin.service','display-manager.service'):
    assert execute(a.name,['/usr/bin/systemctl','is-enabled',unit])['output'].strip()=='masked'
processes=run('/usr/bin/ps','-eo','comm=')
assert not any(name in processes.splitlines() for name in ('kwin_wayland','plasmashell','steam','gnome-shell','sddm','plasmalogin'))
units=run('/usr/bin/systemctl','cat','xur-control.service','xur-agent.service','xur-gateway.service')
assert '/var/lib/xur/app/current/' in units
manifest=run('/usr/bin/cat','/var/lib/xur/app/current/bundle.json')
assert json.loads(manifest)['hostAbi']==1
assert run('/usr/bin/systemctl','is-enabled','xur-os-update.timer').strip()=='enabled'
# Stop the manager and agent, then run the very same service used by the timer.
run('/usr/bin/systemctl','stop','xur-control.service','xur-agent.service')
try:
    run('/usr/bin/systemctl','start','--no-block','xur-os-update.service')
    for _ in range(720):
        state=execute(a.name,['/usr/bin/systemctl','show','xur-os-update.service','--property=ActiveState,Result'])['output']
        if 'ActiveState=inactive' in state:
            assert 'Result=success' in state,state
            break
        if 'ActiveState=failed' in state:raise RuntimeError('Independent upstream update service failed')
        time.sleep(2)
    else:raise RuntimeError('Upstream update did not finish')
finally:
    run('/usr/bin/systemctl','start','xur-agent.service','xur-control.service')
status=json.loads(run('/var/lib/xur/app/current/host/os-update','status'))
assert status['operation']['stage']=='Complete'
deployment_observation=json.loads(run('/usr/bin/bootc','status','--json'))
staged=deployment_observation['status'].get('staged')
staged_image=staged.get('image') if staged else None
version_change=('Same-version signed deployment and rollback; no newer upstream version was available.'
                if staged_image and staged_image.get('version')==current.get('version')
                else 'Different upstream version staged.' if staged_image else 'No deployment staged.')
assert run('/usr/bin/cat','/var/lib/xur/app/current/bundle.json')==manifest
def reboot():
    old=run('/usr/bin/cat','/proc/sys/kernel/random/boot_id')
    try:run('/usr/bin/systemctl','reboot','--no-block')
    except (OSError,ValueError):pass # The guest agent can stop before returning its exit receipt.
    for _ in range(120):
        time.sleep(3)
        try:
            if run('/usr/bin/cat','/proc/sys/kernel/random/boot_id')==old:continue
            if run('/usr/bin/systemctl','is-active','xur-control.service','xur-agent.service').splitlines()!=['active','active']:continue
            assert run('/usr/bin/cat','/var/lib/xur/app/current/bundle.json')==manifest
            assert run('/usr/bin/systemctl','get-default').strip()=='multi-user.target'
            return json.loads(run('/var/lib/xur/app/current/host/os-update','status'))
        except (OSError,RuntimeError):pass
    raise RuntimeError('Updated OS and manager did not return after reboot')
update_reboot='No newer deployment available'
rollback='No previous deployment available'
if status['pending']:
    expected=status['pending']['digest']
    status=reboot();assert status['current']['digest']==expected
    update_reboot='Passed'
    if status['previous']:
        expected=status['previous']['digest']
        run('/var/lib/xur/app/current/host/os-update','rollback')
        status=reboot();assert status['current']['digest']==expected
        rollback='Passed'
receipt={'suite':'BazziteHost','result':'Passed','upstreamImage':current,'desktopStopped':True,'persistentApplicationBundle':True,'upstreamUpdateWithoutManagerOrAgent':True,'applicationManifestUnchanged':True,'kernel':run('/usr/bin/uname','-r').strip(),'nvidiaPackage':run('/usr/bin/rpm','-q','nvidia-driver').strip(),'memory':run('/usr/bin/free','-b'),'media':json.loads((vm/'vm-manifest.json').read_text())}
receipt.update(updateReboot=update_reboot,explicitRollback=rollback)
receipt.update(versionChange=version_change,deploymentObservation=deployment_observation)
receipt['hardwareSetupRebootDeferred']=True
(repo/'.build/evidence/bazzite-host.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt))
