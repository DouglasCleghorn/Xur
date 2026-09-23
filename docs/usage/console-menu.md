# Console menu

The installed console offers **Status and login**, **Tailscale QR**, **Network settings**,
**Hardware**, **Logs**, **Updates**, **Power**, **Server name**, and **Local setup**.
The installer offers **Setup and installation**, **Network settings**, **Hardware**,
**Logs**, and **Power**. Web management and Tailscale begin after installation.
The physical/serial console and the interactive `xur` command share the update
and power screens.

On the physical console, use arrows or a number to select a row, then Enter.
Escape or 0 returns to the parent screen. PgUp/PgDn scroll long status content.
The text menu accepts a row number followed by Enter; 0 returns or exits.

**Updates** shows installed versions, the selected Xur channel, and the latest
Update All progress and results. Choose **Update All** to check and stage OS
updates, then check and update Xur. Each component reports its own result, even
if the other fails. The job survives manager restarts. It never reboots
automatically and does not replace the engine versions pinned by saved workloads.
An existing queued OS deployment is preserved.

The screen refreshes every five seconds while open. After a manager restart,
reopen Updates to see the persisted operation. If status cannot be loaded,
**Refresh status** retries. Conflicting update actions remain unavailable while
another update runs. The console displays rejected requests and retains those
messages through background refreshes.

**Xur application** and **Operating system** retain individual check and update
actions. Rollback appears only when a previous version exists; an already queued
OS deployment must be completed before another can be staged. Automatic OS
updates use explicit **Pause** and **Enable** labels. Select the Xur update
channel in web **Settings → Update channel**.

When an OS change is queued, **Reboot to finish** appears in Updates and the OS
screen. **Power** also offers reboot and shutdown. Both require a confirmation
that defaults to Cancel and explains that running workstations and AI services
will stop. Cancelling returns to the screen where the action began.

Noninteractive commands keep their explicit behavior:

```bash
xur update-all status --json
xur update-all start
xur application-updates status --json
xur updates status --json
xur reboot
xur shutdown
```

The power commands above execute directly; confirmation belongs to the
interactive menus. Update All's local HTTP routes are `GET /local/update-all`
and `POST /local/update-all/start`, available only through the root-private
control socket. Update routes reject installer mode.

**Network settings** edits wired IPv4/IPv6 addresses, gateway and DNS, with a
two-minute keep/revert window. See [networking and answer YAML](answer-file.md).

## Initial server setup

The initial setup boot prompts for the **server name**. Enter a hostname using
letters, numbers and hyphens, then save. It persists through installation and
later boots. Escape skips the prompt for now; **Server name** lets you set or
change it later. Saving the name is required before disk approval. New Tailscale enrollment uses the saved name, and renaming an
already-enrolled server updates its advertised Tailscale name when available.
Disk installation still requires its own explicit approval.

## Wi-Fi

Choose **Network settings → Wi-Fi setup**. With one adapter, Xur opens the nearby
network list directly. With multiple adapters, choose the interface first. If
Wi-Fi is off, select **Enable Wi-Fi**; hardware airplane-mode switches must be
unblocked on the machine.

Select an SSID from the signal-strength list. WPA2-Personal and WPA3-Personal
networks prompt for a password; open networks connect directly. Password input
is hidden on the physical/serial console and interactive `xur` terminal. Spaces
in passwords are preserved. Enter connects; Escape cancels (or `/cancel` in a
line-oriented session). Long network lists remain navigable with the arrow keys.
Use **Scan again** to refresh the list. WEP, enterprise authentication and hidden
SSIDs require separate configuration.

Successful connections are saved for automatic reconnection and copied into the
installed system. Failed activation restores the previous connection. Credentials
are stored in NetworkManager profiles with owner-only permissions and are never
included in process arguments or console frames. The answer YAML schema remains
for wired IP configuration; Wi-Fi credentials are entered in this menu.

## Display behavior

The console uses larger text on high-resolution screens: 32-pixel glyphs at
2560×1440 and 48-pixel glyphs at 3840×2160. Cloned displays on one GPU use the
smallest screen's size so the menu and QR remain visible.

The Serve login QR appears automatically when enrollment completes, without
leaving and reopening the screen. Display clients check for changes every
100 ms, reuse unchanged frames, and redraw only changed rows. Serial writes run
outside the menu lock so a slow serial terminal does not hold up local input.

**Logs** stays on the menu's input terminal. Choose **Back to menu**, press Enter,
or press Escape / 0 to return; PgUp/PgDn scroll the log. The separate Alt+F2 log
terminal also accepts Enter or Escape to switch back to the menu.

Tailscale connection and HTTPS proxy status are shown separately. Xur checks and
restores its private Serve route after reconnecting or restarting, and shows the
login QR once that route is configured. If tailnet HTTPS is unavailable, the
console explains the issue and retains the LAN management addresses.

Network addresses update in place when a cable is connected, DHCP supplies an
address, or an adapter disappears. Link/address notifications trigger an immediate
refresh, with periodic refresh as a fallback. You do not need to leave and reopen
the status or network screen.

## Setup without a browser

On installer media, choose **Setup and installation**. Set the server name,
configure wired networking or Wi-Fi, then review and approve a disk. Saving the
name opens networking immediately. **Continue to disk selection** advances from
networking or a successful Wi-Fi connection. Back moves to the previous step;
reopening setup during installation returns to progress.
Internet is required for the Bazzite download. There is no live web listener
or Tailscale enrollment. After reboot, use the console access code in the
browser to create the required administrator account. This supports Chrome
generated passwords without entering them at the console.

**Choose installation disk** lists model, size, device path and disk identity.
Boot media and other blocked disks cannot be selected. Review the erase plan and
unaffected disks, then choose **Yes** at the erase confirmation. **No** is selected by
default; Enter on No, Escape or 0 cancels.
Cancellation, an expired plan or a changed disk identity requires a new review.
Progress updates in the console; completion offers a separately confirmed reboot.
Failures never automatically retry disk erasure. Choose **Installation logs** on
the progress screen to read Anaconda, storage, download and Xur configuration
errors. **More lines** / **Previous lines** navigate the report; **Back to progress**
returns without restarting installation. Capture the error before rebooting:
live logs are temporary. Retrying requires rebooting the installer and reviewing
and approving the disk again. Locked LUKS containers are not
opened to search for answer files; they are listed as skipped and do not block
selection of an otherwise eligible disk. Erasing that disk destroys its encrypted
data. Invalid or ambiguous answers and actual discovery errors still block setup.

The installer starts the bundled app without waiting for internet. Update checks
run separately and report their status without restarting setup. A saved server
name is required before starting Tailscale enrollment from either console or web.

Wi-Fi pages identify the adapter, driver, firmware, state and NetworkManager
reason. An unavailable adapter shows **Refresh adapter** and troubleshooting
details immediately; it does not start a scan until ready. Scans show progress
and allow Back while waiting. An initial empty result is checked again, and scan
failures are distinguished from completed scans with no named networks.

## Idle screen

The setup console goes completely black after five minutes without keyboard
input, including while logs or installation progress are changing. The server
and installation continue running. The first key only wakes the display; press
again to operate the menu. The black screen retains the HDMI signal instead of
putting the monitor into power-save. Workstation desktops keep their own idle
settings.

The display console selects the monitor's preferred mode instead of inheriting
firmware timing. Connected outputs that stay disabled or in display power-save
receive bounded recovery attempts, including when another output is healthy.
Workstation-owned GPUs are excluded from console recovery.

On installation failure, Xur tries to save a new `xur-diagnostics-*.txt` report
to a writable installer USB filesystem. Progress shows whether saving succeeded.
**Save logs to USB** also lets you select a writable USB volume after installation
stops. Reports contain Anaconda and configuration log tails, the bundle identity,
and display/kernel diagnostics; credentials are redacted. Files are flushed to
the drive, and drives mounted only for export are unmounted afterward. Existing
files are kept, and the approved installation disk is excluded.

Read-only media (including raw/DD ISO filesystems) cannot store reports. Insert
a second FAT32 or exFAT USB drive and choose **Refresh USB drives**. Xur does not
format drives or change their read-only protection to save logs. Save before
rebooting, which clears live logs.
