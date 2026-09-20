# Build disk retention

`eng/package-update.sh` cleans obsolete generated media before and after a release. It requires at least 12 GiB of free space before building. The release lock prevents standalone cleanup from overlapping packaging.

Run `python3 eng/cleanup-build.py` to preview or add `--apply` to delete the listed files. Add `--keep-vm NAME` to preserve a test VM, or create `.keep` inside a VM/snapshot directory.

Cleanup removes raw media from inactive test VMs older than three days, retaining all development VMs and the three most recently modified VM disks. It preserves an entire VM's media if any disk is recent. It skips VM pruning when QCOW2 overlays exist, and skips files open by a process, referenced by process arguments, or attached to a loop device.

It also removes ISO files from old build snapshots (retaining the two newest snapshot directories) and redundant staging archives older than three days. Credentials, signing keys, source archives, manifests, logs, evidence, the build toolchain, builder VM, and all published update bundles are retained. Pruned test VMs must be recreated to run again.

The last cleanup report is `.build/cleanup-last.json`. Published bundles are deliberately retained for existing clients and rollback; pruning that repository requires a separate retention decision.
