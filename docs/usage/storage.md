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
data objects (at least 128 bytes, including their field name) are compressed in memory
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
(`Compress=128`, a byte threshold rather than a zstd compression level) and
`/etc/systemd/system/systemd-journald.service.d/60-xur-log-compression.conf`
(`SYSTEMD_JOURNAL_COMPRESS=ZSTD`). To override them, use separate, later-sorting
administrator drop-ins such as `99-local.conf`. Algorithm selection uses systemd's
[documented environment variable](https://github.com/systemd/systemd/blob/v258/docs/ENVIRONMENT.md#systemd-journald-journalctl),
which has weaker stability guarantees than its main configuration interface.
`journalctl --header` reports `COMPRESSED-ZSTD` on new journal files when the
codec is enabled. That flag describes the file's compression capability; it can
appear even when no data objects were worth compressing. Likewise,
`systemctl show systemd-journald -p Environment` shows the requested codec, not
proof that individual objects were compressed.

A synthetic systemd 259 journal-writer comparison found that lowering the
threshold from 512 to 128 allowed 360-byte messages to compress, reducing used
journal content by about 37% relative to uncompressed journals. The 120-byte
messages did not benefit, and the 1800-byte messages already compressed at the
default threshold. All messages round-tripped through `journalctl`. This measures
journal content, not SSD writes; journal preallocation, metadata, and device write
amplification still affect actual wear.

Sunshine's console output remains in the journal for workstation logs, diagnostics,
and encoder readiness checks. Xur sets `log_path=/dev/null` to discard Sunshine's
duplicate file output, without moving logs to RAM. This takes effect on the next
Sunshine start; running streams are not restarted for this setting. Existing file
logs are retained. Sunshine's own file-backed log viewer will have no file output;
use Xur's workstation logs instead. The pinned Sunshine version has no independent
file-logging switch and attempts to rotate `/dev/null`, producing one harmless
`Failed to rotate log file '/dev/null': Permission denied` warning at startup.
It runs as the unprivileged workstation user and continues logging to stdout.
See the [pinned logging implementation](https://github.com/LizardByte/Sunshine/blob/v2026.914.233613/src/logging.cpp).

The other plain text logs written by Xur are episodic:

| Log | Writes and readers | Wear priority |
| --- | --- | --- |
| `/var/lib/xur/updates/operation.log` | Replaced for each OS operation; child output goes directly to this file rather than also to the journal. The update view returns its last 24,000 characters. | Best remaining text-log candidate for compression; the file itself has no size cap. |
| `/var/lib/xur/workloads/<id>.error.log` | Replaced on a workload startup failure; combined with container logs when Xur displays workload logs. | Low; keep this failure evidence. |
| Installer `/tmp/*.log` | Installer and Anaconda operation logs, with explicit diagnostic export when requested. | Installation-only; separate from steady-state host logging. |

Xur's own service logger writes to stderr and keeps a bounded in-memory fallback;
it does not also write a persistent text file. Container logs depend on the Podman
log driver chosen when the container was created. Journald compression covers
the journal driver, not file drivers or application logs inside containers.

For ordinary append-only text logs on Btrfs, a directory's `compression=zstd`
property can compress future writes before they reach disk. Enabling compression
does not first write an uncompressed copy. Existing extents remain untouched;
recompressing them with defragmentation would add writes. Btrfs compression
requires copy-on-write and checksums, while journal files commonly use NoCoW
and preallocation, so a Btrfs mount setting alone does not cover the journal.
See the [Btrfs compression documentation](https://btrfs.readthedocs.io/en/latest/Compression.html).
Before adding a text-log property, check whether the live filesystem already uses
zstd. A directory property applies to newly created files; existing log files need
their own property for future writes. No text-log properties or recompression of
old files are applied by Xur's journal defaults.

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
