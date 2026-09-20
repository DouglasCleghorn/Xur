# Rufus ISO/File Copy mode

Select `xur-installer-x86_64.iso` in Rufus, choose **GPT**, **UEFI (non CSM)**,
and **FAT32**. When prompted, choose **ISO Image mode (file copy)**.
Keep the default volume label; Rufus also rewrites GRUB references when the
label changes. Use a USB drive of at least 16 GB. Writing replaces its contents.

The ISO remains hybrid media: virtual DVD and raw/DD USB writing remain
available. File-copy mode uses the supplied Fedora shim and GRUB at
`EFI/BOOT/BOOTX64.EFI`; no NTFS helper or custom unsigned GRUB is needed.
Preserving these loaders does not establish that the complete installed Bazzite
Secure Boot chain has been tested. The automated test uses ordinary UEFI.

The online ISO contains Xur and Anaconda; it downloads Bazzite during installation.
No `xur/payload/` directory or complete OS layer archive is embedded. Internet
access is required. Inspection retains the FAT32 per-file size checks and supplied
EFI-loader checks. The previous 7.8 GB offline image and its 2026-09-15 boot/install
receipts describe historical media, not validation of the new online ISO.

The automated fixture in `tests/Xur.Media.Tests/make-file-copy-usb.py` copies all
files from the final ISO onto FAT32, verifies their hashes, and changes the USB
label plus corresponding GRUB references. QEMU boots only that extracted USB
fixture, with no optical media attached. This does not execute the Rufus Windows UI.

Run fixture creation as root only in the disposable Fedora builder, with
`dosfstools` and util-linux:

```bash
sudo python3 make-file-copy-usb.py xur-output/bootiso/install.iso \
  xur-output/fat32.raw
```

Copy the resulting `.raw` and `.json` into `.build/fixtures/`, then:

```bash
python3 tests/Xur.Media.Tests/start-vm.py dist/xur-installer-x86_64.iso \
  --name fat32-install --memory-mib 8192 --file-copy-usb .build/fixtures/fat32.raw
python3 tests/Xur.Media.Tests/check-file-copy-install.py \
  fat32-install .build/fixtures/fat32.raw
```

The fixture uses only newly created file-backed storage. The 2026-09-15 run
completed offline installation and reboot with 8 GiB RAM and four vCPUs; both
the USB and separate data disk remained byte-identical. Tests require Xur's web
root, two DHCP interfaces, read-only scanning, boot-parent protection independent
of disk size, and rejection of changed disk identity before exact approval.
They complete Anaconda installation and reboot with the USB attached, verify
manager account/session handoff, and hash both the USB and separate data disk.

After the tests pass, collect current build metadata and package the release:

```bash
python3 eng/collect-release-evidence.py
python3 eng/build-app-bundle.py --from-published
python3 eng/package-file-copy-release.py
```

Older evidence retains its original ISO and application identities.

References: [Ubuntu casper layered live filesystems](https://manpages.ubuntu.com/manpages/jammy/man7/casper.7.html),
[bootc 1.16.10 source image formats](https://github.com/bootc-dev/bootc/blob/v1.16.10/crates/lib/src/install.rs),
[Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ),
[Rufus label replacement](https://github.com/pbatard/rufus/blob/v4.15/src/iso.c).
