# Storage usage

Open Storage from the desktop navigation or More → Storage on phones. It shows
capacity, used and available space for local mounted filesystems, folder usage,
and physical disks including unmounted disks. The unified **Disks & mounts** table
places each filesystem beside its observed backing disks and their capacities.
Shared filesystems appear once; disks can appear on more than one filesystem row.
Scroll the table horizontally on narrow screens to view all columns. Bind mounts of the same filesystem
are grouped so they are not counted repeatedly. Available space is reported by
the filesystem and may exclude reserved blocks.

Folder categories cover the Xur model store, container/engine storage (including
model caches inside containers), user homes, application versions, logs, and Xur
settings/state. These are allocated-block measurements, not download sizes. Shared
data, snapshots, filesystem metadata and other OS files mean these categories are
not an exact partition of total filesystem usage. Hardlinked model files are
counted once within the model category. Directory scans do not follow symlinks or
cross filesystem boundaries; separate filesystems still appear in the table.

Disk metadata and endurance are sampled by the privileged agent every five
minutes, even with no viewers, and stored in local VictoriaMetrics. Page requests
reuse this cache; folder and volume size scans remain on demand. Opening the page immediately returns the last snapshot
while a new scan runs. Refresh usage requests a scan without starting concurrent
scans. The page polls while scanning and keeps disclosure panels open until the
operator closes them. Errors are shown as unavailable, never as an invented zero.
No storage is mounted or erased by this page.

Authenticated API: `GET /api/storage/usage`,
`POST /api/storage/usage/refresh`. Cookie mutations require CSRF; authenticated
bearer clients use the same API. Installer mode disables these installed-system
endpoints.

## Write endurance remaining

The disk table shows the SMART estimate of remaining write endurance when
reported. Percent remaining is `max(0, 100 - percentage used)`; this is a wear
indicator, not a prediction of failure or a guarantee of data integrity. Missing
readings are omitted from the table, not displayed as zero. Available spare blocks and free
filesystem space are not used as endurance measurements.

Remaining bytes are displayed in humanized binary units only if explicitly
reported by the drive. Standard SMART wear readings do not provide this value,
so only the percentage is shown. A disk with no reported endurance has a blank
endurance cell. Xur does
not extrapolate bytes from lifetime writes and wear, consult a model/TBW lookup,
or substitute a lifetime endurance rating for a remaining budget.

The agent reads the host's `smartctl` JSON during its existing five-minute cached
scan, with a 15-second per-device timeout. No SMART tests or writes are started;
rotating or unidentified media are skipped. Supported SSD queries include a
standby check. Unsupported bridges, a missing tool, and failed reads remain
available in the API without preventing filesystem usage from loading. This is a current
snapshot in the UI; full-resolution disk capacity, filesystem used/available
space and reported endurance are retained in VictoriaMetrics. Missing endurance
values are omitted from history too. The existing RAM buffer handles write
outages; failures and dropped samples appear in a Storage-page alert.

Smartmontools 7.5 supplies normalized ATA/SCSI/NVMe endurance fields; standard
NVMe and SCSI fields from older versions are also supported. ATA vendor attribute
IDs are not interpreted independently. See the upstream
[NVMe JSON fields](https://github.com/smartmontools/smartmontools/blob/main/src/nvmeprint.cpp)
and [normalized ATA fields](https://github.com/smartmontools/smartmontools/blob/main/src/ataprint.cpp).

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

## Network shares

Open **Storage → Network shares** to host encrypted SMB3 folders or connect to
an existing SMB3 server. Install the **SMB runtime** on that page first. This
builds a persistent local container image from the current Fedora image at
`quay.io/fedora/fedora:latest` and its supported Samba packages. **Update SMB
runtime** resolves current packages again; restart hosting to use the update.
A failed build retains the previous image. Normal startup does not download or
install software. The host also needs `mount.cifs` from `cifs-utils` (included in
the upstream Bazzite images); a missing helper is reported before saving a
connection.

### Hosting a folder

1. Choose an existing local ext4, XFS or Btrfs data folder beneath `/var/home`,
   `/var/mnt`, `/var/srv`, `/mnt` or `/srv`. Symlink paths and network re-exports are rejected.
2. Add a share name, a dedicated SMB username/password and a filesystem UID/GID.
   This creates an SMB account inside the service, not a host login account.
   Each share has one account in this milestone. The selected UID/GID must have
   access to the folder; Xur does not recursively change ownership or permissions.
3. Select read-only or read/write access. Choose this machine's specific LAN or
   VPN IPv4 address and start hosting. Use the displayed `smb://address/share`
   address, or `\\address\share` on Windows. SMB3 encryption and authenticated
   access are required; guest access and SMB1 are disabled.

Hosting runs in `xur-smb.service`, independently of GPU profiles and the web
interface. Podman publishes TCP 445 only on the chosen address. An existing SMB
listener can prevent startup; Xur does not replace other SMB services. Stop
hosting before adding/removing shares. Removing a share does not remove its
files. To replace a hosting account/password, stop hosting, remove that share
and recreate it with the new credentials.

The saved filesystem UUID is checked before every service start so a missing
backing disk cannot turn into an empty share on the system disk. Private bind
mounts retain the original filesystem if a host mount is detached. Samba does
not follow symlinks inside shared folders. The Samba container disables SELinux
label separation for itself to access explicitly selected existing folders
without relabeling user data; host SELinux remains enabled. The server receives
only hosting configuration/password hashes and selected folders, not saved
credentials for outgoing connections.

### Connecting and assigning folders

Save a server DNS name or IPv4 address, share name, credentials and the local
UID/GID that should access the mounted files. Read-only access and required
SMB encryption are the defaults. Select **Connect** or **Test access** to verify
an independent SMB login and the mounted filesystem. Saved status distinguishes
mounted from unmounted; a mounted share is not proof that its server is online.

Connections use stable paths under `/var/mnt/xur-smb/`. They mount on demand after
reboot and do not require the remote server during ordinary boot. **Disconnect**
stops and disables the automount; **Connect** enables it again. Starting an
assigned workload also mounts its required connection. To replace server details
or credentials, remove/recreate the connection and update its assignments.

In the profile editor, expand **Shared network folders** on a container or model
workload. Select connections and optionally allow writes; a read-only connection
cannot be assigned writable. Each appears at the displayed `/shared/smb-…` path
inside the workload. The mount GID is added to the container’s supplementary
groups so workloads can access the mapped files without DAC override
capabilities. Existing persistent volumes cannot overlap these paths.
Changing assignments or write permissions changes the workload fingerprint and
therefore recreates the container on the next profile load. Workstations can
access the host mount path using the configured local UID/GID; per-desktop
storage isolation is outside this milestone.

Every assigned connection is required. Startup checks a fresh SMB session and
verifies the expected CIFS mount; cached metadata alone cannot admit startup.
Mount failures remain visible as workload startup errors. Assigned containers
use no automatic Podman restart policy, so restarts go through Xur's checks.
Their systemd lifetime depends on all required mounts, and their private bind
mounts never switch to an empty local directory. An outage during running I/O
still follows Linux CIFS reconnect/error behavior; this feature does not provide
an application-level transaction or an automatic workload restart on recovery.
Unload all assigned workloads (including pending ones and independent services)
before disconnecting/removing a connection.

Credentials are stored in owner-only files below
`/var/lib/xur/network-shares`; passwords are not returned in status, exported in
ordinary configuration JSON or put in process arguments. The private recovery
backup includes network-share configuration and credentials. It excludes
container images, so reinstall the SMB runtime after restoring to a new host.
Treat that backup as a secret. Browser mutations require the existing
administrator session and CSRF protection. T3 MCP can perform these operations
when **Change Xur settings and run actions** (or full host access) is enabled;
ordinary automation keys remain read-only for network shares.

Authenticated read endpoint: `GET /api/storage/network`. Browser or write-enabled T3 MCP mutations:
`POST /api/storage/network/runtime`, `/connections`, `/connections/action`,
`/hosted`, `/hosted/remove`, and `/server` (all under `/api/storage/network`).
Connection actions are `connect`, `test`, `disconnect`, and `remove`.

Validation: run `Xur.Unit.Tests --network-shares` for credential boundaries,
persistent connections, mount identity, offline startup, permissions and removal
guards. Live acceptance additionally needs a Bazzite host and SMB server: test
read/write permissions, host reboot, server outage/recovery, profile switching,
backing-disk removal and SELinux Enforcing before release. Automated command
fixtures do not substitute for those live filesystem checks.


### Managing shares through Xur MCP

Connect Xur MCP from the T3 Code page and grant read or write permission there.
Call `xur_catalog` first; network-share routes include request schemas and
operation descriptions. Use `xur_read` with `/api/storage/network` to inspect
current state. Read-only permission cannot change storage. With write permission,
use `xur_write` and the POST routes above to install the runtime, create shares
and connections, test access, start/stop hosting, or disconnect/remove entries.
The same workload-in-use checks, path validation and filesystem checks apply.
Disabling permissions or revoking the MCP key takes effect on the next request.
MCP cannot change its own permissions or create Xur manager credentials.

To assign a connection, read the profile and use the profile APIs from the
catalog, preserving its revision and other workload settings. Each workload's
`networkShares` array contains `{ "connectionId": "smb-…", "readOnly": true }`.
Only container/model workloads support these assignments. Creating a connection
or hosted account requires a password in the request body: use secure secret
input, or create it in the browser and use MCP for subsequent management. Do not
paste passwords into ordinary chat or commit them to configuration files.
