# Application updates

Open **Updates → Xur application → Check for updates**, then **Update Xur**.
The default source is public GitHub Releases at `DouglasCleghorn/Xur`; no GitHub
account or token is required to download updates. The updater follows only
GitHub's HTTPS release/CDN redirects and pins the resolved release tag before
fetching its descriptor, signature and bundle. A new release appearing during a
download cannot mix files from different releases.

**Settings → Update channel** offers Stable, Nightly and Local build testing.
For local testing, enter your development computer’s server address (for example
`192.0.2.10:8088`) and paste its **Ed25519 public key in PEM format**. Obtain the
key from the contributor through a trusted channel; trusting it permits their
builds to run privileged code. Never paste or share the private key.

Local servers support HTTP or verified HTTPS, including bracketed IPv6. The
default port is 8088; redirects are rejected. Switching to Stable or Nightly
restores the official Xur signing key and retains the local server/key for next
time. Saving a channel does not install an update.

All sources require signature, archive/file hash and compatibility checks.
There is no unsigned-build or TLS-verification bypass. Replay protection is
separate for each public channel and each local server/public-key pair, so a
contributor build cannot raise the official release sequence floor. Settings
survive app/OS updates in `/etc/xur/application-updates.json`; the local key never
replaces `/etc/xur/application-update-key.pem`. Existing local configurations
continue using the official key until an explicit custom key is saved.

The terminal menu also offers **Updates → Xur application**. CLI equivalents:

```bash
xur application-updates status --json
xur application-updates check
xur application-updates update
xur application-updates rollback
```

Authenticated automation uses `GET /api/application-updates` and
`POST /api/application-updates`, with an **Automation** API key created in
Settings. Send it in `Authorization: Bearer …`; see [API keys](api-keys.md). The request bodies are:

```json
{"action":"channel","channel":"local","server":"192.0.2.10:8088","publicKey":"-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n"}
{"action":"channel","channel":"stable"}
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
schema before changing the active symlink. Application releases beneath
`/var/lib/xur/app/releases` are replaced; profiles, models, homes, credentials
and Tailscale state stay in their existing persistent locations. Releases with
an unsupported data schema are rejected. This implementation supports schema 1;
future incompatible migrations require an explicit migration implementation.

The old release is retained. Failed manager/agent/gateway health checks restore
it automatically. A durable transaction and a separate boot recovery unit
recover interrupted activation. The recovery implementation is installed at
`/var/lib/xur/updater/app-update`, outside the replaceable app, so a broken app
cannot replace its own recovery path. Normal updates execute the current bundle’s updater as a separate systemd job; the independent recovery copy retains the transaction recovery contract.
The new agent also repairs the exact legacy installer-owned
`/etc/systemd/system/xur-agent.service` startup dependencies before reporting
health. This runs on the first upgrade even when the preceding updater has no
migration hook. The agent waits for NetworkManager, while optional wired network
activation can fail or time out without blocking local setup. The repair preserves
unrelated unit settings, permissions and administrator drop-ins; custom dependency
lines, custom executables, symlinks and masks are skipped with a migration log
message. Administrator drop-ins remain authoritative and may retain their own
network dependency. A durable, root-owned completion marker is bound to the repaired unit bytes;
completed boots do not run SELinux or systemd commands again. Interrupted repairs
without a matching marker retry the bounded label and configuration reload.
No service restart or reboot is requested by the repair.

The dependency repair remains in place through application rollback and recovery:
older agents support these relaxed startup dependencies. A failed repair restores
the original unit unless an administrator has since changed it, and withholds
new-agent health so update recovery can run. An
installation whose existing dependency already prevents the new agent from
starting cannot execute this migration and still needs local recovery first.

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
signed descriptors, and an atomic `current` pointer. It also keeps the older
`latest` format for existing local test clients. Publishing and building
share a context lock. The signing key is private at
`~/.local/share/xur-updates/signing-key.pem` (directory 0700, file 0600); **back it up**.
`XUR_UPDATE_SIGNING_KEY` selects an official maintainer key and must match the
tracked trust anchor. Contributors use **`XUR_LOCAL_SIGNING_KEY`** instead:

```bash
export XUR_LOCAL_SIGNING_KEY="$PWD/.build/private/contributor-signing-key.pem"
python3 eng/update-repository.py keys
bash eng/test-fast.sh
./eng/package-update.sh --build-only --version 2026.09.20.1
./eng/update-server.sh start
cat .build/update-repository/application-update-key.pem
```

The `keys` command generates an Ed25519 key if the selected path does not exist,
with mode 0600, and exports only its public half. Paste that public PEM into
**Settings → Update channel → Local build testing**, together with the server
address. The tracked official key and installer trust anchor remain unchanged.
Custom keys are rejected by official Stable/Nightly publication. The public key
is served for convenience, but fetching it from the update server alone does not
establish trust. Keep private keys outside source and the served directory.

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

## Release channels and GitHub approval

Every push to `main` builds a Nightly application candidate; every push to
`release` builds a Stable candidate. The **Build and publish release** workflow
runs source, fast and browser checks. Nightly publishes automatically after the
required application checks pass. Stable waits for the maintainer's `stable`
GitHub Environment approval after hardware testing. Signing keys are unavailable
to build/test jobs, and superseded commits cannot advance a channel.

Installer builds are requested separately with **Run workflow → build_iso**.
They publish inspected media in a separate release after the matching application
release is available; they never move the application channel pointer. See
[installer release automation](../development/installer-releases.md) for candidate
retention, environment restrictions and validation requirements.

Nightly uses a small `nightly` release pointer to an immutable per-build release.
Stable uses its own small channel pointer. GitHub **Latest** follows the most
recent Stable application publication. Signed metadata binds each release to its
channel. Downloads resolve the pointer before fetching metadata and payload,
so a concurrent publication cannot mix release assets.

**Settings → Update channel** selects Nightly, Stable or Local build testing. Switching channels does
not install immediately; check for updates and apply the selected release.
Moving from a newer Nightly to an older Stable is allowed if its data/features
remain compatible. Replay protection applies separately within each channel.
If a public channel has no `current` pointer, the check reports “No Stable release
available” or “No Nightly release available,” clears any stale available update,
and keeps the running app. It does not try retired release paths. Network,
authorization, missing descriptor and signature failures remain update errors.

## Local build testing

`eng/package-update.sh --build-only --version VERSION` retains the existing local
build route without VM tests. Run `bash eng/test-fast.sh` separately. The package
records that VM validation was skipped rather than claiming a VM pass. Enable
**Settings → Update channel → Local build testing** at the bottom of Settings
and configure the local repository and public key. Signatures and compatibility
checks remain required. Select Stable or Nightly to return to official builds.

The older `eng/publish-github.py` can inspect legacy packages but cannot publish.
Public releases use the checked CI workflow and its channel-specific publication
policy. Contributor builds can still be served locally without GitHub.

## Compact releases and retired ext4 downloads

Official application releases contain `xur-update-x86_64.tar.gz` and
`xur-update.json`. Installer releases add `xur-<channel>-<version>-x86_64.iso`.
The updater never downloads the ISO. A check fetches a small channel pointer
and the JSON descriptor (normally a few KiB, bounded at 64 KiB). Installing
fetches the app archive once, directly into staging. An already-installed bundle
requires no archive download. Channel checks retain signature, channel, ABI and
replay validation; activation and rollback retain their health checks.

The JSON envelope has `schema: 2`, a `release` object and a base64 Ed25519
`signature`. The signed bytes are the release object serialized with Python's
`json.dumps(release, sort_keys=True, separators=(',', ':')).encode()` (ASCII-escaped
JSON, UTF-8, no trailing newline). The signature covers the archive filename,
hash/length, version, channel, sequence, compatibility fields and installer receipt.
Metadata and downloads are pinned to an immutable release. No archive extraction
happens before hash verification. `eng/verify-release.py` checks the same signature
and can verify an ISO without downloading the app archive.

Ext4-root releases and their public updater bridges were removed on 2026-10-03.
Public releases use only the signed compact format and channel `current` pointers;
no transition release or public legacy fallback is created. Existing ext4-root
installations require a backup and reinstall with Btrfs media. `/boot` remains
ext4 by design. See [Steam storage](steam-storage.md) for the filesystem requirement.

Contributor repositories publish `current`, an immutable `<bundle-id>.update.json`,
and `<bundle-id>.tar.gz`, using the contributor's configured public key. Legacy
files and signed schema-1 fallback remain supported only for local repositories
that lack `current` (HTTP 404). Invalid metadata and other HTTP failures never
trigger fallback. `eng/package-update.py` still
prepares local test updates; public publication goes through GitHub's approval-gated
release workflow.
