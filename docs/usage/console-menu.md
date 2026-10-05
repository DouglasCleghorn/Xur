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

An Xbox One controller can also operate the physical console once Linux detects
it as a gamepad. Connect it by USB, or use an already paired Bluetooth connection
or wireless receiver supported by the host. No menu setting is needed; connecting,
disconnecting and reconnecting a controller while the menu is running is supported.

| Controller control | Menu action |
| --- | --- |
| D-pad up/down or left stick up/down | Select a row; hold to repeat |
| A | Open or confirm the selected row |
| B | Back or cancel |
| LB / RB | Scroll backward / forward through long content |

A and B require a new press for each action. Reboot, shutdown and disk erasure
keep their existing confirmation screens and default to Cancel or No.
Bluetooth pairing is configured outside this menu.
Controllers assigned to workstation seats, including streaming input,
do not control the setup menu. Input is accepted while the setup or log terminal
is active, and paused while another terminal or workstation desktop is active.

Text fields such as server names, IP addresses and Wi-Fi passwords also support
a two-stick keyboard. Hold **LT or RT**, point the **left stick** at a character
group, then point the **right stick** at a character within that group. The left
wheel is the most significant digit; the right wheel is the second digit.
Both wheels show their choices and the selected character is previewed locally.
**Release the trigger to type that character once.** If both triggers are held,
release both. Centering either stick or pressing B while holding the trigger
cancels the character without leaving the text field.

Holding a trigger opens a centered overlay over the dimmed menu. Your current
text appears above two wheels; highlighted sectors and stick-position dots show
each selection, with the character preview between them. Each wheel's center
means cancel. Releasing shows **Typed** feedback for half a second; the overlay
stays open for 1.2 seconds between gestures. Password text and Typed feedback stay
masked. Small terminals use compact wheels or a selected-group strip so the
preview, text and cancellation controls remain visible. Keyboard entry dismisses
the overlay, and field changes or controller disconnection clear it immediately.

**LB/RB** cycle lowercase, uppercase, numbers and symbols. Letters and symbols
use six slices per wheel; numbers use four, the minimum needed for ten digits.
Slices start at up and proceed clockwise. Empty combinations insert nothing.
**X** deletes, **Y** inserts a space, and **A** submits the field after the trigger
is released. **B** without a held trigger cancels the field. A normal keyboard
continues to work. Password text stays masked, and diagnostic console snapshots
omit the transient character preview. Waking the display, reconnecting a
controller, changing fields or recovering an input overrun cancels an unfinished
typing gesture; start a fresh gesture to type.

**Updates** shows installed versions, the selected Xur channel, and the latest
Update All progress and results. Choose **Update All** to check and stage OS
updates, then check and update Xur. Each component reports its own result, even
if the other fails. The job survives manager restarts. It never reboots
automatically or restart running model engines. Engines check their latest
upstream image at the next start; selected model weights and settings are retained.
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

On the installed system, you can also configure Wi-Fi in the web manager under
**Settings → Manage Wi-Fi and IP settings → Wi-Fi** (`/settings/network`).
Keep Ethernet or another reachable connection active while setting it up. Enable
Wi-Fi if needed, choose **Scan for networks** for the adapter you want, select an
SSID, and enter its password. Open networks connect without a password. The page
shows connection errors and the new address after a successful connection. If
you change the Wi-Fi connection used by your browser, reconnect at the new address
and refresh to check its status. Wi-Fi setup uses the same saved profiles and
failure recovery as the console.

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

After ten minutes without keyboard or controller input, the setup console puts its monitors
into power-save, including while logs or installation progress are changing.
Displays without power-save support show a completely black screen. The server
and installation continue running. The first key or mapped controller action only wakes the display; press
again to operate the menu. Waking can take a few seconds while the monitor
restores its HDMI or DisplayPort connection. Workstation desktops keep their
own idle settings.

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
