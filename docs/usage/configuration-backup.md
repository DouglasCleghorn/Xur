# Recovery configuration backup

In **Settings → Configuration backup**, choose **Download config backup**.
Xur downloads `xur-backup-<UTC timestamp>.zip` through HTTPS. Plain HTTP opens
the encrypted manager before collecting credentials. Authentication is required;
scoped API keys cannot download this archive.

**This ZIP contains credentials and private keys. Compression is not encryption.**
Keep it in private, preferably encrypted storage. Do not attach it to bug reports.

## Contents

- `files/var/lib/xur/profiles.db`: a complete SQLite online snapshot, including
  committed WAL data, profile revisions, workstation identities and journals.
  Xur checks the snapshot with `PRAGMA integrity_check` before archiving it.
- `files/`: original paths for Xur settings and selected recipes; GPU labels/power
  settings; account/password and API records; session-signing, HTTPS and Hugging
  Face credentials; application update preferences and verification keys;
  NetworkManager profiles; time/NTP configuration; account UID/GID and password
  records; storage mounts; local SSH, firewall, container, systemd and authorization
  configuration; machine identity; Tailscale state; and Moonlight pairing keys,
  certificates and streaming port reservations.
- `configuration.json`: a readable copy of profiles and workstation definitions
  taken under the same lock as the database snapshot.
- `metadata.json`: format version, backup ID, UTC start/completion timestamps,
  Xur application/version, host/OS/architecture, consistency limits, exclusions,
  absent optional paths, and a per-entry inventory. Payload records include
  SHA-256, size, source/ZIP path, Unix permissions, UID/GID and modification time.
  Directories and symbolic-link targets are recorded too. Metadata does not hash
  itself; hashes detect corruption, not a maliciously replaced archive.
- `RESTORE.md`: recovery steps and precautions, included with every download.

Links are recorded without following their targets. Symlinked configuration
directories, unexpected special files, unreadable files, detected changes during
capture or size/file-count limits abort the backup instead of silently omitting
content. Limits are 256 MiB per file, 1 GiB total captured data and 20,000 entries.
Optional paths that do not exist are listed in metadata. Archive staging is
owner-only and removed on failure or when the download stream closes.

SQLite is captured consistently. The other files are captured individually over
the recorded interval, not in one atomic snapshot of all services. Avoid changing
configuration while backing up. A running profile change must finish first.

## Separate data backups are still required

The ZIP excludes models, games, home directories, container images/volumes/writable
layers, logs, caches, benchmark history, OS/application binaries and external
storage contents. Locally built container images must be exported separately;
Xur deletes build contexts after preparation, so preserve original Dockerfiles
and build files separately too. Hardware-bound identity keys may not be portable.

## Restore

Automatic restore is not implemented. Read the bundled guide and verify the
checksums before use. Install a compatible Xur version and recover data disks
separately. Stop the relevant services before replacing files. Never blindly
extract the archive over a live host or run two machines with the same identity.

Merge user/group records with the correct UID/GID instead of replacing a fresh
OS's system account files. Review network, storage and GPU assignments for the
replacement hardware. Restore original ownership and restrictive permissions,
recreate reviewed symbolic links, and restore SELinux labels with `restorecon`.

With Xur stopped, replace `profiles.db` and remove old WAL/SHM sidecars. Review
active profiles and operation journals before starting workloads: processes and
in-progress operations are not restored by the archive. Restore Tailscale state
with tailscaled stopped, or re-enroll if the identity cannot be recovered. Omit
the session-signing key if existing sessions should be invalidated.
