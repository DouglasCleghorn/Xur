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
