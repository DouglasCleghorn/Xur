# Updates

The online installer installs the upstream Bazzite KDE Desktop NVIDIA-open image
directly from `ghcr.io/ublue-os/bazzite-nvidia-open:stable` and enforces the
upstream signature policy. The ISO contains a separate Fedora
live environment and Xur, without an embedded Bazzite OS payload. Internet access
is required. `os/bootc/upstream-lock.json` is a historical reference, not the
version selector for new online installations.

Installed OS updates use the same upstream Bazzite channel. No Xur-derived OS
image is published or required. Xur's Stable/Nightly application channel does not
change the Bazzite channel. See [online installer](../architecture/online-installer.md).

## Operating system

The installed web manager has an Updates page. The terminal menu has OS updates,
and System.CommandLine exposes `xur updates status --json`, `check`, `stage`,
`rollback`, `enable`, `disable`, and `skip`. `xur updates schedule` opens the
same schedule editor as the console's OS updates menu.

Both interfaces use typed agent operations on the private Unix socket. Browser
mutations require authentication and CSRF protection; bearer API clients use
`GET /api/updates` and `POST /api/updates` with an action from the command list.
The browser cannot supply an image reference or arbitrary shell command.

The page shows installed, available, staged and previous deployments, automatic
update settings, operation state and logs. Checks do not restart applications.
Update stages Bazzite's signed stable channel for the next reboot. Reboot to
finish uses the existing reconnect page and retains the session signing key.
Rollback queues the previous OS deployment; it does not roll back application
data. A pending deployment is never replaced automatically.

A persistent `xur-os-update.timer` runs the small host update script every minute,
without the .NET manager or agent. It fetches directly from Bazzite's upstream
registry. It needs neither Xur's GitHub repository nor a Xur server. The first
timer tick occurs a minute after startup; update checks run only before a
scheduled window. Automatic staging is on by default, scheduled for **Sunday
at 03:00 in the server timezone**, with **15 minutes of advance notice**.
Existing installations keep their saved On/Paused setting and any saved
schedule. Installations without a saved schedule use this Sunday default.

Set the time, days and 5–120 minutes of advance notice under **Updates →
Automatic update schedule**, or **Console → Updates → Operating system →
Set automatic update schedule**. Saving a schedule preserves On/Paused.
Pause automatic updates cancels an announced window and disables future windows.
Reboot remains an explicit operator action.

Before each window the host checks whether a newer OS deployment is available.
Only an available update triggers an announcement: a live banner throughout the
web manager, a notice on the physical/serial console, and an actionable Plasma
notification on each running workstation. **Skip this window** cancels that one
window for the whole host and leaves the recurring schedule enabled. On the
console, open Updates and choose Skip this window; the CLI equivalent is
`xur updates skip`. Workstation notifications run as the desktop user inside
their existing device-restricted slice; their Skip action grants no administrator
credentials or general management access.

A late or slow availability check leaves the full configured notice period
before staging. Skips survive service restarts and reboot. A missed window is
not staged later without notice; the next scheduled window is used. The timer
allows up to two minutes of dispatch delay for an already announced window.
Changing the schedule cancels its old announcement. Queued deployments are
never replaced automatically, and staging failures retain the operation log.

The authenticated API uses the existing `POST /api/updates` route:

```json
{"action":"schedule","schedule":{"time":"03:00","days":[6],"warningMinutes":15}}
```

Days are Monday=0 through Sunday=6. `GET /api/updates` includes `schedule`,
`timezone`, `nextWindow`, and the current `window` when one is announced. Times
in window status are Unix seconds. Send `{"action":"skip","windowId":"…"}`
with that window's ID; an expired or changed ID is rejected. Browser forms keep
the existing authentication and CSRF protection.

Home's Reboot control remains available during profile changes, failed operations
and unavailable update status. Confirming reboot interrupts running workloads and
unsaved workstation work. Installation, OS staging and OS rollback temporarily
block power actions; the manager shows the reason instead of entering the reboot
waiting page. Update checks and a completed, queued deployment do not block reboot.

Bazzite's hardware-setup service keeps its device fixes. Its service-specific
PATH defers the pinned script's final reboot command, leaving any staged kernel
arguments visible in Xur's pending-deployment state. The installer supplies
Bazzite's common Bluetooth argument up front to avoid an unnecessary first-boot
deployment on ordinary desktop hardware. This integration must be rechecked
when upstream changes that script.

The installer masks Bazzite's competing `uupd.timer` and bootc's auto-apply timer
so they cannot bypass Xur's pause or trigger competing update operations. The
installed scheduler invokes the bundled native `xurutil` OS updater for checks
and staging. The native updater invokes upstream bootc, enforces signatures, serializes
operations with a file lock, and retains a durable receipt. The manager observes
actual bootc state after interruption instead of assuming a command succeeded.
Staging first ensures the fixed channel has signature enforcement, then runs
`bootc upgrade` if switching to that same channel did not stage a deployment.
An available update with no staged deployment is reported as a failure, including
through Update All.

The OS update includes upstream kernel, NVIDIA driver and desktop components.
It does not update the selected Xur application bundle or selected model
weights. Model engines check for their latest image at container start. Graphics and compute compatibility can change with upstream driver
updates; previous OS deployment rollback remains available. Automatic failed
boot recovery is not claimed by Xur.

## Application packaging and GitHub

The installer seeds a versioned bundle under `/var/lib/xur/app/releases`, with
`current` selecting the installed version. It includes the control application,
agent, gateway, catalog and host integration scripts. Each bundle has a content
manifest and host ABI version. Persistent systemd units in `/etc` start these
services after boot; local SELinux mappings cover their executable paths.

`python3 eng/build-app-bundle.py` produces `dist/xur-app-x86_64.tar.gz` and its
checksum without building an OS image. **Build and publish release** builds and
checks application candidates on `main` and `release`; Nightly publishes
automatically and Stable requires maintainer approval. A manual dispatch with
`build_iso` builds media in an isolated Fedora VM on a disposable GitHub-hosted
Ubuntu runner. It publishes a separate installer release after inspection and
the matching app publication.
Neither path participates in installed OS updates. See
[installer release automation](../development/installer-releases.md).

Signed application download, activation and rollback are implemented separately
from OS updates. Public GitHub releases are the default source; select a channel
in **Settings → Update channel**. Local build testing accepts a development
server address and its signing public key. The terminal menu and authenticated
API expose the same operations. The updater drains requests, preserves running workloads, and has
health-check rollback and interrupted-activation recovery. Schema 1 bundles
are supported; incompatible schema changes are rejected until an explicit
migration is supplied. See [Application updates](application-updates.md) for
server setup, publishing, APIs and tests.

Model engines update independently at container start. Updating Xur does not
restart running models.

References:
- https://docs.bazzite.gg/General/FAQ/
- https://bootc.dev/bootc/upgrades.html

## Tools and engines

The web Updates page lists the latest upstream channels for llama.cpp (CPU,
CUDA, ROCm and Vulkan), vLLM and vLLM-Omni, plus the bundled Sunshine version.
Engine images show whether the channel has been downloaded locally. Every model
start checks upstream and pulls the current image, including previously saved
selections. Changed images recreate stopped containers while preserving their
model-cache volumes; running models continue until their next start. Download
failures use the newest compatible locally downloaded image and record a warning
in workload logs. Models in the loaded profile automatically restart after an
exit or reboot, checking for latest each time and refreshing their gateway port
once healthy. An intentional unload keeps them stopped. Xur update buttons
use the application bundle lifecycle and do not restart running streams.

Tailscale, Podman, Plasma, Mesa, the running kernel and NVIDIA driver report host
versions and use the OS update lifecycle. Their buttons check or stage the whole
OS deployment. A reboot activates it. Missing tools are shown as unavailable or
not installed rather than given a build-time version.
