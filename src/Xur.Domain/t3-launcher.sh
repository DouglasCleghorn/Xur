#!/bin/sh
set -eu
export T3CODE_HOME="${T3CODE_HOME:-$HOME/.t3}"
export T3CODE_INSTALL_BIN_DIR="${T3CODE_INSTALL_BIN_DIR:-$HOME/.local/bin}"
export PATH="$HOME/.local/bin:$HOME/.opencode/bin:$HOME/.local/share/xur-tools/bin:$PATH"

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

# GitHub CLI distributes official archives rather than a self-update command.
# Resolve the stable release and its checksum each time; replace only a verified
# executable, leaving both the saved CLI and native ~/.config/gh account intact.
install_gh() {
    timeout --foreground --kill-after=5s 120s python3 - <<'XUR_GH_INSTALL'
import fcntl, hashlib, json, os, platform, re, shutil, subprocess, tarfile, tempfile
from pathlib import Path

home = Path.home()
binary = home / ".local/bin/gh"
notice = home / ".local/share/gh/LICENSE"
binary.parent.mkdir(parents=True, exist_ok=True)
notice.parent.mkdir(parents=True, exist_ok=True)

def download(url, target):
    subprocess.run(["curl", "--fail", "--silent", "--show-error", "--location", "--retry", "2",
                    "--connect-timeout", "10", "--max-time", "30", url, "-o", str(target)], check=True)

def version_matches(path, version):
    try:
        result = subprocess.run([str(path), "--version"], capture_output=True, text=True, timeout=10)
        return result.returncode == 0 and result.stdout.startswith("gh version " + version + " ")
    except (OSError, subprocess.TimeoutExpired):
        return False

with (notice.parent / "update.lock").open("a") as lock:
    fcntl.flock(lock, fcntl.LOCK_EX)
    arch = {"x86_64": "amd64", "aarch64": "arm64"}.get(platform.machine())
    if not arch:
        raise SystemExit("GitHub CLI: unsupported architecture " + platform.machine())
    with tempfile.TemporaryDirectory(prefix=".gh-update-", dir=binary.parent) as temporary:
        stage = Path(temporary)
        download("https://api.github.com/repos/cli/cli/releases/latest", stage / "release.json")
        release = json.loads((stage / "release.json").read_text())
        tag = release["tag_name"]
        if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", tag) or release.get("prerelease") or release.get("draft"):
            raise SystemExit("GitHub CLI: expected a stable upstream release")
        version = tag[1:]
        if not (version_matches(binary, version) and notice.is_file()):
            prefix = "gh_" + version + "_linux_" + arch
            archive_name = prefix + ".tar.gz"
            checksums_name = "gh_" + version + "_checksums.txt"
            assets = {asset["name"] for asset in release["assets"]}
            if not {archive_name, checksums_name} <= assets:
                raise SystemExit("GitHub CLI: upstream archive or checksums missing")
            base = "https://github.com/cli/cli/releases/download/" + tag + "/"
            download(base + checksums_name, stage / "checksums")
            download(base + archive_name, stage / "archive.tar.gz")
            sums = [line.split() for line in (stage / "checksums").read_text().splitlines()]
            expected = [parts[0] for parts in sums if len(parts) == 2 and parts[1].lstrip("*") == archive_name]
            with (stage / "archive.tar.gz").open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            if expected != [digest]:
                raise SystemExit("GitHub CLI: archive checksum mismatch; saved installation retained")
            with tarfile.open(stage / "archive.tar.gz", "r:gz") as archive:
                # Copy exact regular files; never extract paths or links from the archive.
                for source, target in ((prefix + "/bin/gh", "gh"), (prefix + "/LICENSE", "LICENSE")):
                    member = archive.getmember(source)
                    if not member.isfile() or member.size > 200 * 1024 * 1024:
                        raise SystemExit("GitHub CLI: unexpected archive entry")
                    with archive.extractfile(member) as incoming, (stage / target).open("wb") as outgoing:
                        shutil.copyfileobj(incoming, outgoing)
            (stage / "gh").chmod(0o755)
            if not version_matches(stage / "gh", version):
                raise SystemExit("GitHub CLI: downloaded executable failed validation")
            # The executable is published last, with a single atomic replacement.
            shutil.copyfile(stage / "LICENSE", notice.parent / "LICENSE.tmp")
            os.replace(notice.parent / "LICENSE.tmp", notice)
            os.replace(stage / "gh", binary)
XUR_GH_INSTALL
}

# Conda-forge supplies relocatable binaries and their libraries/notices. Keep
# this environment outside the image so native package updates survive replacement.
retain_notice() {
    [ -s "$1" ] && return 0
    mkdir -p "$(dirname "$1")"
    curl --fail --silent --show-error --location --retry 2 --connect-timeout 10 --max-time 30 "$2" -o "$1.tmp" || return $?
    mv "$1.tmp" "$1"
}

install_tools() (
    export MAMBA_ROOT_PREFIX="$HOME/.local/share/mamba"
    export MAMBA_NO_BANNER=1
    retain_notice "$MAMBA_ROOT_PREFIX/LICENSE" https://raw.githubusercontent.com/mamba-org/mamba/main/LICENSE || return $?
    manager="$HOME/.local/bin/micromamba"
    if [ -x "$manager" ]; then
        timeout --foreground --kill-after=5s 120s "$manager" self-update --yes || true
    else
        export BIN_FOLDER="$HOME/.local/bin" INIT_YES=no CONDA_FORGE_YES=no SHELL=/bin/sh
        install_script https://micro.mamba.pm/install.sh sh </dev/null || return $?
    fi
    prefix="$HOME/.local/share/xur-tools"
    action=install
    [ -f "$prefix/conda-meta/history" ] || action=create
    timeout --foreground --kill-after=5s 600s "$manager" "$action" --yes --prefix "$prefix" \
        --override-channels --channel conda-forge --strict-channel-priority \
        ripgrep fd-find jq git-lfs rsync zip unzip sqlite || return $?
    timeout --foreground --kill-after=5s 600s "$manager" update --all --yes --prefix "$prefix" \
        --override-channels --channel conda-forge --strict-channel-priority || return $?
    "$prefix/bin/git-lfs" install --skip-repo
)

refresh() (
    tool=$1
    case "$tool" in
        t3) binary="$T3CODE_INSTALL_BIN_DIR/t3" ;;
        codex|claude|opencode|gh|uv) binary="$HOME/.local/bin/$tool" ;;
        tools) binary="$HOME/.local/share/xur-tools/bin/rg" ;;
        *) echo 'Unknown T3 harness.' >&2; exit 1 ;;
    esac
    printf 'Updating %s…\n' "$tool"
    result=0
    case "$tool" in
        gh) install_gh || result=$? ;;
        tools) install_tools || result=$? ;;
        uv)
            retain_notice "$HOME/.local/share/uv/LICENSE-MIT" https://raw.githubusercontent.com/astral-sh/uv/main/LICENSE-MIT || result=$?
            retain_notice "$HOME/.local/share/uv/LICENSE-APACHE" https://raw.githubusercontent.com/astral-sh/uv/main/LICENSE-APACHE || result=$?
            if [ -x "$binary" ]; then
                timeout --foreground --kill-after=5s 120s "$binary" self update || result=$?
            else
                export UV_INSTALL_DIR="$HOME/.local/bin" UV_NO_MODIFY_PATH=1
                install_script https://astral.sh/uv/install.sh sh || result=$?
            fi ;;
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
    case "${2:-}" in codex|claude|opencode|gh|uv|tools) refresh "$2"; exit $? ;; esac
    echo 'Choose Codex, Claude, OpenCode, gh, uv or tools.' >&2; exit 1
fi

# Pairing and other CLI calls must not trigger updates or race the live server.
if [ "${1:-}" = start ]; then
    # Start an installed T3 immediately. Each updater logs its own result and
    # keeps native state in the persistent home; new T3 code is used next start.
    for tool in codex claude opencode gh uv tools; do
        refresh "$tool" &
    done
    if [ -x "$T3CODE_INSTALL_BIN_DIR/t3" ]; then
        refresh t3 &
    else
        refresh t3
    fi
fi
exec "$T3CODE_INSTALL_BIN_DIR/t3" "$@"
