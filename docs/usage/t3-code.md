# T3 Code

T3 Code runs through Persisted Compute as a DinD container or native host service,
with shared access to all host GPUs.
It starts automatically after installation and reboot, independently of profiles.
Phones and desktops connect
**directly to T3 Code on TCP port 3773**.
Xur does not proxy its HTTP or WebSocket connections.

1. Open **T3 Code** and select **Install**. Stay on the page while it installs;
   connection and account controls appear when it finishes.
2. Auto-run is enabled by default. On first start, T3's native installer downloads the latest stable version into
   the persistent home volume. This needs internet access; workload logs show
   installation progress and any download errors. Later starts launch the saved
   installation immediately. Use T3's update controls to update the server;
   harnesses and development tools check for updates in the background.
3. Install the T3 Code app on your device, then return to Xur's
   **T3 Code → Create pairing link**. Paste the link into **Add environment** in
   the app, or scan the QR code on your phone. Each link is single-use and expires
   after five minutes.
4. Use **Coding accounts and harnesses** on Xur's T3 Code page to connect a
   subscription, or install a harness and choose a model under **Use Xur models
   in T3**.

After pairing, open the saved environment in the T3 Code app to reconnect.
Turn **Auto-run** off to stop T3 Code and keep it off, including after reboot.
Turn it back on to start the same installation. Projects, accounts, pairings and
installed updates remain saved. If startup repeatedly fails, the page shows the
error and a **Retry** button after five attempts.

Create another link for each additional device. Xur sign-in is required to mint
links; Xur API keys cannot mint them. T3 handles authentication and permissions
on its own port. Treat pairing links as credentials.

The URL uses the hostname or IP used to open Xur, with HTTP port 3773. Both devices
must be able to reach that address over the LAN or tailnet. Tailscale encrypts
traffic between tailnet devices; direct LAN HTTP does not provide TLS. Use the
T3 Code app for this direct HTTP connection. See the upstream
[remote access guide](https://github.com/pingdotgg/t3code/blob/main/docs/user/remote-access.md)
for T3's other connection options. Xur does not configure those services.

One T3 Code workload can run on the host at a time. If another service already
uses port 3773, its selected runtime fails to start and workload logs show the error. T3's
traffic is excluded from Xur's inference gateway routes.

## Runtime configuration and switching

Choose **Runtime → Host** (native host service) or **DinD** (Docker-in-Docker
container), then select
**Apply runtime**. Existing installations default to the DinD container.
The setting persists across reboot; changing it while auto-run is off keeps T3
off and uses the new mode the next time you enable it.

Both modes use the **same existing named home volume**, mounted at `/home/node`,
and the same `.t3` database, pairing credentials, accounts, native T3 release,
projects, hostname and TCP port 3773. There is no data copy, ownership conversion,
new environment or fresh pairing. Keep using the saved environment in the T3 app.
Required native tools or Docker packages are prepared while the current server
stays available. A failed preflight leaves that server alone. Xur then stops the
old server, verifies it stopped and starts the selected runtime. A failed handoff
restores the previous runtime and reports the error. Interrupted switches resume
or roll back after an agent restart.

The handoff briefly restarts T3: an existing TCP/WebSocket connection cannot stay
open while its server moves between processes. The paired client reconnects to
the same address using its saved credentials. Finish active commands before
applying; ongoing terminals, agents and account sign-in flows may be interrupted.
Outstanding pairing links keep their existing expiry during the handoff.

Native mode runs a systemd service as numeric UID/GID 1000, preserving the
container home’s ownership. It uses the host kernel, network, devices, tools and
libraries, without an OCI container or separate OS image. A systemd filesystem
namespace supplies the existing `/home/node`, `/host`, `/models` and `/model-sets`
paths, including on an immutable host root. It is a set of host bind mounts, not
a copy of the host OS. Native projects can also use host paths directly with the
host user's permissions. Folder selections control the shortcuts and additional
file access; they do not isolate native agents from that user's other files.
**Full host access** additionally provides `xur-host` root commands in either mode.
Native mode does not start the inner Docker daemon; use the host's container tools
with their normal user permissions, or select DinD for a private Docker engine.

Node, Python, Git, SSH, curl, ripgrep and Bash are installed for native mode from
conda-forge into `~/.local/share/xur-native`; the OS image is not modified. Their
package metadata and license notices remain in the persistent mamba cache.
To update this native tool environment, run
`micromamba update --all --yes --prefix ~/.local/share/xur-native --override-channels --channel conda-forge`.
T3 and harness native update controls work in either mode. Native service output
is available through Xur's workload logs or
`journalctl -u xur-t3-code-native.service`.

## ChatGPT and Claude subscriptions

Sign in to the account on the persistent T3 server, even when using Xur from a
phone or another computer. An eligible ChatGPT or Claude subscription uses the
provider's normal account flow; an API key is not required.

- **ChatGPT:** choose **Sign in with device code**, open the supplied OpenAI
  page and enter its short code. If device sign-in is unavailable, expand
  **Advanced sign-in** to use a browser login and paste the final localhost URL
  back into Xur. Use **Open Codex in T3** for the configured subscription entry.
- **Claude:** choose **Sign in with Claude**, follow the official link, and paste
  its one-time code into Xur. Then use **Open Claude in T3**.

Harnesses install automatically. **Retry setup** retries a missing installation.
After sign-in, the button becomes a quieter **Sign in again** action.

Codex sign-in reuses T3's default **Codex** provider when it uses the same native
CLI and credential directory. Existing names, model preferences and other settings
are preserved. A matching custom instance can also be reused; the link opens the
actual selected instance. Managed accounts, custom routing, separate credential
directories and disabled providers are not overwritten. If no compatible instance
is available, sign in through that provider's settings in T3.

Older Xur sign-ins created an additional **ChatGPT subscription** provider. If it
duplicates the default Codex account, hide its models in T3's provider settings to
remove duplicate picker choices while retaining the provider for existing threads.
Xur does not delete or disable old entries automatically. Keep intentionally
separate accounts and local-model providers enabled as needed.

Xur shows installation, waiting, verification and signed-in states. A page
refresh can recover a sign-in while the agent and T3 runtime are still running.
Cancel or restart an expired sign-in; it has a fifteen-minute limit. Provider
passwords and subscription tokens never pass through the Xur page. The one-time
Claude code is sent over Xur's authenticated, CSRF-protected connection, held
only for the active flow, and not logged. Native credentials remain under
`/home/node/.codex`, `/home/node/.claude`, or T3's own managed-provider storage.
The status on Xur's Codex card describes this CLI account; any separately
managed account in T3 has its own status.

Use the native provider controls or CLI in T3 to sign out or change accounts.
For provider-specific prerequisites, see
[Codex authentication](https://developers.openai.com/codex/auth/) and
[Claude Code authentication](https://code.claude.com/docs/en/authentication).

## Models from Xur profiles

1. Wait for **OpenCode** or **Codex** setup in **Coding accounts and harnesses**.
2. Enable T3 Code and load a profile containing the desired language-model workload.
   Saved but unloaded models remain listed with a link to review profiles.
3. Open Xur at its HTTPS LAN or Tailscale address, reachable from the container.
   Choose the exact **Served model** ID, harness, and serving context limit.
   Xur discovers IDs from `/v1/models`; a name such as DeepSeek 1017 is a display
   label, not an assumed API ID. The context limit is filled from engine metadata
   when available; otherwise enter the limit configured for that server.
4. Choose **Connect to T3**. Xur checks a streamed tool call and its returned
   result from inside the T3 container, using the actual TLS and inference path.
   A failure leaves the connection disabled and explains what to check.
5. Open the saved provider in T3, refresh provider status, and select the model.
   When updating an existing OpenCode connection, let current work finish before
   refreshing; T3 may retain its helper process until it has been idle briefly.

OpenCode uses streaming Chat Completions. Codex requires streaming Responses
API tool calls; exposing `/v1/models` alone does not establish compatibility.
Serving tool parsers and chat templates must also support the model. A successful
probe establishes basic protocol compatibility, not coding quality or support
for images, every tool schema, reasoning controls or very long conversations.

Each connection has its own provider entry, configuration and inference-only
credential. Codex local-model settings use a separate home from the subscription
account. OpenCode uses a separate configuration directory. The connection binds
the exact workload definition and served model ID: unloading it returns a
model-unavailable error, and another workload taking its route cannot redirect
the request. Load the original workload again to resume. Xur does not load a
profile automatically or switch to a paid provider when a model is unavailable.

**Connect to T3** also checks an existing connection's files. Missing storage,
changed settings, a changed server address/context limit, or a revoked key can
be repaired by connecting again. A successful repair rotates the connection's
key. Keys appear in **Settings → Manage API keys** as `coding` access; revoking
one disables subsequent requests. They have no expiry until revoked. Existing
in-flight requests are not cancelled by key revocation.

Local-model requests pass through Xur's authenticated inference endpoint and
the gateway's workload identity checks. The T3 helper trusts Xur's public TLS
certificate without disabling certificate validation; no private server key is
copied. Xur stores only credential hashes and key IDs. The usable model key and
provider configs stay in T3's private persistent home, so back up and protect that
volume like the projects and account credentials it contains.

## GPU access

Coding agents and project commands inside T3 can use all host GPUs, including
GPUs assigned to model or workstation workloads. No GPU selection is needed in
the profile editor, and T3 also runs on hosts without GPUs. Access does not
reserve GPU memory: GPU work in T3 shares capacity with other workloads. Stop
that work before a profile change that needs the GPU to be released; Xur's
existing device-release checks still apply to allocated workloads.

NVIDIA GPUs use the host's CDI configuration (`nvidia.com/gpu=all`), including
driver libraries. AMD and Intel GPUs receive their DRM devices; AMD also receives
`/dev/kfd`. T3 runs as `node` with the numeric host device groups needed for
access. In container mode, the outer container is privileged to support Docker-in-Docker,
disabling SELinux, seccomp and device isolation. Native mode uses the host GPU
devices and driver libraries directly, retaining their permission groups.
Device access does not install CUDA, ROCm, Level Zero or project-specific libraries;
install the tools needed by your project inside the coding environment.

Persisted Compute replaces a legacy T3 container that lacks shared GPU access
using its existing volume. After changing host GPUs or device permissions,
disable and re-enable T3 to refresh its device mappings and supplementary groups.

## Persistent storage

The named volume mounted at `/home/node` in both modes retains projects, T3's database and
pairings under `/home/node/.t3`, the installed T3 runtime and updater selection,
and provider credentials. Keep repositories in
`/home/node/projects`. Tools installed under `/home/node/.local` also persist;
changes elsewhere in the container can be lost when replacing it.

Switching, unloading or deleting profiles does not stop T3 Code. Existing T3
profile references are retired while their original revisions remain available.
Migration adopts the existing runtime identity and volume; it does not create a
second server. If several independent T3 volumes exist and none is active, Xur
reports the ambiguity instead of choosing one or creating empty storage.
Volumes are visible on **Storage**. Back up project and credential data separately
from Xur's configuration export.

### Server drives and model files

After installation, expand **Storage and host access** on the T3 page. By default T3 can
read and edit all server folders and mounted drives under `/host`. For example,
`/var/mnt/xur/projects` on Xur is `/host/var/mnt/xur/projects` in T3. This includes
system files; these are real files, not copies. New mounts propagate into the
running container. Disconnected drives must be mounted on Xur before use.

Turn off **All drives and folders** to share only the full server paths you enter,
one per line. Leave the list empty to remove those host mounts. The separate
model-library option mounts both selected model stores at `/models` and
`/model-sets`, following Xur's configured storage location. Turn off both options
and leave the folder list empty to use only T3's own persistent home. Removing
the model shortcuts does not hide model files accessible through `/host`.

Saving applies the settings by restarting the enabled runtime, preserving its
home, accounts, projects and auto-run setting. Disabled T3 stays disabled.
Missing selected folders or model storage report an error rather than silently
creating an empty replacement. Choose real directories instead of symlink paths.

T3 keeps UID/GID 1000 in both modes (the `node` user in the container). Shared storage grants file-permission access
without changing ownership or SELinux labels on server directories. Read-only
filesystems remain read-only. Existing repositories owned by another user may
require Git's explicit `safe.directory` approval for that repository.

### Full host access

**Full host access** is off by default. Enable it under **Storage and host
access**, then select **Save access settings**. Saving restarts an enabled T3;
the setting survives reboot and container replacement.

This grants agents complete root control over Xur, including system services,
devices, credentials and every host file, regardless of the folder selections.
Use `xur-host id` to check the host identity, `xur-host systemctl status xur-agent`
to run a host command, or `xur-host sh -lc 'your command'` for a host shell.
Commands run from the host root directory with the host's tools and namespaces.
Host filesystem restrictions such as read-only mounts still apply.

T3 itself continues running as `node`, preserving ownership of projects,
credentials and native updates. The privileged container installs a root-owned
setuid helper in its disposable filesystem and shares host PIDs so that
`xur-host` can enter the host environment. Turn the option off and save to
restart the runtime, remove the helper and restore the selected storage
policy. Docker-in-Docker still requires a privileged outer container; this
setting controls the host command helper and host PID access, and is not a
security boundary for untrusted agents. Disabling access does not undo host changes or terminate independent
host services an agent has already started.

### Docker-in-Docker

In **DinD** mode, T3 includes a private **rootless Docker Engine**, **Docker Compose** and
**Buildx**. Run `docker info`, `docker compose version` or `docker buildx version`
in a T3 terminal. The daemon runs as `node` inside T3; Xur does not mount the
host's Docker or Podman socket. Bind mounts use paths as seen inside T3, so a
project under `/home/node/projects` can be mounted at that same path.

Docker's signed stable APT repository supplies the latest packages without
version pins. Binaries, plugins and package copyright notices stay under
`~/.local/share/xur-docker`; images, build caches and named volumes stay under
`~/.local/share/docker`. These survive container replacement with the existing
home volume. Each start checks for package updates in the background. An
installed daemon starts immediately from its saved selection, including offline;
a new selection takes effect at the next T3 restart. First installation requires
internet access. Failed downloads retain the last complete selection.

T3 waits for Docker readiness before starting its server. Docker startup errors
and an unexpected daemon exit appear in workload logs and fail the service so
Persisted Compute can retry. Stopping T3 also stops the inner daemon. The Docker
API uses a private Unix socket at `/run/user/1000/docker.sock`; no Docker API TCP
port is exposed. Inner published ports are inside T3's network namespace; only
T3's port 3773 is published to the host. Rootless Docker without an inner systemd
session has limited cgroup resource controls; check `docker info` before relying
on per-container CPU or memory limits.

Use fully qualified images such as `mirror.gcr.io/library/alpine:latest` in
`docker run`, Dockerfiles, Compose files and Buildx builds. Docker Hub images
must use Google's cache explicitly; there is no configured fallback to Hub.
GHCR and Quay retain their own registry addresses. A missing cached image must
be resolved explicitly rather than retrying against Docker Hub.

In container mode, the outer T3 container uses privileged mode as required by
[Docker's rootless DinD setup](https://docs.docker.com/engine/security/rootless/tips/#rootless-docker-in-docker).
It has access to host devices even when **Full host access** is off. Treat T3
agents as trusted server users; folder selections and the host command helper
are convenience controls, not isolation from hostile code.

Existing T3 installations adopt this runtime policy while preserving their
recipe identity and home volume. Disable and re-enable T3 to recreate an old
container if requested by the service. The first migration of an older image
also installs namespace/network prerequisites in its disposable filesystem.

### Agent tools

Node/npm, Git, SSH, curl, Python and ripgrep are included in the base image.
GitHub CLI, uv/uvx, fd, jq, Git LFS, rsync, zip/unzip and SQLite install in the
persistent home on startup. The tools become available as their independent
background installers finish. The tool bundle also keeps ripgrep current.
Use `gh auth login` to connect your GitHub account. Git LFS initializes its
filters in the persistent home using `git lfs install --skip-repo`, without
forcing replacement of custom filter settings.

uv uses its official installer and `uv self update`. The other utility binaries
and their shared libraries live in `~/.local/share/xur-tools`, managed by
micromamba from conda-forge; its cache and package notices live under
`~/.local/share/mamba`. Versions follow the current channel, without fixed pins.
Each startup refreshes the tools without waiting before starting an installed
T3 server. Manual updates can use `uv self update` and
`micromamba update --all --yes --prefix ~/.local/share/xur-tools --override-channels --channel conda-forge`.
Project-specific SDKs and browser downloads can be installed separately.

## Updates

Use T3's sidebar update button or **Settings → Environments → Update server**.
Xur runs T3 under its native service launcher, so T3 can download, verify and
activate the requested release, restart the server and reconnect your client.
Finish active coding work first: a server update can interrupt agents and terminals.
No new Xur release or container rebuild is needed for normal T3 updates.

Older Xur containers ran `t3 start` directly. T3 deliberately disables remote
self-updates for foreground servers, so its update dialog reported that the
server needed a manual update. After installing this Xur fix, turn **Auto-run**
off and back on once to adopt the service launcher. Keep the existing storage;
projects, credentials and pairings remain in place.

T3's installer and updater keep the runtime under `/home/node/.t3/runtime` and
initially select it with `/home/node/.local/bin/t3`. On first service startup,
Xur checks compatibility and adopts that selection into T3's native
`runtime/service-state.json`. Afterwards that state is authoritative, including
pending update and rollback records. Restarts and replacement containers preserve
it even if the CLI symlink still points at an older release. All runtime and update
state is in the persistent volume and writable by the workload user. Xur does not
run a competing `t3 update` in the background. The native
installer checks the downloaded archive against upstream release checksums and
keeps its local web assets and dependency notices.

In container mode, Podman owns the container lifecycle; no nested systemd service is required. The
outer container is privileged for rootless DinD. Full host access additionally
shares host PIDs and installs the host command helper. The integration uses
upstream's internal service launcher
and checks its protocol before initial adoption. An incompatible launcher protocol,
invalid state or missing selected runtime fails visibly without resetting saved
update state. A future incompatible upstream service protocol may require a Xur
integration change; normal releases on the supported protocol update independently.

The Node base follows its LTS channel through Google's Docker Hub cache; there
is no fallback to Docker Hub on a cache miss. T3's MIT notice is retained
separately from Xur's license.

Codex, Claude Code and OpenCode install and update in parallel on startup using
their upstream installers and native update commands. Their executables and
credentials stay in the persistent home. The setup buttons retry these same
installers, with no fixed versions. Upstream licenses and account terms apply.

Saved run intent requires `persisted-compute-v1` support. Xur rejects rollback
to a release that cannot preserve it. See the [runtime implementation notes](../development/persisted-compute-runtime.md) for migration and recovery details.

## Validation

Automated tests cover recipe validation, single-server selection, persistent
storage reuse, initial installation, updater selection across replacement
launchers, offline restart, direct IPv4/hostname and IPv6 links, pairing responses
and rollback compatibility. The optional native service contract check runs the
actual upstream supervisor with synthetic server children to verify IPC update
handoff, commit, database rollback, shutdown and restart persistence:

```sh
python3 -B tests/Xur.Integration.Tests/t3-service-native.py /path/to/installed/t3
```

Building and booting this container on a Xur host
remains a separate integration check.

The account/model setup checks use synthetic CLI sign-ins and streamed model
responses, including cancellation, credential isolation, repair, failed probes,
profile unloads and route reassignment. Desktop/phone browser checks use the real
Razor page with fake accounts. OpenCode configuration parsing is also covered. Real subscription authorization and inference from a deployed T3
container still require validation on the target host; these tests do not consume
subscription usage or run a real model.

## Xur MCP in the T3 container

Open **Permissions and storage** on the T3 Code page to choose **Read Xur
data** and **Change Xur settings and run actions**. Changes imply read access.
Both permissions default to off. **Full host access** inherently grants both;
the individual controls are hidden while it is selected. Saved individual
choices return when full host access is turned off. Folder access is separate:
MCP permissions do not restrict files already shared with the container.

Save the permissions, wait for T3 to be running, then select **Connect or repair
Xur MCP** from Xur's HTTPS LAN or Tailscale address. Start a new T3 thread after
connection. This registers a C# stdio MCP server using the official MCP SDK inside the
container, registered as `xur` for native Codex, Claude and OpenCode accounts,
including Xur's isolated local-model configurations. Custom managed provider
homes may need the same registration in their own configuration. Existing
unrelated harness settings are preserved; conflicting `xur` entries are reported
rather than overwritten. For an existing OpenCode JSONC configuration, merge a
local MCP named `xur` with command
`["/home/node/.local/share/xur-mcp/server"]` and enable it, then remove or migrate
the JSONC file to JSON before using the repair action.

The server, certificate and restricted API key live under
`/home/node/.local/share/xur-mcp`, on the persistent home volume. Credentials are
owner-only and are never placed in harness command arguments. Reconnection
rotates the key; the old key is revoked only after successful setup. The key
appears as **T3 Xur MCP** in API keys and can be revoked there. Permission changes
are enforced by Xur on every request, including existing MCP processes, and a
permission-service outage denies access.

The tools are `xur_catalog` (live API routes and request schemas), `xur_read`
(data/settings), and `xur_write` (settings/actions). These cover Xur's published
JSON APIs, including monitoring and telemetry, profiles, models, workloads,
network, displays, storage and updates. Existing API validation, revision and
approval requirements apply. Browser-only forms, manager credential creation, account
setup, installation, T3 pairing and T3 permission changes are not exposed.
Network-share management is available with MCP write permission, including SMB
accounts and connections; see [Network shares](storage.md#managing-shares-through-xur-mcp)
for the workflow and secret-input requirements. General automation keys remain
read-only for these operations.
Streaming endpoints and responses above 8 MiB require their dedicated clients;
binary responses below that size are returned as base64. No extra telemetry
store is created. HTTPS certificate and hostname validation remain enabled.

Native registration follows the [Codex MCP configuration](https://learn.chatgpt.com/docs/extend/mcp),
[Claude user scope](https://code.claude.com/docs/en/mcp), and
[OpenCode local MCP](https://opencode.ai/docs/mcp-servers/) formats.

### .NET and MCP updates

T3 installs the latest supported stable .NET SDK (including its runtime) into
`~/.local/share/dotnet` and exposes `dotnet` on the container PATH. Microsoft release
metadata selects the SDK and its SHA-512 checksum at installation time; preview
and end-of-life channels are excluded. The SDK and MCP refresh at container startup
and every 24 hours while it runs. The first installation needs internet access;
wait for it to finish before connecting MCP. Update failures appear in T3 workload
logs and `~/.local/share/xur-mcp/update-status.json`.

Each refresh builds the C# server with the latest stable official MCP SDK and
hosting packages, then checks stateless discovery, tool calls and rejection of legacy clients. Only a verified
build becomes active. Saved SDKs, MCP builds and selections survive container
replacement; an unsuccessful update preserves the last working pair, and existing
processes keep their original runtime. Identical updates reuse the same generation.
No Xur release is needed to refresh upstream .NET or MCP dependencies. Xur-owned
server source changes arrive through Xur updates. Python remains only in the
existing container/account setup helper, not the MCP server or its protocol handling.

### Stateless protocol only

Xur MCP accepts only the **2026-07-28** protocol revision. Every request must carry
its protocol version and client capabilities in `_meta`; there is no legacy
`initialize` handshake or downgrade fallback. This uses the revision's
[stateless stdio binding](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio),
so T3 harnesses still launch it locally without exposing an HTTP port.
Harnesses must support this revision; older clients fail explicitly. The update
verification checks direct calls before discovery, missing metadata after a valid
call, and rejection of all four earlier initialization-based revisions. SDK
package updates remain on the floating latest stable channel; the protocol
revision is an explicit compatibility boundary, not a dependency version pin.
