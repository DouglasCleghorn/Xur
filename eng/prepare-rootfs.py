#!/usr/bin/env python3
import pathlib,shutil,hashlib,json,time
repo=pathlib.Path(__file__).resolve().parents[1];context=repo/'.build/context'
for name in ('rootfs','installer-rootfs'):
    target=context/name
    if target.exists():shutil.rmtree(target)
    target.mkdir()
def copy(source,target):
    target.parent.mkdir(parents=True,exist_ok=True)
    if source.is_dir():shutil.copytree(source,target,dirs_exist_ok=True)
    else:shutil.copy2(source,target)
base=context/'rootfs'
for source,target in [('publish/control','usr/lib/xur/control'),('publish/agent','usr/lib/xur/agent'),('publish/gateway','usr/lib/xur/gateway'),('catalog','usr/share/xur/catalog'),
                      ('tailscale/tailscale','usr/bin/tailscale'),('tailscale/tailscaled','usr/sbin/tailscaled')]:
    copy(context/source,base/target)
copy(repo/'.build/console-runtime',base/'usr/lib/xur/agent/console')
copy(repo/'.build/virtual-display-runtime',base/'usr/lib/xur/agent/virtual-display')
copy(repo/'.build/streaming-runtime',base/'usr/lib/xur/agent/streaming')
copy(repo/'os/bootc/systemd',base/'usr/lib/systemd/system')
copy(repo/'os/bootc/system.conf.d',base/'usr/lib/systemd/system.conf.d')
copy(repo/'os/bootc/journald.conf.d',base/'usr/lib/systemd/journald.conf.d')
copy(repo/'os/bootc/xur-network',base/'usr/libexec/xur-network')
copy(repo/'os/bootc/upstream-lock.json',base/'usr/share/xur/upstream-lock.json')
for name in ('xur-os-update.service','xur-os-update.timer'):
    copy(repo/'os/bootc/systemd'/name,base/'usr/share/xur'/name)
bundle=base/'usr/share/xur/app-bundle'
for name in ('control','agent','gateway'):
    copy(context/'publish'/name,bundle/name)
copy(context/'catalog',bundle/'catalog')
copy(repo/'.build/console-runtime',bundle/'agent/console')
copy(repo/'.build/virtual-display-runtime',bundle/'agent/virtual-display')
copy(repo/'.build/streaming-runtime',bundle/'agent/streaming')
copy(repo/'os/bootc/application-update-key.pem',base/'usr/share/xur/application-update-key.pem')
copy(repo/'os/bootc/application-update-key.pem',bundle/'host/application-update-key.pem')
copy(repo/'os/bootc/application-features.json',bundle/'host/application-features.json')
for name in ('os-update','app-update','update-all','xur-network','hardware-hooks/reboot'):
    copy(repo/'os/bootc'/name,bundle/'host'/name)
    (bundle/'host'/name).chmod(0o755)
files={str(p.relative_to(bundle)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(bundle.rglob('*')) if p.is_file()}
bundle_id=hashlib.sha256(json.dumps(files,sort_keys=True).encode()).hexdigest()
(bundle/'bundle.json').write_text(json.dumps({'schema':1,'hostAbi':1,'buildSequence':int(time.time()),'version':bundle_id[:12],'id':bundle_id,'files':files},indent=2)+'\n')
live=context/'installer-rootfs'
for source,target in [('app-bootstrap','usr/libexec/xur-installer-app'),('live-app','usr/libexec/xur-live-app'),('resolve-source','usr/libexec/xur-resolve-install-source'),('systemd','usr/lib/systemd/system'),('iso.yaml','usr/lib/image-builder/bootc/iso.yaml'),
                      ('install-template.ks','usr/share/xur/install-template.ks'),('run-install','usr/libexec/xur-run-install'),('install-manager','usr/libexec/xur-install-manager')]:
    copy(repo/'os/installer'/source,live/target)

# Scope live service overrides to the installer only; installed units use the bundle directly.
for name in ('control','agent','gateway'):
    override=live/f'usr/lib/systemd/system/xur-{name}.service.d/installer.conf'
    override.parent.mkdir(parents=True,exist_ok=True)
    override.write_text(f'[Unit]\nWants=xur-installer-app-prepare.service\nAfter=xur-installer-app-prepare.service\n[Service]\nExecStart=\nExecStart=/usr/libexec/xur-live-app {name}\n')
copy(repo/'os/bootc/bazzite.pub',live/'etc/pki/containers/xur-bazzite.pub')
policy={'default':[{'type':'reject'}],'transports':{'docker':{'ghcr.io/ublue-os/bazzite-nvidia-open':[{'type':'sigstoreSigned','keyPath':'/etc/pki/containers/xur-bazzite.pub','signedIdentity':{'type':'matchRepository'}}]}}}
(live/'etc/containers').mkdir(parents=True,exist_ok=True)
(live/'etc/containers/policy.json').write_text(json.dumps(policy)+'\n')
(live/'etc/containers/registries.d').mkdir(parents=True,exist_ok=True)
(live/'etc/containers/registries.d/xur-bazzite.yaml').write_text('docker:\n  ghcr.io/ublue-os:\n    use-sigstore-attachments: true\n')

(live/'usr/lib/bootc/install').mkdir(parents=True,exist_ok=True)
(live/'usr/lib/bootc/install/90-xur.toml').write_text('[install]\nenforce-container-sigpolicy = true\n')
