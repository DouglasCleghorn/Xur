#!/usr/bin/env bash
# Compile and exercise the native startup utility without host systemd or disks.
set -euo pipefail
cd "$(dirname "$0")/.."
sdk="${XUR_DOTNET:-$HOME/.local/share/xur-build/dotnet/dotnet}"
mkdir -p .build/evidence/xurutil
"$sdk" publish tools/Xur.Util -c Release -r linux-x64 -o .build/xurutil \
  > .build/evidence/xurutil/native-publish.log 2>&1
native="$(readlink -f .build/xurutil/xurutil)"
env -i PATH=/usr/bin:/bin "$native" --help > .build/evidence/xurutil/native-help.txt
env -i PATH=/usr/bin:/bin "$native" display moonlight > .build/evidence/xurutil/native-moonlight.txt 2>&1
fixture="$(mktemp -d .build/evidence/xurutil/native-root-XXXXXX)"
trap 'rm -rf "$fixture"' EXIT
fixture="$(readlink -f "$fixture")"
env -i PATH=/usr/bin:/bin "$native" logs configure --root "$fixture" \
  > .build/evidence/xurutil/native-offline.txt
for name in journald.conf.d/60-xur-log-compression.conf system/systemd-journald.service.d/60-xur-log-compression.conf; do
  cmp "os/bootc/logging/$name" "$fixture/etc/systemd/$name"
done
test -f "$fixture/etc/systemd/journald.conf.d/.xur-log-compression.pending"
env -i PATH=/usr/bin:/bin "$native" logs configure --root "$fixture" \
  >> .build/evidence/xurutil/native-offline.txt
if "$native" logs configure --root relative > .build/evidence/xurutil/native-invalid.txt 2>&1; then
  echo 'Native utility accepted a relative root' >&2
  exit 1
fi
test -s .build/xurutil/licenses/System.CommandLine-LICENSE.txt
test -s .build/xurutil/licenses/dotnet-LICENSE.txt
test -s .build/xurutil/licenses/TeeForge-LICENSE.txt
test -s .build/xurutil/licenses/TeeForge-THIRD-PARTY-NOTICES.txt
test -s .build/xurutil/licenses/dotnet-THIRD-PARTY-NOTICES.TXT
file "$native" > .build/evidence/xurutil/native-file.txt
stat --printf='%s\n' "$native" > .build/evidence/xurutil/native-size.txt
"$sdk" run --project tests/Xur.Util.Tests -c Release -- --layout .build/xurutil \
  > .build/evidence/xurutil/native-layout.txt
"$sdk" publish tests/Xur.Util.Tests -c Release -r linux-x64 -p:PublishAot=true \
  -o .build/xurutil-tests > .build/evidence/xurutil/native-fixture-publish.log 2>&1
env -i PATH=/usr/bin:/bin .build/xurutil-tests/Xur.Util.Tests \
  > .build/evidence/xurutil/native-fixture-tests.log 2>&1
# Native fixture publication enables compiler packages only for that invocation.
# Restore the normal test lock afterward, keeping checked-in lock files consistent.
"$sdk" restore tests/Xur.Util.Tests > .build/evidence/xurutil/test-restore.log 2>&1
echo 'Native AOT startup utility checks passed.'
