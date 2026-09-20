# Headless workstation input

Included in application update 2026.09.18.3.

The former `kwin_wayland --virtual` launch selected KWin's virtual backend,
which does not create its libinput backend. Sunshine's uinput devices could
therefore exist while the displayed desktop ignored their events.

Headless startup now uses the same PlasmaLogin/logind session as a local
workstation, pins KWin's DRM backend to the assigned GPU, and keeps a
1920×1080 virtual output alive through KDE's screencast protocol. An output is
necessary: KWin's no-monitor placeholder deliberately filters input. The small
`xur-virtual-output` helper opens no network service and stops with the workstation.
Its desktop entry grants only the required screencast interface; global KWin
permission checks stay enabled. Helper startup failures appear in workstation logs.

The uinput module is loaded before installing the user slice's device allowlist.
Streaming checks access to `/dev/uinput` as the workstation user in that slice.
Existing desktops need to be unloaded and loaded after installing this update.

## Validation

`python3 tests/Xur.Media.Tests/check-headless-input.py --name editor-dev --evidence .build/headless-input.json`

This explicitly interrupts only a marked disposable development VM, forces all
DRM connectors disconnected before starting Plasma, then verifies:

- KWin runs the DRM backend and the virtual output is 1920×1080.
- The workstation's restricted user slice can open uinput.
- Real uinput keyboard and mouse events reach an Xwayland application.
- The original display configuration and agent are restored afterward.

The VM test does not exercise Moonlight's network transport or an RTX 3090.
Those still require an end-to-end test on the physical workstation.

Implementation references: [KWin backend selection](https://github.com/KDE/kwin/blob/master/src/main_wayland.cpp),
[DRM input backend](https://github.com/KDE/kwin/blob/master/src/backends/drm/drm_backend.cpp),
[placeholder input filtering](https://github.com/KDE/kwin/blob/master/src/workspace.cpp).
The protocol is pinned and licensed in `tools/Xur.VirtualDisplay`.
