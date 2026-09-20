# Historical implementation status

This document records the earlier offline installer implementation. Current media
uses the [online installer](../architecture/online-installer.md); the embedded OS
payload and historical media results below do not describe or validate that path.

The current build includes the shared web installer and installed system UI,
keyboard console navigation, LAN login, Tailscale browser authorization and
local QR, read-only answer scanning, available-disk selection, disk identity
revalidation, and Anaconda installation of an embedded, verified upstream Bazzite Desktop image.

JWT sessions survive installation and reboot. The reboot page waits for a new
server boot before returning to the installed dashboard. That dashboard shows
real system metrics, running workload instances and system services. Profile
actions manage the workload set. It does not include installer controls.

Login sessions take effect immediately without a JWT not-before timestamp.
This avoids rejecting an existing session when installer startup resets the
live clock backward from the hardware clock. Signed expiry remains enforced.
The media suite checks authenticated progress throughout Anaconda startup and
exercises browser/API sessions across clock corrections and a manager restart.

Saved workload profiles now have a browser editor, exact change preview, SQLite
revision/action journal, typed Podman lifecycle operations and a separate streaming
gateway. The picker includes a native gaming workstation, live Unsloth GGUF and Hugging
Face model search, and the pinned upstream vLLM-Omni supported-model list.
GGUF imports pin revisions, file hashes, engine digests and Unsloth sampling
settings. A small CPU recipe remains bundled for smoke tests. See
`docs/usage/profiles.md` and the matching release acceptance file for executed checks.

The upstream Bazzite NVIDIA driver and container toolkit are included. Reference
multi-GPU model deployments and multiple independent workstations remain
unfinished. One native Plasma workstation now starts and stops through the
agent with a selected DRM GPU and dedicated user. Its VM test renders a Vulkan
window alongside a continuing CPU model and exercises reboot restoration.
Physical HDMI audio and per-station USB routing are not established by that
software-rendering test. The current profile implementation
is not yet the complete four-3090 AI/workstation system.

The build manifest binds the ISO, embedded binaries, source and selected test
receipts. The tests use UEFI VMs with file-backed storage. Operator Tailscale
account authorization is separate from testing the real client's login link
and QR output. See `dist/acceptance.json` for the executed checks.

Xur is installed as a separate versioned application bundle under /var, with
persistent systemd startup configuration. Desktop login managers are masked;
KDE and Steam do not start merely because they are installed. OS updates come
directly from Bazzite and can be managed from the website or terminal menu.
An independent system timer stages updates without running the Xur manager.
Signed application updates now run through the web manager, terminal menu and API using a configurable LAN repository. Model and workstation continuity, request draining, health rollback and interrupted-activation recovery are covered by real VM tests. Independent engine update transactions remain outstanding.
