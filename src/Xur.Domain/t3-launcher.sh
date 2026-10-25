#!/bin/sh
set -eu
export T3CODE_HOME="${T3CODE_HOME:-$HOME/.t3}"
export T3CODE_INSTALL_BIN_DIR="${T3CODE_INSTALL_BIN_DIR:-$HOME/.local/bin}"
export PATH="$HOME/.local/bin:$HOME/.opencode/bin:$PATH"

# Download the complete upstream script before executing it. Bound each job so
# an offline update cannot hold startup forever. Upstream owns versions/checksums.
install_script() {
    timeout --foreground --kill-after=5s 120s sh -c '
        set -eu
        installer=$(mktemp)
        trap '\''rm -f "$installer"'\'' EXIT
        trap '\''exit 130'\'' INT
        trap '\''exit 143'\'' TERM
        curl --fail --silent --show-error --location --retry 2 --connect-timeout 10 --max-time 30 "$1" -o "$installer"
        shift
        interpreter=$1; shift
        "$interpreter" "$installer" "$@"
    ' installer "$@"
}

refresh() (
    tool=$1
    case "$tool" in
        t3) binary="$T3CODE_INSTALL_BIN_DIR/t3" ;;
        codex|claude|opencode) binary="$HOME/.local/bin/$tool" ;;
        *) echo 'Unknown T3 harness.' >&2; exit 1 ;;
    esac
    printf 'Updating %s…\n' "$tool"
    result=0
    case "$tool" in
        t3)
            if [ -x "$binary" ]; then
                timeout --foreground --kill-after=5s 120s "$binary" update --yes || result=$?
            else
                install_script https://t3.codes/install.sh sh || result=$?
            fi ;;
        codex)
            export CODEX_INSTALL_DIR="$HOME/.local/bin" CODEX_NON_INTERACTIVE=1
            install_script https://chatgpt.com/codex/install.sh sh || result=$? ;;
        claude)
            if [ -x "$binary" ]; then
                timeout --foreground --kill-after=5s 120s "$binary" update || result=$?
            else
                install_script https://claude.ai/install.sh bash || result=$?
            fi ;;
        opencode)
            if [ -x "$HOME/.opencode/bin/opencode" ]; then
                timeout --foreground --kill-after=5s 120s "$HOME/.opencode/bin/opencode" upgrade || result=$?
            else
                install_script https://opencode.ai/v2/install bash --no-modify-path || result=$?
            fi
            if [ -x "$HOME/.opencode/bin/opencode" ]; then
                mkdir -p "$HOME/.local/bin"
                ln -sfn "$HOME/.opencode/bin/opencode" "$binary"
            fi ;;
    esac
    if [ "$result" -ne 0 ]; then
        printf '%s update failed (exit %s); checking the saved installation.\n' "$tool" "$result" >&2
    fi
    if ! timeout --foreground --kill-after=5s 15s "$binary" --version; then
        printf '%s is unavailable. Check internet access and retry startup.\n' "$tool" >&2
        exit 1
    fi
)

# The setup UI uses the same installer, without launching or updating T3.
if [ "${1:-}" = xur-install-harness ]; then
    case "${2:-}" in codex|claude|opencode) refresh "$2"; exit $? ;; esac
    echo 'Choose Codex, Claude or OpenCode.' >&2; exit 1
fi

# Pairing and other CLI calls must not trigger updates or race the live server.
if [ "${1:-}" = start ]; then
    # Start an installed T3 immediately. Each updater logs its own result and
    # keeps native state in the persistent home; new T3 code is used next start.
    for tool in codex claude opencode; do
        refresh "$tool" &
    done
    if [ -x "$T3CODE_INSTALL_BIN_DIR/t3" ]; then
        refresh t3 &
    else
        refresh t3
    fi
fi
exec "$T3CODE_INSTALL_BIN_DIR/t3" "$@"
