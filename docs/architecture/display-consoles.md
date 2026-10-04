# Consoles on unassigned displays

See the [console menu guide](../usage/console-menu.md) for updates, power actions,
and keyboard/controller navigation.

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
control socket every 100 ms. Unchanged frames are cached and only changed rows
are written to kmscon. Slow serial writes do not hold the shared menu lock.
It retains the last complete frame during manager reconnection;
no frame, access code or QR is written to its journal. Keyboard navigation stays
on the existing local VT, and serial access remains available. While a desktop
owns the local keyboard, the other screens continue displaying the menu.

The control service also reads gamepad evdev devices on seat0, using the
[Linux gamepad mappings](https://docs.kernel.org/input/gamepad.html) for Xbox One
USB and HID controllers. A shared input gate routes D-pad/left-stick navigation,
A/B and bumper scrolling through the same menu handling as keyboard input.
Discovery repeats while the menu is running; disconnection closes the descriptor
and reconnection starts with the current button/axis state. Stick hysteresis
filters drift, and only navigation/scrolling repeat. A/B require fresh presses.
After an evdev queue overrun, queued events are ignored through SYN_REPORT and
the current state is queried before accepting more input, following the
[evdev synchronization protocol](https://docs.kernel.org/input/event-codes.html).

Gamepad readers use nonblocking, read-only descriptors without exclusive grabs.
They close when the active VT leaves tty3/tty2, or when udev seat ownership or
device identity changes. Missing udev records and `xur/` synthetic input are
excluded. Seat ownership is rechecked before dispatching input, so a workstation
controller cannot operate the setup menu. Display children still have no input
devices. Bluetooth pairing remains outside gamepad navigation.

In text fields, a packet-synchronized two-stick keyboard normalizes all four
stick axes using their reported ranges. The left wheel selects the high digit
and the right wheel the low digit of a character index. Each alphabet uses
`ceil(sqrt(character count))` slices: six for lowercase, uppercase and symbols,
four for numbers. Trigger hold previews; trigger release inserts once. Radial
and angular hysteresis stabilize selection, and centered sticks or unused
combinations cancel insertion. Bumpers switch alphabets; X deletes and Y inserts
a space. All printable ASCII characters remain available, including passwords
with spaces and punctuation. Ordinary keyboard entry also remains available.

Controller text input uses complete SYN_REPORT packets so the trigger release
sees both stick coordinates from the same packet. Wake, text-field transitions,
disconnects and overruns cancel pending gestures. Held buttons and triggers in
state snapshots require release before rearming. The local preview is rendered
separately from diagnostic snapshots, and entered secret text remains masked.

The signed app bundle includes the renderer under `agent/console`, using the
existing installed executable labels. Each child receives only its assigned DRM
card through its systemd device policy, and no input devices. App updates restart
console children on the new bundle without taking over a running desktop.

Build with `python3 eng/build-console.py`. The pinned kmscon source, local
integration patches, native frame client, dependency versions and file checksums
are recorded in the cached runtime receipt. The build runs in the disposable
Fedora builder and is reused by `eng/publish.sh` when its inputs match.

The VM test `tests/Xur.Media.Tests/check-display-consoles.py` uses two real QEMU
DRM adapters and Plasma, checking console screenshots, selected-card handoff,
unchanged console PIDs on the other card, and three load/unload cycles. In that
VM a console used approximately 16 MiB of systemd-accounted memory and two
1280×800 32-bit scanout buffers. Actual display modes and drivers affect usage.

Font size follows the preferred mode of connected outputs: Unifont's 16-pixel
steps preserve at least 45 rows on larger screens, using 32 pixels at 1440p and
48 pixels at 4K. Cloned outputs use the smallest connected mode. A changed font
size restarts only the affected unassigned display console. Serial terminals
continue to use their own font settings.

Console reconciliation also compares the connected outputs, complete mode list
and a hash of EDID. A late mode list or monitor replacement restarts only the
affected unassigned GPU's console, even when font size is unchanged. If all
connected outputs remain disabled while the console service is active, recovery
waits 15 seconds and permits at most three restart attempts, at least 30 seconds
apart. An enabled output resets that budget. Workstation-owned and handoff GPUs
are excluded. This does not diagnose every HDMI cable, firmware or driver fault;
physical AMD HDMI recovery still requires hardware testing.

After ten minutes without local keyboard or controller activity, the control service returns
a black frame with an explicit power-save header over its root-private socket.
The native client sends the corresponding OSC to its kmscon child, which sets
DPMS Off on its assigned displays. Displays without DPMS support retain the
black frame. The renderer continues reading its PTY while asleep so the next
keyboard or controller input can restore DPMS On and repaint the menu. Newly connected
outputs inherit the sleep state.

The control service maintains a root-private `console-sleep` marker in its run
directory. Display recovery excludes deliberate sleep and resets its recovery
budget; the wake input removes the marker before menu handling, including disk
approval. Control startup clears stale sleep state. Frame polling, log updates
and network changes never count as input. The server and installation continue
running, and workstation desktops retain their own display power settings.
