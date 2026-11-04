set -eu
# Native host prerequisites live alongside the shared home, never in the OS image.
native_tools() (
    set -eu
    export MAMBA_ROOT_PREFIX="$HOME/.local/share/mamba" MAMBA_NO_BANNER=1
    mkdir -p "$HOME/.local/bin" "$MAMBA_ROOT_PREFIX"
    exec 9>"$MAMBA_ROOT_PREFIX/xur-native.lock"
    flock -w 600 9
    manager="$HOME/.local/bin/micromamba"
    if [ ! -x "$manager" ]; then
        installer=$(mktemp)
        trap 'rm -f "$installer"' EXIT
        curl --fail --silent --show-error --location --retry 2 --connect-timeout 10 --max-time 30 https://micro.mamba.pm/install.sh -o "$installer"
        BIN_FOLDER="$HOME/.local/bin" INIT_YES=no CONDA_FORGE_YES=no SHELL=/bin/sh sh "$installer" </dev/null
    fi
    if [ ! -s "$MAMBA_ROOT_PREFIX/LICENSE" ]; then
        curl --fail --silent --show-error --location --retry 2 --connect-timeout 10 --max-time 30 https://raw.githubusercontent.com/mamba-org/mamba/main/LICENSE -o "$MAMBA_ROOT_PREFIX/LICENSE"
    fi
    prefix="$HOME/.local/share/xur-native"
    action=install
    [ -f "$prefix/conda-meta/history" ] || action=create
    "$manager" "$action" --yes --prefix "$prefix" --override-channels --channel conda-forge --strict-channel-priority \
        nodejs python git openssh curl ripgrep bash
)
export PATH="$HOME/.local/share/xur-native/bin:$PATH"
if [ "${1:-}" = xur-native-prepare ]; then
    native_tools
    exit $?
fi
for native_tool in python3 node git ssh curl rg bash; do
    if [ ! -x "$HOME/.local/share/xur-native/bin/$native_tool" ]; then
        native_tools
        break
    fi
done
