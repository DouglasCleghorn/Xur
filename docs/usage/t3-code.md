# T3 Code

T3 Code runs as a persistent CPU container. Xur prepares, starts and stops the
workload; phones and desktops connect **directly to T3 Code on TCP port 3773**.
Xur does not proxy its HTTP or WebSocket connections.

1. Open **T3 Code → Prepare T3 Code**. Preparation builds the image.
   **Containers** shows progress and errors.
2. Edit a profile, add a workload, select **T3 Code**, and save and load the profile.
   On first start, T3's native installer downloads the latest stable version into
   the persistent home volume. This needs internet access; workload logs show
   installation progress and any download errors. Later starts use the saved
   installation without downloading it again.
3. Return to **T3 Code → Create pairing link**. Use **Pair this device**, scan the
   QR code on a phone, or paste the link into **Add environment** in the T3 Code
   desktop app. Each link is single-use and expires after five minutes.
4. Use **Coding accounts and harnesses** on Xur's T3 Code page to connect a
   subscription, or install a harness and choose a model under **Use Xur models
   in T3**.

After pairing, **Open T3 Code** reconnects the browser using T3's saved session.
Create another link for each additional device. Xur sign-in is required to mint
links; Xur API keys cannot mint them. T3 handles authentication and permissions
on its own port. Treat pairing links as credentials.

The URL uses the hostname or IP used to open Xur, with HTTP port 3773. Both devices
must be able to reach that address over the LAN or tailnet. Tailscale encrypts
traffic between tailnet devices; direct LAN HTTP does not provide TLS. The hosted
T3 web client requires HTTPS, so use the locally served web app or desktop client
for this direct HTTP connection. See the upstream
[remote access guide](https://github.com/pingdotgg/t3code/blob/main/docs/user/remote-access.md)
for T3's other connection options. Xur does not configure those services.

One T3 Code workload can run on the host at a time. If another service already
uses port 3773, its container start fails and workload logs show the error. T3's
traffic is excluded from Xur's inference gateway routes.

## ChatGPT and Claude subscriptions

Sign in to the account on the persistent T3 server, even when using Xur from a
phone or another computer. An eligible ChatGPT or Claude subscription uses the
provider's normal account flow; an API key is not required.

- **ChatGPT:** after pairing, choose **Sign in with ChatGPT in T3**. This opens
  T3's managed Codex provider settings, including its install and sign-in flow.
  For CLI authentication, expand **Use the Codex CLI or a local model**, select
  **Install Codex**, then **Sign in with device code**. Open the supplied OpenAI
  page and enter its short code. Device login may need enabling in ChatGPT's
  security settings. Use **Open CLI subscription in T3** for this separate entry.
- **Claude:** select **Install Claude Code**, then **Sign in with Claude**.
  Follow the official Claude link on your current device and paste its one-time
  code into Xur to complete the server login. Use **Open Claude in T3**, refresh
  provider status, and select **Claude subscription**.

Xur shows installation, waiting, verification and signed-in states. A page
refresh can recover a sign-in while the agent and container are still running.
Cancel or restart an expired sign-in; it has a fifteen-minute limit. Provider
passwords and subscription tokens never pass through the Xur page. The one-time
Claude code is sent over Xur's authenticated, CSRF-protected connection, held
only for the active flow, and not logged. Native credentials remain under
`/home/node/.codex`, `/home/node/.claude`, or T3's own managed-provider storage.
The CLI status shown under Codex's expandable section describes the CLI account;
T3 displays its managed account status separately.

Use the native provider controls or CLI in T3 to sign out or change accounts.
For provider-specific prerequisites, see
[Codex authentication](https://developers.openai.com/codex/auth/) and
[Claude Code authentication](https://code.claude.com/docs/en/authentication).

## Models from Xur profiles

1. Install **OpenCode** or **Codex** in **Coding accounts and harnesses**.
2. Load a profile containing both T3 Code and the desired language-model workload.
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

## Persistent storage

The named volume mounted at `/home/node` retains projects, T3's database and
pairings under `/home/node/.t3`, the installed T3 runtime and updater selection,
and provider credentials. Keep repositories in
`/home/node/projects`. Tools installed under `/home/node/.local` also persist;
changes elsewhere in the container can be lost when replacing it.

Use the same prepared workload in multiple profiles to keep its instance when
switching. Unload, stop and profile deletion retain the volume. To prepare a
replacement image with the same data, select **Reuse T3 Code storage**. Creating
new storage starts a separate environment with no previous projects or sessions.
Volumes are visible on **Storage**. Back up project and credential data separately
from Xur's configuration export.

The image runs as the unprivileged `node` user and has no host directory or
container-engine socket mounts. It includes Node.js, Git, SSH, Python and ripgrep.
Additional development tools can be configured inside the environment. Harness
installation and provider sign-in need network access; the web app
assets and QR generation are served locally.

## Updates

Run `t3 update` in T3 Code's terminal to install the latest stable update using
T3's native updater. Once it finishes, unload and reload the profile in Xur to
restart the server on the updated version. Unloading interrupts workloads in
that profile, so finish active work first. No new Xur release or container
rebuild is needed.

T3's installer and updater keep the runtime under `/home/node/.t3/runtime` and
select it with `/home/node/.local/bin/t3`. Both are in the persistent volume and
writable by the workload user. Restarts and replacement containers honor that
selection; they do not reinstall or overwrite an updated runtime. The native
installer checks the downloaded archive against upstream release checksums and
keeps its local web assets and dependency notices.

For a workload prepared before persistent updates were supported, prepare a new
container with **Reuse T3 Code storage**, select it in the profile, and load it.
Its first start installs the latest T3 runtime alongside the existing data.
Subsequent updates use `t3 update`.

The Node base follows its LTS channel through Google's Docker Hub cache; there
is no fallback to Docker Hub on a cache miss. T3's MIT notice is retained
separately from Xur's license.

The optional install buttons pin `@openai/codex` 0.163.0,
`@anthropic-ai/claude-code` 2.1.306 and `@opencode/cli` 2.1.2 under the persistent
`/home/node/.local` prefix. They are downloaded from npm only when requested,
retain their upstream notices and terms, and are not bundled or relicensed as
Xur code. Installing again selects these pinned versions. T3's managed Codex
installation is independent of these buttons.

Xur marks this workload as requiring `t3-code-v1` support, preventing rollback to
an application version that cannot read its recipe. Preparing an image does not
replace an active container; select the new recipe and apply the profile change.

## Validation

Automated tests cover recipe validation, single-server selection, persistent
storage reuse, initial installation, updater selection across replacement
launchers, offline restart, direct IPv4/hostname and IPv6 links, pairing responses
and rollback compatibility. Building and booting this container on a Xur host
remains a separate integration check.

The account/model setup checks use synthetic CLI sign-ins and streamed model
responses, including cancellation, credential isolation, repair, failed probes,
profile unloads and route reassignment. Desktop/phone browser checks use the real
Razor page with fake accounts. The pinned OpenCode CLI also parses the generated
configuration. Real subscription authorization and inference from a deployed T3
container still require validation on the target host; these tests do not consume
subscription usage or run a real model.
