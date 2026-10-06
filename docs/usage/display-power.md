# Display sleep and HDMI-CEC

Xur keeps the shared server awake so web management, AI services and remote
workstations remain available. Console and workstation displays can still sleep.
This differs from suspending the entire computer: wake from system suspend
depends on firmware, the USB/Bluetooth adapter and the controller's driver.
Xur does not enable system suspend or promise controller wake from it.

The console already wakes its idle display on mapped controller activity. Its
first input wakes without selecting a menu action. The workstation's native
profile helper now reports fresh controller button presses and D-pad activity
to Plasma, and requests display wake in that user's session. Release the wake
button before navigating the picker. Screen locks still require authentication.
The helper must be running, the controller connected and assigned to that seat;
an application update requires reloading an existing workstation to start the
updated helper. While the picker is closed, a wake button can also reach a game.

## CEC power controls

- In the console, choose **Power → Display power / CEC screen off**, select a
  display, then **CEC screen off** or **CEC screen on**. This list contains only
  console displays.
- In the workstation profile picker, choose **CEC screen off: display name**
  or **CEC screen on: display name**. Only that workstation's displays appear.
  These actions remain available when profile loading is disabled.
- In the web manager, **Home → Displays**, the **Switch profile** overlay and
  **Displays** offer separate on/off controls for each connected display.

Screen off requests TV standby; it does not shut down the workstation, unload
a profile or stop workloads. Controller input wakes CEC screens that Xur put
in standby, including after an agent restart during the same boot. Background
polling never sends a wake command. A TV that drops HDMI hotplug in standby
remains in the list so it can be woken. An acknowledgement confirms receipt of
the CEC command, not a measured panel power state; a TV may respond slowly or
ignore the requested change. Xur displays **Standby requested**, **On requested**
or **Unknown** rather than guessing its actual state.

CEC requires a TV with CEC enabled and hardware exposing a Linux `/dev/cecN`
adapter. Many PC HDMI ports, monitors and DisplayPort connections provide no
CEC. Unsupported displays remain visible with disabled controls and an
explanation. Native adapters are matched through the kernel's DRM connector
information. Under **Displays → External CEC adapters**, explicitly assign an
external adapter to the display whose HDMI cable it connects to. An adapter can
belong to only one display; Xur never guesses an external adapter's cable.
Adapters requiring a userspace bridge must first expose a Linux CEC device.

There is no universal controller-button-to-CEC binding. Xur uses its existing
**View + Menu** hold to open the picker, then D-pad and A to choose screen power.
The CEC wire commands are standardized: TV-directed **Standby** (`0x36`) and
**Image View On** (`0x04`). Xur does not broadcast standby or select the TV's
input. See the [Linux CEC API](https://docs.kernel.org/userspace-api/media/cec/cec-ioc-receive.html)
and [connector mapping](https://docs.kernel.org/userspace-api/media/cec/cec-ioc-adap-g-conn-info.html).

Authenticated automation can read `GET /api/displays`, send
`POST /api/displays/power` with `{"id":"display ID","action":"off"}` or
`"action":"on"`, and assign an external adapter with
`POST /api/displays/adapter` and `{"id":"display ID","adapter":"adapter ID"}`.
An empty adapter value removes the external assignment. Diagnostic API keys can
read the display list; mutations require automation access. Browser mutations
retain the usual session and CSRF protection.

Hardware acceptance remains necessary: test USB and paired Bluetooth controllers,
each TV's standby/hotplug behavior, native and external CEC adapters, and multiple
workstation ownership. Mocked protocol and UI checks cannot establish support on
a particular cable, controller or television.
