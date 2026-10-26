# Persisted Compute runtime

The initial implementation provides shared, durable container run intent and
uses T3 Code as its first application. It reuses `WorkloadRuntime` and its host
allocation locks, container identity checks, health checks and volume handling.
It does not add a competing container launcher.

`/var/lib/xur/persisted-compute.json` records each service's workload, enabled
state, revision, failure count, next retry and last error. Writes flush a private
temporary file before atomic replacement. Stopping a service removes its runtime
receipt/container, retaining the service definition and named volumes. Original
T3 runtime IDs are adopted when present; otherwise a prepared T3 recipe supplies
the image and existing volume. Several unrelated volumes require explicit
selection through the agent's `/persisted-compute/t3-code` endpoint with a
`recipeId`; discovery never guesses between different environments.

The agent reconciles enabled services after startup, installation and crashes.
Failed starts back off for 5, 10, 20 and 40 seconds; the fifth failure requires
Retry. The failure count survives agent restarts. Disabled intent cancels an
in-flight startup or pending retry. Native container restart policies are set to
`no` so they cannot bypass this intent or the retry limit. Application update
maintenance suspends reconciliation; healthy containers remain in their existing
systemd units through agent restarts. No recurring schedule is created.

T3 installation defaults to enabled. The authenticated T3 page changes this
setting through `POST /api/t3-code/enabled`. The runtime reports actual state and
errors separately from intent. The page has no profile lifecycle controls or
link to a separate workload manager. Direct T3 connections still use port 3773.

Every container start launches independent background update jobs for T3 Code,
Codex, Claude Code, OpenCode, GitHub CLI (`gh`), uv and the agent utility bundle. An installed T3 server starts immediately;
only a missing T3 executable requires its initial install before launch. The
other CLIs install in parallel and become available as their installers finish.
A background T3 update takes effect on the next start. Pairing and other CLI
calls do not run updates. An agent restart that adopts a healthy container does
not count as a container start.

The first installation uses the official scripts at `t3.codes/install.sh`,
`chatgpt.com/codex/install.sh`, `claude.ai/install.sh` and
`opencode.ai/v2/install`. Subsequent starts use `t3 update --yes`, the Codex
installer, `claude update` and `opencode upgrade`. No release versions are pinned.
Installed binaries, update selections and account state stay in the writable
home volume. Update failures are logged without stopping an installed server.
An unavailable first T3 installation fails startup and can be retried.

GitHub CLI uses the latest stable release from `cli/cli`, resolving its Linux
archive and published SHA-256 checksums at startup. The verified executable is
atomically installed in `~/.local/bin/gh`; its upstream license is retained in
`~/.local/share/gh/LICENSE`. A failed download, checksum or executable check keeps
the saved binary. GitHub's native configuration and authentication remain under
`~/.config/gh` in the persistent home. Users sign in with `gh auth login`; Xur does
not copy the host's GitHub credentials into the workload.

The agent supplies the current launcher when creating T3 containers, including
containers from previously saved images. A bootstrap label identifies the
launcher; a container with an older bootstrap is replaced once while retaining
its recipe and volume. T3 receives the Xur machine's hostname, so its advertised
connection name uses the host name rather than a generated container identity.
Renaming the host also refreshes this container setting without replacing T3's
persisted environment ID. The setup retry buttons use the same installer commands.

Codex account setup defaults to `codex login --device-auth`. The collapsed
Advanced sign-in option runs native `codex login`; the user signs in on OpenAI's
site and pastes the final localhost callback URL into Xur. The helper checks its
loopback address, port, callback path and OAuth state against that pending CLI
attempt before forwarding it inside the same container. It does not execute the
URL or follow arbitrary redirects. A local `/success` redirect is completed
without copying its credential-bearing query. Callback URLs are submitted in an
authenticated, antiforgery-protected POST, never in Xur navigation URLs or logs.
Cancellation, expiry and container replacement invalidate the setup session.
Claude uses `claude auth login --claudeai`, with its official sign-in link and
one-time code form. Both flows retain native credentials in the persistent home;
neither proxies the T3 connection nor changes its port.

Persisted runtime receipts share the host runtime's allocation authority but are
excluded from observations used for profile planning. Old profile stop commands
cannot stop a persisted receipt. T3 is removed from profile selection and from
legacy profile plans; repaired profile revisions retain their original history.
A model chosen for T3 still follows its own inference profile. This does not
make the T3 service itself depend on that profile.

The updater's `persisted-compute-v1` compatibility guard prevents activation of
older releases once service intent exists. Existing container and T3 recipe
compatibility guards remain in place.

Validation covers default startup, off/on persistence, simulated reboot,
interrupted startup, bounded retry and retry cancellation, identity and volume
adoption, old profile stop isolation, preserved revision history and rollback
compatibility. Real host reboot and container execution require target-host
validation; fixture tests do not claim that coverage.

This is the lifecycle foundation, not completion of every proposal in the
[design](persisted-compute.md). The general service-management page, GPU/resource
overrides, usage attribution, Tailscale service origins and operation-scoped
Robotics peripheral ownership remain future work. Robotics is not migrated by
this change. The shared lifecycle currently accepts prepared containers without
exclusive GPU allocations; T3's existing runtime supplies its shared GPU access.

## T3 storage and utility tools

`t3-storage.json` stores independent service settings: `allDrives` and `models`
default to true; `folders` defaults to an empty list. The installed T3 page reads
and saves them through `/api/t3-code/storage`. Mutations require the browser
administrator and antiforgery token; automation API keys cannot broaden access.
A storage-policy label covers settings and selected model paths. Reconciliation
replaces stale containers while retaining their existing recipe and home volume.
Storage saves share the runtime's T3 allocation lock; retry refresh preserves the
latest enabled/disabled intent.

All-drive mode recursively binds `/` at `/host` read/write with slave propagation,
including drives mounted later. Selected folders instead bind at `/host` followed
by their original path. Model aliases resolve through `DataStorage`, call its
availability checks, and fail if a selected store is missing. No storage move,
recursive ownership change or host SELinux relabel is performed. File access uses
ambient `DAC_OVERRIDE` with UID/GID 1000, keeping native CLIs non-root. Label
isolation is disabled for shared files; the container is not privileged. This
mode intentionally gives agents broad server file access. Turning all sharing
off removes both the binds and that capability on container replacement.

The utility bundle installs ripgrep, fd-find, jq, git-lfs, rsync, zip, unzip and
sqlite from conda-forge into `~/.local/share/xur-tools`. Micromamba is installed
with its official script, updates itself, and resolves current package versions
and checksums through its native package manager. uv uses its official installer
and self-updater. Both are independent background jobs. All package libraries,
metadata, caches and update selections persist; saved container images receive
the current launcher and PATH without rebuilding. Pairing never runs updates.

Validation includes actual upstream tool installation in an isolated home,
parallel/offline startup fixtures, storage defaults and persistence, selected
folders and invalid paths, model relocation/missing storage, endpoint forwarding,
and browser settings interactions. Actual bind permissions and propagation need
a container-capable Xur host; argument tests do not substitute for that check.
