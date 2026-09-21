# Files

Open **Files** and choose **Storage files** for a mounted disk, or **Workstation
files** for a workstation home. Click a folder to open it; breadcrumbs and **Up**
return to its parent. Click the displayed directory path to copy it.

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

Only folder-size measurements are cached, for 30 minutes. Visible folders are
measured in the background, with their immediate child-folder sizes preloaded
for the next navigation. No directory listing is retained in this cache. Moves
and deletes invalidate sizes. Measurements show allocated bytes; file rows show
file length. A `≥` marks a partial measurement. Size scans have time, entry and
depth limits, with at most two running at once.

File contents are streamed through the agent and control server in bounded
buffers, including archive downloads. ZIP libraries retain archive entry metadata,
but do not hold the archive’s file contents in memory. Links and nested mounts are
not followed. A file that changes or becomes unreadable while downloading can
interrupt the download. A failed recursive delete can leave a partially deleted
folder; refresh its listing before retrying.
