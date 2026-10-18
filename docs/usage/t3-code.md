# T3 Code

T3 Code runs as a persistent CPU container. Xur prepares, starts and stops the
workload; phones and desktops connect **directly to T3 Code on TCP port 3773**.
Xur does not proxy its HTTP or WebSocket connections.

1. Open **T3 Code → Prepare T3 Code**. Preparation downloads the pinned upstream
   release and builds the image. **Containers** shows progress and errors.
2. Edit a profile, add a workload, select **T3 Code**, and save and load the profile.
3. Return to **T3 Code → Create pairing link**. Use **Pair this device**, scan the
   QR code on a phone, or paste the link into **Add environment** in the T3 Code
   desktop app. Each link is single-use and expires after five minutes.
4. Configure a provider in T3 Code. Managed provider sign-in and separately
   installed provider CLIs follow T3 Code's own setup instructions.

After pairing, **Open T3 Code** reconnects the browser using T3's saved session.
Create another link for each additional device. Xur sign-in is required to mint
links; Xur API keys cannot mint them. T3 handles authentication and permissions
on its own port. Treat pairing links as credentials.

The URL uses the hostname or IP used to open Xur, with HTTP port 3773. Both devices
must be able to reach that address over the LAN or tailnet. Tailscale encrypts
traffic between tailnet devices; direct LAN HTTP does not provide TLS. The hosted
T3 web client requires HTTPS, so use the locally served web app or desktop client
for this direct HTTP connection. See the upstream
[remote access guide](https://github.com/pingdotgg/t3code/blob/v0.1.0/docs/user/remote-access.md)
for T3's other connection options. Xur does not configure those services.

One T3 Code workload can run on the host at a time. If another service already
uses port 3773, its container start fails and workload logs show the error. T3's
traffic is excluded from Xur's inference gateway routes.

## Persistent storage

The named volume mounted at `/home/node` retains projects, T3's database and
pairings under `/home/node/.t3`, and provider credentials. Keep repositories in
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
Additional development tools and provider authentication are configured inside
the environment. Provider services need their own network access; the web app
assets and QR generation are served locally.

## Version and validation

The preset uses the checksum-verified T3 Code **0.1.0** Linux x64 distribution,
including its local web assets and dependency notices. Its MIT notice is retained
separately from Xur's license. The Node base comes from Google's Docker Hub cache;
there is no fallback to Docker Hub on a cache miss.

Xur marks this workload as requiring `t3-code-v1` support, preventing rollback to
an application version that cannot read its recipe. Preparing an image does not
replace an active container; select the new recipe and apply the profile change.

Automated tests cover recipe validation, single-server selection, persistent
storage reuse, direct IPv4/hostname and IPv6 links, pairing responses and rollback
compatibility. The 2026-10-17 source checks passed 2,053 unit assertions and 139
updater assertions. The pinned upstream executable also completed native pairing
over a direct LAN port in the collaborative browser at phone size; no agent task
was started. Building and booting this container on a Xur host remains a separate
integration check.
