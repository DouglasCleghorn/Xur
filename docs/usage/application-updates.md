# Application updates

Open **Updates → Xur application → Check for updates**, then **Update Xur**.
The default source is public GitHub Releases at `DouglasCleghorn/Xur`; no GitHub
account or token is required to download updates. The updater follows only
GitHub's HTTPS release/CDN redirects and pins the resolved release tag before
fetching its descriptor, signature and bundle. A new release appearing during a
download cannot mix files from different releases.

For local development, open **Settings → Local build testing**, enable the
option, and save your development computer's address (for example
`192.0.2.10:8088`). Local servers support HTTP or verified HTTPS, including
bracketed IPv6. HTTP defaults to port 8088. Redirects are rejected. Turning the
option off restores GitHub Releases while retaining the local address for next
time. The old arbitrary-server setting alone no longer enables local updates.

Both sources require the same trusted Ed25519 signature, archive/file hashes,
compatibility checks and anti-rollback sequence. There is no unsigned-build or
TLS-verification bypass. A local release newer than the current public release
will stay installed until a newer public release exists, or you explicitly roll
back to the retained previous installation. Settings survive app/OS updates in
`/etc/xur/application-updates.json`.

The terminal menu also offers **Updates → Xur application**. CLI equivalents:

```bash
xur application-updates status --json
xur application-updates check
xur application-updates update
xur application-updates rollback
```

Authenticated automation uses `GET /api/application-updates` and
`POST /api/application-updates`, with a bearer token obtained through
`POST /api/bootstrap`. The request bodies are:

```json
{"action":"development","development":true}
{"action":"configure","server":"192.0.2.10:8088"}
{"action":"check"}
{"action":"update"}
{"action":"rollback"}
```

POST returns 202 after starting a separate systemd job. Poll GET for `busy`,
`operation.id`, `operation.stage`, `current`, `available`, and `previous`.
The manager briefly disconnects during activation; the Updates page reconnects.
Existing authentication cookies retain their original expiration. New profile
changes and inference requests receive 503 while activation waits for existing
requests to finish. If draining exceeds five minutes, the app stays unchanged.
Running engine containers and graphical sessions are independent of the three
Xur services and are not stopped by an app update.

A root-owned updater runs outside those services. It verifies an Ed25519 signed
release descriptor, archive hash, individual file hashes, host ABI and data
schema before changing the active symlink. Only application releases beneath
`/var/lib/xur/app/releases` are replaced; profiles, models, homes, credentials
and Tailscale state stay in their existing persistent locations. Releases with
an unsupported data schema are rejected. This implementation supports schema 1;
future incompatible migrations require an explicit migration implementation.

The old release is retained. Failed manager/agent/gateway health checks restore
it automatically. A durable transaction and a separate boot recovery unit
recover interrupted activation. The recovery implementation is installed at
`/var/lib/xur/updater/app-update`, outside the replaceable app, so a broken app
cannot replace its own recovery path. Normal updates execute the current bundle’s updater as a separate systemd job; the independent recovery copy retains the transaction recovery contract.
Manual recovery, when the web manager cannot start, is:

```bash
sudo python3 /var/lib/xur/updater/app-update recover
```

OS updates continue through the separate upstream Bazzite update mechanism.
Application updates do not upgrade the kernel or NVIDIA drivers, reboot the OS,
or replace the versions used by already-running model containers.

# Repository on the development computer

`xur-update-repository.service` is a systemd user service. It listens on all
IPv4 and IPv6 addresses on port 8088 and serves only
`.build/update-repository/`. It is read-only over HTTP and does not expose the
workspace or signing key. Start, stop and inspect it with:

```bash
systemctl --user start xur-update-repository
systemctl --user status xur-update-repository
journalctl --user -u xur-update-repository
```

It starts with this user's systemd session. The machine must be awake and that
service running for clients to download updates. If its LAN address changes,
change the local server setting or use a DNS name. This remains separate from the public GitHub release source.

Publish an app update without rebuilding the ISO:

```bash
./eng/package-update.sh --version 2026.09.15.6
```

This single command builds the app, runs the fast checks, signs a private staging
archive, installs that exact archive through the API in the running disposable
`editor-dev` VM, checks workload and login continuity, reboots to check model
persistence, and verifies the diagnostics download at desktop and phone widths.
Only then does it publish the tested archive and package source, checksums and
evidence under `dist/updates/<version>/`. Use `--vm NAME` for another prepared,
running development VM; it must already contain downloaded models. Failed checks
leave the public update unchanged. No ISO is rebuilt. `publish-update.sh` is an
alias for the same pipeline.

The repository verifies the published bundle and creates a versioned archive,
a signed descriptor, and an atomic `latest` pointer. Publishing and building
share a context lock. The signing key is private at
`~/.local/share/xur-updates/signing-key.pem` (directory 0700, file 0600); **back it up**.
Set `XUR_UPDATE_SIGNING_KEY` to use another private key path. The matching public key is included in the ISO and installed under `/etc/xur`.
Clients can change the server address, but only packages signed by that key
are accepted. Replacing the repository key requires explicitly replacing the
client trust anchor; serving a new public key over HTTP does not grant trust.

For a different build owner, generate an Ed25519 key and deliberately replace
`os/bootc/application-update-key.pem` before building their initial ISO.
Private keys never belong in source archives, ISOs, logs or the served directory.

# Testing

`tests/Xur.Integration.Tests/application-update.py` checks address parsing,
hostile archives and invalid signatures without touching the host installation.
`tests/Xur.Media.Tests/check-application-updates.py NAME` runs actual signed
updates through the API in an installed disposable VM. Bad releases are served
only by a temporary loopback test repository, never by the LAN repository.
The test preserves a real llama.cpp process, an active streaming request,
profile state, browser authentication and saved files, and exercises failed
health checks and explicit rollback. It also interrupts an activation, reboots,
and verifies recovery through the independent updater, with a native
workstation active during the update tests. `--bootstrap` is limited to development
VMs created by `eng/test-vm-app.py`; release media tests use the updater installed
by Anaconda.

The terminal root menu groups both update paths under **Updates**. Choose
**Xur application** or **Operating system**. Escape or the Back row returns to
Updates, then to the root menu. The noninteractive `xur application-updates …`
and `xur updates …` commands remain available.

## Public release publication

Source pushes do not publish application updates. After testing, committing and
pushing the source, package a version locally with the command above. To inspect
its verified public assets, then explicitly publish them:

```bash
python3 eng/publish-github.py --version VERSION
python3 eng/publish-github.py --version VERSION --publish
# Optionally add: --iso dist/xur-installer-x86_64.iso
```

Publication checks the package signature, hashes and source manifest, requires a
clean checkout matching `origin/main`, and requires a public GitHub repository.
It creates a draft release, uploads `latest`, the hash-named descriptor,
signature and archive, plus the source archive (and optional ISO/checksum), then
publishes it as latest. Failed uploads leave a draft, never a partial public
update. Each asset must be under 2 GiB. Keep signing keys on the release computer;
GitHub receives public artifacts only. CI source checks do not need a signing key.

`--build-only` on `eng/package-update.sh` retains the existing local build route
without VM tests; run `bash eng/test-fast.sh` separately when using it. It records
that release validation was skipped rather than claiming a VM pass. Public
publishing always remains a separate explicit command.
