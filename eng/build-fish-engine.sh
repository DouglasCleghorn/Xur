#!/usr/bin/env bash
# Developer verification of the same dependency layer prepared by the agent.
set -euo pipefail
cd "$(dirname "$0")/.."
builder="${XUR_CONTAINER_BUILDER:-podman}"
command -v "$builder" >/dev/null || { echo "Install Podman or set XUR_CONTAINER_BUILDER to a compatible builder." >&2; exit 1; }
mkdir -p .build/fish-engine
"$builder" build --platform linux/amd64 --iidfile .build/fish-engine/image-id -t localhost/xur-fish:prep -f os/engines/fish/Containerfile os/engines/fish
"$builder" run --rm --network=none --entrypoint cat localhost/xur-fish:prep /opt/xur-fish-install.json > .build/fish-engine/install-report.json
"$builder" run --rm --network=none --entrypoint cat localhost/xur-fish:prep /opt/xur-fish-packages.txt > .build/fish-engine/packages.txt
printf 'Local Fish image built and codec initialized. The agent prepares and caches this layer automatically; no separate registry publication is required.\n'
