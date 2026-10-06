# Steam game storage

Each workstation user has a private Steam installation, login, game library,
saves, shader cache and Proton prefixes. Steam downloads and updates games for
that user normally. Xur automatically deduplicates equal blocks in game files
on supported storage: the files keep separate owners and inodes while identical
data occupies shared physical extents. Writes and deletions affect that user's
files; other users retain their copies.

New installations use Btrfs for root storage, including `/var/home`, with ext4
for `/boot` and FAT32 for EFI. This follows Bazzite's [recommended filesystem](https://docs.bazzite.gg/Gaming/Hardware_compatibility_for_gaming/).
**Btrfs root storage is required going forward.** Older ext4 installations are
legacy and unsupported. Back up their data and reinstall using current media;
Xur provides no in-place filesystem migration. Application updates check the
physical root filesystem before activating bundles marked `btrfs-root-v1`, and
the installed agent independently enforces this requirement. Recovery media and
development runs without an installed-host marker are exempt. Older updaters
that predate this check rely on the new agent's health failure and their existing
automatic application rollback; they do not convert the filesystem.

Each user needs their own game library directory. XFS libraries also qualify
when their filesystem supports reflinks. Copies on different filesystems cannot
share blocks.

The agent starts its first pass two minutes after startup, then waits fifteen
minutes between passes. Each pass runs with idle I/O priority and reduced CPU
priority, and is bounded to ten minutes and 250,000 directory entries. Files
modified within the last two minutes are deferred. A private metadata cache
avoids comparing unchanged file pairs on every pass; changed or replaced files
are reconsidered. Interrupted passes keep successful sharing and retry uncached
pairs on the next pass. Large-file comparisons checkpoint their progress so the
next pass can continue from the last completed range.

The native `xurutil steam share` worker uses reusable `SteamSharing`,
`ExtentSharing` and descriptor-relative file access from `Xur.IO`. It discovers native Steam libraries and the default Flatpak Steam
library, plus additional libraries listed in `steamapps/libraryfolders.vdf`.
Only account-owned regular files of at least 4 KiB in `steamapps/common` qualify.
Candidate copies must have the same path within `common` and the same size.
Matching blocks are shared in 1 MiB ranges, allowing matching portions of
different versions to share too. The kernel verifies equality during every
deduplication request, including when Steam or a game writes concurrently.
See the [Btrfs deduplication documentation](https://btrfs.readthedocs.io/en/latest/Deduplication.html).

Library paths containing symlinks, nested mounts inside `common`, hardlinked
files, and files belonging to another account are excluded. Login records,
`userdata`, `compatdata`, and `shadercache` outside `common` are excluded. Games
that store saves or mods within their installation retain independently writable
files even if some equal blocks are shared. Tiny files, differently named or sized
copies, and shifted matching data may remain duplicated.

This reduces disk use after installation; it does not eliminate repeat downloads
or the temporary disk space needed before a pass. Deduplication is automatic
background maintenance, rather than a shared writable Steam library. Steam
account ownership and game licensing continue to apply separately.

The authenticated `GET /api/steam-storage` endpoint reports whether a pass is
running, its completion time, its last report and any worker error. Reports count
libraries, unsupported libraries, files, attempted and cached pairs, differing
ranges, errors and whether a pass reached its limit. `sharedBytes` counts bytes
accepted for sharing during that pass, **not measured disk savings**: ranges may
already be shared. Use filesystem free space to observe physical storage use.
Unsupported storage is reported and games continue using their normal copies.
Aggregate pass reports also appear in `journalctl -u xur-agent.service` without
usernames or game paths. Cache state is private under
`/var/lib/xur/steam-storage/pairs.sqlite`.

Local verification:

```bash
python3 tests/Xur.Integration.Tests/steam-storage.py
bash eng/test-xurutil.sh
# Create, test and clean up a disposable file-backed Btrfs image (needs btrfs-progs).
sudo bash eng/test-steam-btrfs.sh
# A disposable, already-mounted Btrfs/XFS test filesystem; requires root for UIDs.
sudo python3 tests/Xur.Integration.Tests/steam-storage.py --filesystem .build/test-mount
```

The filesystem test runs the published native fixture, creates four private user libraries, verifies physical extent
sharing, checks differing bytes, independent writes and deletion, and rejects a
bind mount. Generated test data belongs under `.build/`. It does not install or
launch Steam. Fresh Anaconda installation and real Steam workloads need separate
media and hardware validation before release.
