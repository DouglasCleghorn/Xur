#!/usr/bin/env bash
set -eu
cd "$(dirname "$0")/.."
mkdir -p .build
# The lock stays in flock's parent process. Compiler servers must not inherit
# its descriptor and block later publications after this script exits.
if [ "${XUR_PUBLISH_LOCKED:-0}" != 1 ]; then
  exec flock --exclusive --close .build/context.lock env XUR_PUBLISH_LOCKED=1 bash eng/publish.sh "$@"
fi
sdk="${XUR_DOTNET:-$HOME/.local/share/xur-build/dotnet/dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
python3 - <<'PY'
from pathlib import Path
import shutil,hashlib
context=Path('.build/context')
if context.exists(): shutil.rmtree(context)
assert hashlib.file_digest(open('.build/downloads/tailscale.tgz','rb'),'sha256').hexdigest()=='50748df1045e60b5b695f19f4c56b0da36c019948b440fb456b6584a50f0d8b9'
PY
"$sdk" publish src/Xur.Control -c Release -r linux-x64 --self-contained true -o .build/context/publish/control
"$sdk" publish src/Xur.Agent -c Release -r linux-x64 --self-contained true -o .build/context/publish/agent
"$sdk" publish src/Xur.Gateway -c Release -r linux-x64 --self-contained true -o .build/context/publish/gateway
cp -a os catalog .build/context/
mkdir -p .build/context/tailscale
tar -xzf .build/downloads/tailscale.tgz --strip-components=1 -C .build/context/tailscale
python3 eng/build-console.py
python3 eng/build-virtual-display.py
python3 eng/prepare-streaming.py
python3 eng/prepare-rootfs.py
python3 eng/context-receipt.py create .build/context
