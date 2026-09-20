#!/usr/bin/env bash
set -euo pipefail
cd "${1:?Build directory}"
python3 patch.py
meson setup kmscon/build kmscon --buildtype=release --prefix=/usr --libdir=lib \
  -Dtests=false -Ddocs=disabled -Dlibseat=disabled -Ddbus=disabled \
  -Dvideo_fbdev=disabled -Dvideo_drm2d=enabled -Dvideo_drm3d=disabled \
  -Drenderer_gltex=disabled -Dfont_psf=disabled -Dfont_unifont=enabled \
  -Dfont_freetype=disabled -Dfont_pango=disabled --wrap-mode=nofallback
meson compile -C kmscon/build
mkdir -p output/lib output/licenses
install -m755 kmscon/build/src/kmscon output/kmscon
install -m755 kmscon/build/src/font/mod-unifont.so output/lib/
cp -L /usr/lib64/libtsm.so.4 output/lib/libtsm.so.4
cc -O2 -Wall -Wextra -Werror client.c -o output/client $(pkg-config --cflags --libs libcurl)
cp kmscon/COPYING output/licenses/kmscon.txt
rpm -q libtsm libdrm libcurl libxkbcommon glibc > output/build-packages.txt
cp /usr/share/licenses/libtsm/LICENSE* output/licenses/ 2>/dev/null || cp /usr/share/licenses/libtsm/COPYING* output/licenses/
tar -czf console-runtime.tar.gz -C output .
