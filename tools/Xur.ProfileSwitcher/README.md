# Profile switcher

Xur's native Plasma helper runs as the workstation user. It registers
Ctrl + Alt + P through KGlobalAccel and watches only accessible gamepads with
the workstation's exact `ID_SEAT`. Hold View + Menu for one second to open it.
The Plasma launcher entry invokes the same running helper over that user's
session bus. The Shortcuts dialog changes the keyboard combination, controller
combination and hold duration; KDE also exposes the registered shortcut.

The picker needs no Xur login. It uses the dedicated local Unix socket at
`/run/xur-profile-switcher/switcher.sock`. Linux supplies the caller's UID; the
agent verifies that it belongs to an active, registered workstation. The helper
also checks that the service runs as root. The transport accepts only state,
preview, operation-bound cancellation, exact-plan approval and fixed display-state/on/off/wake actions, and returns display data without private
recipes. An approval belongs to the workstation and user that reviewed it.
Settings → Profile access can disable workstation controls immediately.
Display power is independent of profile-loading permission; its requests always
use the authenticated workstation's GPU ownership. The agent opens CEC devices,
so desktop users need neither direct CEC access nor manager credentials.

Selection previews the actual workload changes. Load profile or Stop all workloads separately
approves the returned plan ID and digest. The helper never loads a profile on
opening or navigation. Closing, expiry and failed requests discard stale plans.
Accepted switches and their outcomes record the user, workstation and opening
trigger in Xur logs. The transition journal retains that origin across recovery.

While visible, it exclusively grabs the workstation's evdev controllers and
uses D-pad, A and B for navigation. Releasing all buttons arms navigation after
opening. Devices are rediscovered after hotplug and grabs end on close or exit.
Both digital D-pad buttons and hat axes are supported; duplicate reports move
once. Release packets arm the next press even when they share a queued read.
Touch/contact indicators on HID controllers do not block navigation.
D-pad navigation also reaches Try again, Shortcuts and Close. The selected
control stays highlighted when a fullscreen application retains desktop focus.
In Shortcuts, up/down moves between fields and actions, left/right changes the
controller chord or hold duration, A activates Save/Cancel and B cancels. Editing
the keyboard binding uses a keyboard.

The separate Stop all workloads button stops workstations and AI services while
keeping saved profiles. Its review defaults to Back to profiles; left/right
selects Back or confirmation, and B returns to the picker.

Profiles and Stop all workloads remain selectable while a profile is loading,
cancelling or failed. Choosing one interrupts the displayed operation and waits
for its already-running action to finish at a safe boundary. The new review then
uses the actual remaining workloads and still requires a separate confirmation.
Back and Close remain available during that wait. Choosing again replaces the
pending review; closing never starts the replacement. Cancellation does not kill
an in-flight download, startup, drain or stop.

Fresh button presses and D-pad activity report user activity through Plasma's
screen-saver interface and request `kscreen-doctor --dpms on` in this session.
CEC wake only targets displays deliberately put in standby through Xur. Requests
are bounded to one in flight; Plasma activity/DPMS updates are throttled to two
seconds. Each fresh press checks CEC standby before picker navigation, including
standby requested in the web manager. The wake input is consumed even if a TV
fails to acknowledge it, so it cannot accidentally activate the selected row.
The first press after standby selected in this picker cannot activate its selected
action; release and press again. Background device discovery and held-button
repeats do not send wake requests. See [display power](../../docs/usage/display-power.md).

The opening chord is observed before the grab and can also reach the game.
Games using hidraw directly are outside evdev's exclusive-grab boundary. Fully
suppressing the opening buttons would require an input proxy or cooperation
with the game's input layer; this helper does not proxy devices or force-disable
Steam Input. Fullscreen focus, controller driver mappings, grab behavior and
Moonlight transport still require physical acceptance on the target system.

Original helper code is MIT licensed under the repository's LICENSE. Qt, KDE,
libevdev and libudev retain their upstream licenses; the builder records RPM
source packages and preserves their license notices with the runtime. Fedora
omits XCB keysyms' COPYING file; its matching upstream notice is retained here,
with archive provenance and checksums in `upstream-lock.json`. A changed version
requires reviewing that notice before the build can succeed.
