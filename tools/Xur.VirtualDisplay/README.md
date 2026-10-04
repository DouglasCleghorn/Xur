# Xur virtual monitor

Creates a 1920×1080 output through KDE's screencast protocol and keeps it alive.
It opens no network listener and consumes no video frames. Sunshine captures the
output normally. The compositor retains its DRM and libinput backends.

`screencast.xml` is from KDE/plasma-wayland-protocols v1.22.0, path
src/protocols/zkde-screencast-unstable-v1.xml (LGPL-2.1-or-later).
Source: https://github.com/KDE/plasma-wayland-protocols/tree/v1.22.0
The protocol is a KDE implementation detail. We bind version 2, test it with
our installed KWin, and fail explicitly if unavailable.
