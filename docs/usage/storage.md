# Storage usage

Open Storage from the desktop navigation or More → Storage on phones. It shows
capacity, used and available space for local mounted filesystems, folder usage,
and physical disks including unmounted disks. Bind mounts of the same filesystem
are grouped so they are not counted repeatedly. Available space is reported by
the filesystem and may exclude reserved blocks.

Folder categories cover the Xur model store, container/engine storage (including
model caches inside containers), user homes, application versions, logs, and Xur
settings/state. These are allocated-block measurements, not download sizes. Shared
data, snapshots, filesystem metadata and other OS files mean these categories are
not an exact partition of total filesystem usage. Hardlinked model files are
counted once within the model category. Directory scans do not follow symlinks or
cross filesystem boundaries; separate filesystems still appear in capacity cards.

Measurements run in the privileged agent as background read-only metadata scans,
cached for five minutes. Opening the page immediately returns the last snapshot
while a new scan runs. Refresh usage requests a scan without starting concurrent
scans. The page polls while scanning and keeps disclosure panels open until the
operator closes them. Errors are shown as unavailable, never as an invented zero.
No storage is mounted or erased by this page.

Authenticated API: `GET /api/storage/usage`,
`POST /api/storage/usage/refresh`. Cookie mutations require CSRF; authenticated
bearer clients use the same API. Installer mode disables these installed-system
endpoints.

## SSD TRIM

The SSD TRIM section shows the scheduled timer state, eligible mount points,
discard support and the latest recorded manual result. **Refresh status** reads
the current state. **Trim SSD** starts a manual operation for an eligible mount;
review its result on Storage. Unsupported targets do not offer the action.

TRIM returns unused filesystem blocks to the device. It does not delete files or
increase filesystem free space. Actual support is checked when the operation
runs; a failed operation remains visible. Long device and subvolume paths wrap
within the page.

For file navigation, compressed downloads, moves and deletion, use [Files](files.md).
Its folder-size cache is separate from Storage's five-minute category snapshot;
refresh the relevant view when comparing measurements. Neither is a sum that can
replace the filesystem's capacity/available-space figures.

## Log compression and SSD writes

Xur enables native zstd compression for systemd journal logs. Eligible journal
data objects (larger than the default 512-byte threshold) are compressed in memory
before writing. Short or incompressible records can remain uncompressed, and
journal metadata still requires writes. Logs stay readable through `journalctl`
and Xur's diagnostics. This covers services and containers that log to the journal;
ordinary text logs, including logs written inside containers, are separate.

New installs receive these defaults from the selected app bundle. On an installed
host, the agent installs them on its first startup with the updated bundle, restarts
journald, and rotates once to start journal files with the selected codec. Old
journals are retained without rewriting or recompression. Later agent starts do
not rewrite configuration or restart journald unless configuration changes or a
previous activation failed. A failure is logged and retried on the next agent start.

The defaults are `/etc/systemd/journald.conf.d/60-xur-log-compression.conf`
(`Compress=yes`) and
`/etc/systemd/system/systemd-journald.service.d/60-xur-log-compression.conf`
(`SYSTEMD_JOURNAL_COMPRESS=ZSTD`). To override them, use separate, later-sorting
administrator drop-ins such as `99-local.conf`. Algorithm selection uses systemd's
[documented environment variable](https://github.com/systemd/systemd/blob/v258/docs/ENVIRONMENT.md#systemd-journald-journalctl),
which has weaker stability guarantees than its main configuration interface.
Check `journalctl --header` for `COMPRESSED-ZSTD` on new journal files when
validating an installed release. `systemctl show systemd-journald -p Environment`
shows the requested codec; it does not prove on-disk compression by itself.

For ordinary append-only text logs on Btrfs, a directory's `compression=zstd`
property can compress future writes before they reach disk. Enabling compression
does not first write an uncompressed copy. Existing extents remain untouched;
recompressing them with defragmentation would add writes. Btrfs compression
requires copy-on-write and checksums, while journal files commonly use NoCoW
and preallocation, so a Btrfs mount setting alone does not cover the journal.
See the [Btrfs compression documentation](https://btrfs.readthedocs.io/en/latest/Compression.html).

Further SSD-wear improvements to consider:

- Measure writes per day and identify the processes writing most before tuning.
- Reduce repetitive/debug logging and avoid writing the same logs to multiple sinks.
- Keep disposable caches and temporary files in RAM when memory capacity permits.
- Prefer RAM-only zram for swap, and investigate sustained memory pressure that
  causes disk-backed swap or zram writeback.
- Keep TRIM enabled and adequate free space available; bound log and snapshot
  retention to prevent the disk filling. Retention caps control space, not log volume.
- Avoid routine defragmentation, full balances, and recompression of existing data.
- Consider `noatime` where access timestamps are unnecessary to avoid metadata writes.

RAM-only logging would reduce writes further but loses logs on reboot or power
loss. Persistent logging, retention, and synchronization intervals are unchanged
by this compression default.
