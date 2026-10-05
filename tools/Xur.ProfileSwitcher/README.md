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
preview and exact-plan approval actions, and returns display data without private
recipes. An approval belongs to the workstation and user that reviewed it.
Settings → Profile access can disable workstation controls immediately.

Selection previews the actual workload changes. Load profile or Unload all separately
approves the returned plan ID and digest. The helper never loads a profile on
opening or navigation. Closing, expiry and failed requests discard stale plans.
Accepted switches and their outcomes record the user, workstation and opening
trigger in Xur logs. The transition journal retains that origin across recovery.

While visible, it exclusively grabs the workstation's evdev controllers and
uses D-pad, A and B for navigation. Releasing all buttons arms navigation after
opening. Devices are rediscovered after hotplug and grabs end on close or exit.

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
