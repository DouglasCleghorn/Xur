#!/usr/bin/env bash
# This cleanup is exclusively for disposable GitHub-hosted Ubuntu runners.
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == true && "${RUNNER_ENVIRONMENT:-}" == github-hosted ]]
: "${XUR_BUILD_ROOT:?Set an isolated runner build root}"
[[ "$XUR_BUILD_ROOT" == "$RUNNER_TEMP/"* ]]
# The ISO toolchain needs more space than GitHub's guaranteed 14 GB. Remove
# unrelated preinstalled SDKs, never workspace outputs or a developer's tools.
sudo rm -rf /usr/local/lib/android /usr/share/swift /usr/local/.ghcup /opt/hostedtoolcache/CodeQL
sudo apt-get update -qq
sudo apt-get install -y --no-install-recommends qemu-system-x86 qemu-utils ovmf genisoimage openssh-client iproute2 acl
sudo apt-get clean
[[ -c /dev/kvm ]]
sudo setfacl -m "u:$(id -un):rw" /dev/kvm
mkdir -p "$XUR_BUILD_ROOT/qemu"
ln -s /usr "$XUR_BUILD_ROOT/qemu/usr"
python3 - <<'PY'
import shutil
free=shutil.disk_usage('.').free
if free<40*1024**3:raise SystemExit(f'ISO build needs 40 GiB free after runner cleanup; available {free/1024**3:.1f} GiB. No paid runner is selected automatically.')
PY
