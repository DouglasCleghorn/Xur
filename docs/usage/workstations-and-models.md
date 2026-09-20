# Workstations, model storage and monitoring

Workstations lists every saved workstation, including stopped desktops and GPUs
without a connected screen. Disconnected cards say **No display detected**.
The ASPEED onboard adapter has a readable name. Profiles still allow one active
native workstation at a time; persistent users retain their home and Steam data.

Load the profile, install Moonlight on the client, and add this machine's address.
Select the computer in Moonlight, then open Connect with Moonlight on the
Workstations page and approve its pending request using the four-digit PIN.
Pairing requires the HTTPS manager (port 8443 by default) or Tailscale HTTPS.
The LAN HTTPS certificate is generated locally and persisted; browser trust
requires accepting or importing that certificate. Settings lists HTTPS URLs.
Windows, Linux and macOS launch shortcuts are available after pairing.

Sunshine 2026.914.233613 is included in the signed application bundle. Its exact
upstream checksum is in `tools/Xur.Streaming/upstream-lock.json`. Both LAN and WAN
encryption are mandatory, UPnP is disabled, and Sunshine's administration UI is
restricted to localhost. The agent pins Sunshine's certificate. Credentials,
client pairings and certificates persist under `/var/lib/xur-streaming` with
restricted permissions. Streaming opens only its required firewall ports while
running. Stopping the workstation stops Sunshine before terminating the desktop.

The desktop and encoder use the selected GPU's permitted device nodes. With no
connected monitor, Plasma uses KWin's virtual display backend at 1920×1080. That
backend needs a render device and a working OpenGL compositor for capture. A
basic display-only VM adapter can display and stream a connected virtual screen,
but may not support headless capture. Capture failure is shown explicitly.
Multi-seat, independent outputs of one card, and per-desk USB routing remain
separate work; see `remaining-work.md`.

The Models page inventories managed downloads on startup and provides an explicit
Search attached storage action. GGUF downloads are stored in `/var/lib/xur/models`;
model sets are under `/var/lib/xur/model-sets`. Hugging Face engine caches use
persistent Podman volumes named `xur-cache-*`, mounted at `/root/.cache` inside
containers. Stopping workloads, changing profiles, application updates and
rebooting do not delete these files or volumes. Temporary download files become
complete files only after validation and durable flush.

Attached-storage searches do not move or delete models. Known unmounted
filesystems are searched through read-only block devices and read-only mounts
with replay disabled where applicable. Encrypted/unrecognized filesystems need
to be made available first. Directory links are not traversed; scan limits and
unreadable paths are reported. Found external folders are inventory entries, not
automatically selected runtime models. Multiple paths can refer to the same
weights, so displayed location sizes are not unique physical disk consumption.

Dashboard network charts sample each non-loopback interface every five seconds.
They show receive/send rates and byte counters with 15-minute, one-hour and
24-hour views. History is in memory and starts afresh on reboot. GPU topology
uses `nvidia-smi topo -m` and NVLink status, resolving indices to PCI identities.
Unknown or inactive links are never reported as active. Profile selection shows
NVLink peer hints without silently changing allocations.

Manager usernames may be email addresses. Account setup offers a local password
generator as well as browser password-manager autocomplete. GPU release failures
now identify owning processes/services. Xur's own display console is released
before desktop ownership is checked; verified driver persistence handles alone
are not treated as a workload.

## Streaming startup failures

Sunshine's DRM adapter and CUDA device selection are set separately. For NVIDIA,
Xur passes the assigned full GPU UUID through `CUDA_VISIBLE_DEVICES`; the existing
user-slice device restrictions still apply. The selected card's DRM node remains
`adapter_name`. See the pinned [Sunshine CUDA initializer](https://github.com/LizardByte/Sunshine/blob/63d35f702ee9e362e43263742981836ec0710384/src/video.cpp)
and [NVIDIA's UUID selection documentation](https://docs.nvidia.com/deploy/topics/topic_5_2_1.html).

A failed requested encoder or an unexpected fallback does not become Ready.
Initial startup failure stops Sunshine and retains an error for the workstation
page. Segmentation faults and aborts do not automatically restart; other failures
are limited to three starts within two minutes. Existing desktop processes stay
running so logs can be inspected and streaming retried separately. The runtime
retains failed units for inspection until explicitly retried or stopped.

The owner's September 16 log shows capture on HDMI-A-1 followed by NVENC
`unsupported device` and a Vulkan fallback crash. UUID selection corrects a
configuration gap; it does not establish the cause of that NVENC driver error.
Physical retesting is still required. No additional GPUs or administrative
capabilities are granted to work around the failure.

Local checks without packaging:

```bash
$HOME/.local/share/xur-build/dotnet/dotnet run --project tests/Xur.Unit.Tests -c Release
python3 tests/Xur.Integration.Tests/streaming-recovery.py
```

The latter uses disposable processes in the local systemd user manager to verify
crash suppression and the retry bound. It does not launch a desktop or Sunshine.
