#!/usr/bin/env bash
# Exercise native extent sharing on disposable file-backed storage, with four UIDs.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "$EUID" != 0 ]]; then
  echo 'Run this fixture as root to mount its disposable image and assign test UIDs.' >&2
  exit 1
fi
native="${XUR_UTIL_TESTS:-$PWD/.build/xurutil-tests/Xur.Util.Tests}"
test -x "$native"
mkdir -p .build/evidence/xurutil
fixture="$(mktemp -d "$PWD/.build/evidence/xurutil/btrfs-XXXXXX")"
mounted=false
cleanup() {
  if "$mounted"; then umount "$fixture/mounted" || return; fi
  rm -rf "$fixture"
}
trap cleanup EXIT
truncate -s 256M "$fixture/storage.img"
mkfs.btrfs -f "$fixture/storage.img" > .build/evidence/xurutil/btrfs-mkfs.log
mkdir "$fixture/mounted"
mount -o loop "$fixture/storage.img" "$fixture/mounted"
mounted=true
XUR_UTIL_TESTS="$native" python3 tests/Xur.Integration.Tests/steam-storage.py --filesystem "$fixture/mounted" \
  | tee .build/evidence/xurutil/native-steam-btrfs.json
