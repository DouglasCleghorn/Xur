#!/usr/bin/env bash
set -eu
test "$(id -u)" = 0
. /etc/os-release
test "$ID" = fedora
test "$(uname -m)" = x86_64
cd "${1:?Pass the prepared build context directory}"
python3 /home/builder/context-receipt.py verify .
mkdir -p /etc/containers/registries.conf.d
install -m 644 os/containers/99-xur-docker-hub.conf /etc/containers/registries.conf.d/
podman build --target common -t localhost/xur-common:x86_64 -f os/bootc/Containerfile .
# Online installer: Bazzite is pulled only after disk approval, from its stable channel.
podman build -t localhost/xur-installer:x86_64 -f os/installer/Containerfile .
mkdir -p /home/builder/xur-output
"${XUR_IMAGE_BUILDER:-/home/builder/image-builder}" manifest \
    --bootc-ref localhost/xur-installer:x86_64 \
    --bootc-default-fs ext4 bootc-generic-iso > /home/builder/xur-output/upstream-manifest.json
python3 /home/builder/label-live-manifest.py /home/builder/xur-output/upstream-manifest.json /home/builder/xur-output/xur-manifest.json
osbuild --store /var/cache/xur-osbuild --output-directory /home/builder/xur-output --export bootiso /home/builder/xur-output/xur-manifest.json
