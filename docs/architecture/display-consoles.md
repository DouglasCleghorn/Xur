# Consoles on unassigned displays

See the [console menu guide](../usage/console-menu.md) for updates, power actions,
and keyboard navigation.

Each connected output on an unassigned GPU displays the shared terminal menu.
Loading a workstation releases the console on its selected GPU before starting
Plasma. Other cards retain their console. Stopping the workstation waits for
its complete teardown before restoring the console.

This follows the current whole-GPU workstation assignment: all connected outputs
on a selected card belong to that desktop. Separate desktops per connector remain future work.
Multiple simultaneous workstations use separate GPUs and seats; see
[multiple workstations](multiple-workstations.md).

The agent manages a small kmscon child per unassigned display adapter. It uses
DRM 2D software rendering, without an idle desktop or 3D compositor. Outputs on
the same card clone the menu. Connected DRM cards are discovered through the
existing PCI/VMBus/platform inventory, including virtual adapters.

A native client reads the shared Spectre.Console frame through the root-private
control socket. It retains the last complete frame during manager reconnection;
no frame, access code or QR is written to its journal. Keyboard navigation stays
on the existing local VT, and serial access remains available. While a desktop
owns the local keyboard, the other screens continue displaying the menu.

The signed app bundle includes the renderer under `agent/console`, using the
existing installed executable labels. Each child receives only its assigned DRM
card through its systemd device policy, and no input devices. App updates restart
console children on the new bundle without taking over a running desktop.

Build with `python3 eng/build-console.py`. The pinned kmscon source, two small
integration changes, native frame client, dependency versions and file checksums
are recorded in the cached runtime receipt. The build runs in the disposable
Fedora builder and is reused by `eng/publish.sh` when its inputs match.

The VM test `tests/Xur.Media.Tests/check-display-consoles.py` uses two real QEMU
DRM adapters and Plasma, checking console screenshots, selected-card handoff,
unchanged console PIDs on the other card, and three load/unload cycles. In that
VM a console used approximately 16 MiB of systemd-accounted memory and two
1280×800 32-bit scanout buffers. Actual display modes and drivers affect usage.
