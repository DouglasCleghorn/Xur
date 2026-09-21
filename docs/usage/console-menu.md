# Console menu

The installed console offers **Status and login**, **Tailscale QR**, **Network settings**,
**Hardware**, **Logs**, **Updates**, **Power**, and **Server name**. The installer omits Updates.
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
change it later. New Tailscale enrollment uses the saved name, and renaming an
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
