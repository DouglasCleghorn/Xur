# Controllers and workstation assignments

In **Profiles → Edit**, each workstation has an **Individual controllers** list.
Pair controllers with the Xbox Wireless Adapter, turn them on, then reload the
editor. Select one or more controllers for each workstation and save the profile.
Load it through the normal profile review to apply the assignments.

For example, one Xbox One / Series adapter can serve two controllers assigned to
the gaming workstation and another controller assigned to the studio workstation.
Each controller belongs to one workstation at a time. Multiple workstations still
require separate GPUs and Unix users. The receiver stays attached to the host;
Xur grants each desktop only its allocated input interfaces.

Individual selections take priority over USB receiver and hub assignments. They
also apply to the primary workstation. Identifiable controllers without an
individual selection follow their receiver/hub assignment, or the primary when
the receiver is unassigned. A disconnected saved controller remains selected.
Changing controller ownership restarts running desktops to release open device
handles before transferring access.

The list displays each controller's reported serial. Separate wireless assignments
require the Linux driver to expose a stable controller identity through its input
`uniq` attribute. Current [xone source](https://github.com/dlundqvist/xone/blob/master/driver/common.c)
uses the controller serial for this attribute; older drivers may leave it empty.
Xur does not use connection order, event numbers or receiver slots as identities.
Controllers without a stable identity require whole-receiver assignment; the
editor explains this. Duplicate identities cannot be split reliably. When a
profile has individual controller assignments, unidentified controllers wait
unallocated rather than temporarily appearing in another desktop while their
serial becomes available. Wired controllers without a serial can use a stable
USB port identity; reconnect them to that port.

The Xbox Wireless Adapter is designed for up to eight controllers; this is its
hardware limit, not a guarantee about Linux drivers or a game's player limit.
See [Microsoft's adapter description](https://news.xbox.com/en-us/2015/10/20/xbox-wireless-adapter-for-windows-begins-shipping-today/).
Xur uses the installed host's drivers and firmware; this feature does not install
a driver or pair controllers automatically. Controllers connected to a Moonlight
client continue to arrive through that client's workstation-specific Sunshine
input path.

## Original 2015 Steam Controller

Support is conditional rather than identical to Xbox support. Xur's physical
console and native profile helper read Linux gamepad events without a vendor
allowlist, so the kernel's Steam Controller gamepad can provide the buttons used
by those menus. This has not been verified on physical Steam Controller hardware.
The original controller's right touchpad also differs from an Xbox right stick;
the console's two-stick text entry needs its own hardware acceptance.

Steam games can use Steam Input's gamepad emulation. See Valve's
[gamepad emulation documentation](https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices).
That does not establish compatibility with Xur's console, native helper or web
manager. The [Linux hid-steam driver](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-steam.c)
removes its gamepad interface while a raw HID client such as Steam is open.
The native helper cannot capture those raw HID events, and Steam-created virtual
gamepads do not carry Sunshine's workstation seat tag. Desktop applications also
do not inherit Sunshine's permission to create virtual input devices. Steam Input
virtual-device routing and full menu parity are not implemented or accepted.

Prefer assigning the entire Steam Controller receiver to one workstation for
Steam use. Splitting a receiver between workstations while Steam owns its raw HID
interfaces is not a supported configuration. Physical gamepad discovery groups a
Steam Controller's event and HID interfaces when present; if Steam removes that
gamepad, orphaned raw HID interfaces do not fall back to another workstation in a
profile with individual controller assignments.

## Validation

Source tests cover multiple controllers on one receiver, event/joystick/HID node
isolation, primary and hub precedence, serial-based reconnects in reversed order,
missing and duplicate identities, saved selections and workstation handoffs.
Physical Xbox acceptance remains required: run simultaneous games on separate
desktops, check both menus, reconnect controllers in reversed order, unplug the
adapter, reboot, and verify input and rumble reach only the assigned workstation.
Controller audio and game-specific player ordering also need hardware checks.
