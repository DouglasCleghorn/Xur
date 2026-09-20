#!/usr/bin/env bash
# Preparation only: builds a local image. Does not publish or alter the catalog.
set -euo pipefail
cd "$(dirname "$0")/.."
builder="${XUR_CONTAINER_BUILDER:-podman}"
command -v "$builder" >/dev/null || { echo "Install Podman or set XUR_CONTAINER_BUILDER to a compatible builder." >&2; exit 1; }
mkdir -p .build/fish-engine
"$builder" build --platform linux/amd64 --iidfile .build/fish-engine/image-id -t localhost/xur-fish:prep -f os/engines/fish/Containerfile os/engines/fish
"$builder" run --rm --network=none --entrypoint cat localhost/xur-fish:prep /opt/xur-fish-install.json > .build/fish-engine/install-report.json
"$builder" run --rm --network=none --entrypoint cat localhost/xur-fish:prep /opt/xur-fish-packages.txt > .build/fish-engine/packages.txt
printf 'Local Fish image built and codec initialized. Review dependency report, lock transitive packages, test inference, then publish and pin the registry manifest digest before catalog use.\n'
