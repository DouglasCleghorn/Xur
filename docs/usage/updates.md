# Updates

Xur installs the upstream Bazzite KDE Desktop NVIDIA-open image from
`os/bootc/upstream-lock.json`. The live installer remains a separate Fedora
image. No Xur-derived OS image is published or required for installed updates.
The initial upstream image is digest-pinned and signature-verified in the
builder. OSBuild changes the offline container layer representation; its copy
removes transport signatures after verification, without modifying the upstream
filesystem. Subsequent network updates enforce Bazzite's container signature
policy.

## Operating system

The installed web manager has an Updates page. The terminal menu has OS updates,
and System.CommandLine exposes `xur updates status --json`, `check`, `stage`,
`rollback`, `enable`, and `disable`.

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

A persistent `xur-os-update.timer` runs the small host update script daily,
without the .NET manager or agent. It fetches directly from Bazzite's upstream
registry. It needs neither Xur's GitHub repository nor a Xur server. The first
check occurs 15–45 minutes after startup. Automatic staging is on by default;
Pause automatic updates disables it. Reboot is always an explicit operator
operation in this implementation. A scheduled reboot window is not implemented.

Bazzite's hardware-setup service keeps its device fixes. Its service-specific
PATH defers the pinned script's final reboot command, leaving any staged kernel
arguments visible in Xur's pending-deployment state. The installer supplies
Bazzite's common Bluetooth argument up front to avoid an unnecessary first-boot
deployment on ordinary desktop hardware. This integration must be rechecked
when upstream changes that script.

The installer masks Bazzite's competing `uupd.timer` and bootc's auto-apply timer
so they cannot bypass Xur's pause or trigger competing update operations. The
installed update script invokes upstream bootc, enforces signatures, serializes
operations with a file lock, and retains a durable receipt. The manager observes
actual bootc state after interruption instead of assuming a command succeeded.

The OS update includes upstream kernel, NVIDIA driver and desktop components.
It does not update the selected Xur application bundle or pinned engine/model
versions. Graphics and compute compatibility can change with upstream driver
updates; previous OS deployment rollback remains available. Automatic failed
boot recovery is not claimed by Xur.

## Application packaging and GitHub

The installer seeds a versioned bundle under `/var/lib/xur/app/releases`, with
`current` selecting the installed version. It includes the control application,
agent, gateway, catalog and host integration scripts. Each bundle has a content
manifest and host ABI version. Persistent systemd units in `/etc` start these
services after boot; local SELinux mappings cover their executable paths.

`python3 eng/build-app-bundle.py` produces `dist/xur-app-x86_64.tar.gz` and its
checksum without building an OS image. The application GitHub Actions workflow
builds this archive and attaches it to published GitHub releases. The separate
manual ISO workflow requires a Linux x86-64 runner labeled `xur-iso`, with the
KVM/Fedora-builder prerequisites in `docs/development/build.md` and sufficient disk space.
Neither workflow participates in installed OS updates. Adding workflow files
locally does not activate them until they are pushed to GitHub.

Signed application download, activation and rollback are implemented separately
from OS updates. Clients configure the development computer's IP or domain on
the Updates page. The terminal menu and authenticated API expose the same
operations. The updater drains requests, preserves running workloads, and has
health-check rollback and interrupted-activation recovery. Schema 1 bundles
are supported; incompatible schema changes are rejected until an explicit
migration is supplied. See [Application updates](application-updates.md) for
server setup, publishing, APIs and tests.

Independent engine Update buttons remain unfinished. Existing workloads retain
their pinned engine image when the Xur application is updated.

References:
- https://docs.bazzite.gg/General/FAQ/
- https://bootc.dev/bootc/upgrades.html

## Tools and engines

The web Updates page lists the pinned catalog versions of llama.cpp (CPU, CUDA,
ROCm and Vulkan), vLLM, vLLM-Omni and bundled Sunshine. Engine images show whether
that exact digest has been downloaded. Xur update buttons use the application
bundle lifecycle; they do not rewrite engine pins in saved workloads or restart
running streams.

Tailscale, Podman, Plasma, Mesa, the running kernel and NVIDIA driver report host
versions and use the OS update lifecycle. Their buttons check or stage the whole
OS deployment. A reboot activates it. Missing tools are shown as unavailable or
not installed rather than given a build-time version.
