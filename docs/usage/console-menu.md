# Console menu

The installed console offers **Status and login**, **Tailscale QR**, **IP addresses**,
**Hardware**, **Logs**, **Updates**, and **Power**. The installer omits Updates.
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
