# Xur application update 2026.09.15.2

This release accompanies the FAT32-compatible hybrid installer ISO. The agent
protects the boot source by its kernel-specified label, UUID, partition UUID or
path, including the parent disk. Protection persists when the boot partition is
unmounted after a live RAM copy. Duplicate matching identities protect all
matching disks.

Only the agent assembly and its debug symbols differ from application update
2026.09.15.1. Web UI, gateway, runtime helpers and catalog files are unchanged.
The current application passed unit, bootstrap, terminal ownership and twenty
profile-switching cycles. The new ISO completed an offline FAT32 file-copy
installation and reboot with 8 GiB RAM, preserving the manager account/session,
USB image and separate data disk. See `.build/evidence/file-copy-install.json` and
`.build/evidence/fat32-app-diff.json`.

Existing systems can obtain the agent change from the local update repository
without reinstalling. FAT32 installer layout changes apply when creating new
installation media; an app update cannot rewrite an existing installer USB.
