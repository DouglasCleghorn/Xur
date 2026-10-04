#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
if (( EUID != 0 )); then
    exec sudo -- "$0" "$@"
fi
sdk="${XUR_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$sdk" ]]; then
    user_dir="$(getent passwd "${SUDO_USER:-$(id -un)}" | cut -d: -f6)"
    sdk="$user_dir/.local/share/xur-build/dotnet/dotnet"
fi
[[ -x "$sdk" ]] || { echo 'Install .NET 10 or set XUR_DOTNET to its executable.' >&2; exit 1; }
cache="$script_dir/../.build/usb-update"
mkdir -p "$cache/tmp"
export TMPDIR="$cache/tmp" TMP="$cache/tmp" TEMP="$cache/tmp"
export DOTNET_CLI_HOME="$cache/dotnet-home" NUGET_PACKAGES="$cache/packages"
export NUGET_HTTP_CACHE_PATH="$cache/http-cache" NUGET_SCRATCH="$cache/nuget-scratch"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
exec "$sdk" run --file "$script_dir/update-usb/app.cs" -- "$@"
