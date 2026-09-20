# Xur virtual monitor

Creates a 1920×1080 output through KDE's screencast protocol and keeps it alive.
It opens no network listener and consumes no video frames. Sunshine captures the
output normally. The compositor retains its DRM and libinput backends.

`screencast.xml` is from KDE/plasma-wayland-protocols commit
382dfabda886d3f2f5c067b22e5a22376685ba78, path
src/protocols/zkde-screencast-unstable-v1.xml (LGPL-2.1-or-later).
Source: https://github.com/KDE/plasma-wayland-protocols/tree/382dfabda886d3f2f5c067b22e5a22376685ba78
The protocol is a KDE implementation detail. We bind version 2, test it with
our installed KWin, and fail explicitly if unavailable.
