# Files

Open **Files** and choose **Storage files** for a mounted disk, or **Workstation
files** for a workstation home. Click a folder to open it; breadcrumbs and **Up**
return to its parent. Click the displayed directory path to copy it.
Mounts, folders, breadcrumbs, **Up**, file-location tabs and the **Open folder**
menu item are links: middle-click or Ctrl/Cmd-click opens the destination in a new
tab, with the correct storage mount or workstation user and folder selected.

Storage files works as a disk-usage browser: **Disk usage** measures allocated
space below each mount or folder, sorts largest first, and shows bars relative
to the largest listed item. The current folder's allocated total appears above
the grid. File rows also use allocated blocks, so sparse files show their disk
usage rather than their apparent length. Workstation file rows retain file length.
Use Enter or Right in a folder's name cell to open it, and Left to go up.

Filesystem capacity and free space are separate from directory usage. Mounts
sharing a filesystem are labeled; bind mounts can expose the same data and
subvolumes can overlap. Do not add mount-row measurements together. The same
filesystem capacity beside several mounts is expected, but their directory
usage can differ.

Use the search box to search the grid’s columns together. Click a column heading
to sort, or its filter icon to filter that column. **Clear filters** resets both.
Listings are read afresh when you open or refresh a folder. Each response is
limited to 500 entries; when there are more, **Find name on server** searches the
folder’s names beyond that limit.

Each row has an action menu with icons:

- **Copy path** copies that item’s full path.
- **Download** downloads a file directly; **Download ZIP** streams a folder as a
  compressed ZIP, including empty folders. Links and special files are omitted.
- **Download original ZIP** keeps an existing archive unchanged; **Download ZIP
  (compressed)** rewrites its entries with compression.
- **Download ZIP (uncompressed)** streams a ZIP using stored entries, preserving
  multiple files in one download without compression. An existing ZIP is read
  entry by entry and rewritten; nothing is extracted onto the appliance. Encrypted
  ZIPs can be downloaded in their original form only.
- **Rename / move…** accepts a destination relative to the selected mount or home.
  The destination parent must exist; existing items are never overwritten.
- **Delete…** requires confirmation before permanently removing an item. Deleting
  a folder removes its contents. Read-only mounts do not offer write actions.
- **Refresh folder size** measures that folder again.

Only folder-size measurements are cached, for 30 minutes. Storage measures the
current folder and all listed child folders in the background, including rows
outside the viewport, with immediate child-folder sizes preloaded for navigation.
**Refresh** in Storage files reloads the listing and measures usage again; the
row menu refreshes one folder or mount. Workstation files measures visible folders.
No directory listing is retained in this cache. Moves and deletes invalidate
sizes. Hardlinks are counted once within each scan; independent scans of
overlapping paths can include the same data. A `≥` marks a partial measurement;
errors appear as unavailable. Size scans have time, entry and depth limits, with
at most two running at once. A limited listing sorts only its first 500 entries,
so it may omit larger items; the folder total still scans beyond those entries
within the scan limits. These are directory measurements, not exclusive physical
space accounting for shared extents, snapshots or filesystem metadata.

File contents are streamed through the agent and control server in bounded
buffers, including archive downloads. ZIP libraries retain archive entry metadata,
but do not hold the archive’s file contents in memory. Links and nested mounts are
not followed. A file that changes or becomes unreadable while downloading can
interrupt the download. A failed recursive delete can leave a partially deleted
folder; refresh its listing before retrying.
