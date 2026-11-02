#!/bin/sh
set -eu
export T3CODE_HOME="${T3CODE_HOME:-$HOME/.t3}"
export T3CODE_INSTALL_BIN_DIR="${T3CODE_INSTALL_BIN_DIR:-$HOME/.local/bin}"
export PATH="$HOME/.local/bin:$HOME/.opencode/bin:$HOME/.local/share/xur-tools/bin:$PATH"

# Install/update the SDK and C# MCP in the persistent home. flock is released
# automatically if the container stops; failed updates retain the selected pair.
refresh_dotnet() (
    mkdir -p "$HOME/.local/share/xur-mcp"
    timeout --foreground --kill-after=5s 900s flock -w 5 "$HOME/.local/share/xur-mcp/update.lock" node - <<'XUR_DOTNET_UPDATE'
__XUR_DOTNET_UPDATER__
XUR_DOTNET_UPDATE
)

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
            install_script https://t3.codes/install.sh sh || result=$? ;;
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
    if [ "${2:-}" = dotnet ]; then refresh_dotnet; exit $?; fi
    case "${2:-}" in codex|claude|opencode|gh|uv|tools) refresh "$2"; exit $? ;; esac
    echo 'Choose Codex, Claude, OpenCode, gh, uv or tools.' >&2; exit 1
fi

# Pairing and other CLI calls must not trigger updates or race the live server.
if [ "${1:-}" = start ]; then
    # Only the upstream service launcher may select/restart a running T3.
    # A concurrent `t3 update` would race its durable update/rollback state.
    (while :; do refresh_dotnet || true; sleep 86400; done) &
    for tool in codex claude opencode gh uv tools; do
        refresh "$tool" &
    done
    if [ ! -e "$T3CODE_HOME/runtime/service-state.json" ] && [ ! -x "$T3CODE_INSTALL_BIN_DIR/t3" ]; then
        refresh t3
    fi
fi

# Run the upstream supervisor directly: Podman, rather than systemd inside the
# container, owns its lifecycle. Keep the recipe's command stable for saved images.
exec python3 -c "$(cat <<'XUR_T3_SERVICE'
import json, os, re, subprocess, sys, tempfile
from pathlib import Path

args = sys.argv[1:]
starting = args[:1] == ['start']
if starting:
    args = args[1:]
    while args:
        option = args.pop(0)
        if option in ('--host', '--port', '--base-dir') and args:
            os.environ[{'--host': 'T3CODE_HOST', '--port': 'T3CODE_PORT', '--base-dir': 'T3CODE_HOME'}[option]] = args.pop(0)
        elif option == '--no-browser':
            os.environ['T3CODE_NO_BROWSER'] = 'true'
        elif not option.startswith('-') and not args:
            os.chdir(option)
        else:
            sys.exit('Unsupported Xur T3 startup option: ' + option)

home = Path(os.environ['T3CODE_HOME'])
state_path = home / 'runtime/service-state.json'
binary = Path(os.environ['T3CODE_INSTALL_BIN_DIR']) / 't3'
# This is the upstream service-state/IPC protocol, not a release version pin.
# Fail visibly if upstream changes it; never guess or overwrite recovery state.
protocol = 3
number = r'(?:0|[1-9][0-9]*)'
prerelease = rf'(?:{number}|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
version_pattern = rf'{number}\.{number}\.{number}(?:-{prerelease}(?:\.{prerelease})*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?'

def runtime(version):
    if not isinstance(version, str) or not re.fullmatch(version_pattern, version):
        raise ValueError('Invalid T3 service version; saved state retained.')
    directory = home / 'runtime/versions' / version
    if (directory / '.install-complete').read_text().strip() != version or not os.access(directory / 't3', os.X_OK):
        raise ValueError('Selected T3 runtime is incomplete; saved state retained.')
    return directory / 't3'

try:
    if starting and not state_path.exists():
        # Adopt the official installer's selection once, before any server runs.
        # Preflight checks the actual database and the upstream protocol contract.
        result = subprocess.run([str(binary), '__service-preflight', '--database-path',
                                 str(home / 'userdata/statev2.sqlite'), '--launcher-protocol', str(protocol)],
                                check=True, capture_output=True, text=True, timeout=30)
        preflight = json.loads(result.stdout)
        if not isinstance(preflight, dict):
            raise ValueError('Invalid T3 service preflight response.')
        if preflight.get('status') != 'ready' or preflight.get('launcherProtocol') != protocol:
            raise ValueError(preflight.get('reason', 'T3 service preflight failed.'))
        version = preflight['version']
        runtime(version)
        state = {'protocol': protocol, 'activeVersion': version}
        state_path.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix='.xur-service-', dir=state_path.parent)
        try:
            with os.fdopen(fd, 'w') as stream:
                json.dump(state, stream)
                stream.flush()
                os.fsync(stream.fileno())
            # Publish without replacing an existing launcher's state.
            try:
                os.link(temporary, state_path)
            except FileExistsError:
                pass
            directory_fd = os.open(state_path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
        finally:
            os.unlink(temporary)

    if state_path.exists():
        state = json.loads(state_path.read_text())
        if not isinstance(state, dict) or state.get('protocol') != protocol:
            raise ValueError('Unsupported T3 service protocol; saved state retained.')
        binary = runtime(state.get('activeVersion'))
        # Existing pending/committed/rollback records belong to upstream. Its
        # launcher validates and recovers them before starting the selected child.
    os.execv(str(binary), [str(binary), *(['__service-launcher'] if starting else args)])
except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
    sys.exit('T3 service startup failed: ' + str(error))
XUR_T3_SERVICE
)" "$@"
