#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
start=$SECONDS
sdk="${XUR_DOTNET:-$HOME/.local/share/xur-build/dotnet/dotnet}"
mkdir -p .build/fast
cc -O2 -Wall -Wextra -Werror tests/Xur.Unit.Tests/SeatInputTest.c -ldl -o .build/fast/seat-input-test
.build/fast/seat-input-test
python3 tests/Xur.Integration.Tests/source-manifest.py > .build/fast/source-manifest.log
python3 tests/Xur.Integration.Tests/cleanup-build.py > .build/fast/cleanup.log
bash eng/publish.sh > .build/fast/publish.log 2>&1
"$sdk" run --project tests/Xur.Unit.Tests -c Release > .build/fast/unit.log
python3 tests/Xur.Integration.Tests/station-display.py > .build/fast/station-display.log 2>&1
python3 tests/Xur.Integration.Tests/storage-explorer.py > .build/fast/storage-explorer.log 2>&1
python3 tests/Xur.Integration.Tests/station-files.py > .build/fast/station-files.json
python3 tests/Xur.Integration.Tests/bootstrap.py > .build/fast/control.json
python3 tests/Xur.Integration.Tests/account-api.py > .build/fast/account-api.json
node tests/Xur.Integration.Tests/https-login.cjs > .build/fast/https-login.json
node tests/Xur.Integration.Tests/updates-ui.cjs > .build/fast/updates-ui.json
node tests/Xur.Integration.Tests/control-panel-ui.cjs > .build/fast/control-panel-ui.json
node tests/Xur.Integration.Tests/endpoints-ui.cjs > .build/fast/endpoints-ui.json
node tests/Xur.Integration.Tests/station-identity-ui.cjs > .build/fast/station-identity-ui.json
node tests/Xur.Integration.Tests/workstations-ui.cjs > .build/fast/workstations-ui.json
node tests/Xur.Integration.Tests/settings-storage-ui.cjs > .build/fast/settings-storage-ui.json
node tests/Xur.Integration.Tests/files-ui.cjs > .build/fast/files-ui.json
node tests/Xur.Integration.Tests/api-keys-ui.cjs > .build/fast/api-keys-ui.json
node tests/Xur.Integration.Tests/model-lab-ui.cjs > .build/fast/model-lab-ui.json
python3 tests/Xur.Integration.Tests/os-update.py > .build/fast/os-update.log
python3 tests/Xur.Integration.Tests/update-all.py > .build/fast/update-all.json
node tests/Xur.Integration.Tests/cancellation-ui.cjs > .build/fast/cancellation-ui.json
"$sdk" run --project tests/Xur.Profile.Tests -c Release > .build/fast/profiles.log
python3 tests/Xur.Integration.Tests/application-update.py > .build/fast/application-updates.json
python3 tests/Xur.Integration.Tests/online-installer.py > .build/fast/online-installer.json
python3 tests/Xur.Integration.Tests/compact-update.py > .build/fast/compact-update.json
python3 tests/Xur.Integration.Tests/compact-release.py > .build/fast/compact-release.json
python3 tests/Xur.Integration.Tests/github-release.py > .build/fast/github-release.json
python3 tests/Xur.Integration.Tests/installer-release.py > .build/fast/installer-release.json
python3 tests/Xur.Integration.Tests/builder-ready.py > .build/fast/builder-ready.json
python3 tests/Xur.Integration.Tests/terminal.py > .build/fast/terminal.json
python3 tests/Xur.Integration.Tests/console-menu.py > .build/fast/console-menu.json
XUR_CONSOLE_CLIENT="$PWD/.build/console-runtime/client" python3 tests/Xur.Integration.Tests/console-responsive.py > .build/fast/console-responsive.json
python3 tests/Xur.Integration.Tests/network-startup.py > .build/fast/network-startup.json
node tests/Xur.Integration.Tests/network-settings-ui.cjs > .build/fast/network-settings-ui.json
printf 'Fast checks passed in %ss. Logs: .build/fast/\n' "$((SECONDS-start))"
