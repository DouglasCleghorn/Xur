#!/usr/bin/env bash
# Run in the disposable Fedora builder after exporting the final ISO.
set -eu
iso="${1:?ISO path required}"
out="${2:?New inspection directory required}"
test ! -e "$out"
mkdir -p "$out"
xorriso -osirrox on -indev "$iso" -extract /LiveOS/squashfs.img "$out/squashfs.img" \
    -extract /EFI/BOOT/grub.cfg "$out/uefi-grub.cfg" \
    -extract /boot/grub2/grub.cfg "$out/bios-grub.cfg" \
    -extract /images/pxeboot/initrd.img "$out/initrd.img" \
    -extract /EFI/BOOT/grubx64.efi "$out/removable-grub.efi" \
    -report_el_torito plain > "$out/boot-layout.txt" 2>&1
lsinitrd "$out/initrd.img" > "$out/initrd-files.txt"
unsquashfs -l "$out/squashfs.img" > "$out/live-files.txt"
mkdir "$out/media"
mount -o loop,ro "$iso" "$out/media"
trap 'umount "$out/media"' EXIT
podman run --rm --entrypoint cat localhost/xur-installer:x86_64 /boot/efi/EFI/fedora/gcdx64.efi > "$out/supplied-grub.efi"
unsquashfs -d "$out/root" "$out/squashfs.img" \
    usr/lib/xur usr/share/xur etc/containers etc/pki/containers usr/lib/bootc/install usr/lib/systemd/system usr/libexec/xur-run-install usr/libexec/xur-installer-app usr/libexec/xur-live-app usr/libexec/xur-resolve-install-source usr/libexec/xur-install-manager usr/libexec/xur-network usr/lib/systemd/system/xur-install.service \
    usr/lib/systemd/system/xur-agent.service usr/lib/systemd/system/xur-network.service usr/lib/systemd/system/tailscaled.service \
    usr/lib/systemd/system/xur-os-update.service usr/lib/systemd/system/xur-os-update.timer \
    usr/bin/xur-control usr/bin/xur-agent usr/bin/xur-gateway usr/bin/tailscale usr/sbin/tailscaled usr/lib/systemd/system/xur-control.service usr/lib/systemd/system/xur-gateway.service \
    usr/lib/systemd/system.conf.d/50-xur-console.conf usr/lib/systemd/journald.conf.d/50-xur-console.conf > "$out/extract.txt"
python3 - "$out" /home/builder/xur-build "$iso" <<'PY'
import hashlib,json,pathlib,re,sys
out,context,iso=map(pathlib.Path,sys.argv[1:]);root=out/'root'
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
checks={}
for prefix in ['rootfs','installer-rootfs']:
 for p in (context/prefix).rglob('*'):
  if not p.is_file():continue
  rel=p.relative_to(context/prefix);embedded=root/rel
  if embedded.exists():
   actual=sha(embedded);assert actual==sha(p),str(rel);checks[str(rel)]=actual
assert len(checks)>100
for name in ('xur-install-manager','xur-run-install','xur-network'):
 assert 'usr/libexec/'+name in checks
for name in ('control','agent','gateway'):
 assert (root/f'usr/lib/xur/{name}/Xur.{name.title()}').is_file()
 assert (root/f'usr/bin/xur-{name}').is_symlink()
ks=(root/'usr/share/xur/install-template.ks').read_text()
assert all(x not in ks for x in ['clearpart','ignoredisk','part /'])
for p in [out/'uefi-grub.cfg',out/'bios-grub.cfg']:
 s=p.read_text();assert 'xur.installer=1' in s
 assert 'enforcing=0' not in s and 'selinux=0' not in s
layout=(out/'boot-layout.txt').read_text();assert 'UEFI' in layout and 'BIOS' in layout
initrd=(out/'initrd-files.txt').read_text()
assert 'vfat.ko' in initrd, 'FAT32 initramfs driver is missing'
assert not any(re.fullmatch(r'squashfs-root/usr/lib/modules/[^/]+/initramfs[.]img',line) for line in (out/'live-files.txt').read_text().splitlines()), 'Redundant boot initramfs is embedded in the live filesystem'
assert sha(out/'removable-grub.efi')==sha(out/'supplied-grub.efi'), 'Supplied EFI bootloader was replaced'
files=[p for p in (out/'media').rglob('*') if p.is_file()]
largest=max(files,key=lambda p:p.stat().st_size)
assert largest.stat().st_size <= 2**32-1, 'ISO contains a file too large for FAT32: '+str(largest)
assert 'registry:ghcr.io/ublue-os/bazzite-nvidia-open:stable' in ks
assert not (out/'media/xur/payload').exists(), 'Online ISO embeds an OS payload'
(out/'embedded-verification.json').write_text(json.dumps({'verifiedFiles':checks,'safeKickstartTemplate':True,'uefiAndBiosLayout':True,'enforcementNotDisabled':True,'fat32Compatible':True,'largestFile':str(largest.relative_to(out/'media')),'largestFileBytes':largest.stat().st_size,'isoFilesChecked':len(files),'suppliedEfiLoaderSha256':sha(out/'removable-grub.efi'),'onlineInstaller':True,'deduplicatedBootInitrd':True,'osChannel':'ghcr.io/ublue-os/bazzite-nvidia-open:stable','liveInstallerBase':json.loads(pathlib.Path('/home/builder/xur-output/installer-base.json').read_text())},indent=2)+'\n')
print(json.dumps({'embeddedFilesVerified':len(checks),'safeKickstartTemplate':True,'uefiAndBiosLayout':True}))
PY
