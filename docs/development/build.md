# Building Xur

From the checkout, run:

```bash
./eng/build-iso.sh --check
./eng/build-iso.sh
```

The default output is `dist/xur-installer-x86_64.iso`, with a
SHA-256 file and a report verifying files extracted from the ISO. Use
`--output dist/my-build.iso` to choose another filename. The build replaces that output only after it finishes and verifies the transfer.
Run the media tests before distributing it. Keep a previous release elsewhere
when rebuilding, or choose a different output path.

The ISO supports FAT32 file-copy mode as well as hybrid raw/DD writing.
The ISO contains the live installer and Xur, with no Bazzite OCI payload.
OSBuild retains its supplied BIOS/UEFI layout and EFI binaries. Inspection
rejects files over FAT32’s individual file limit and unexpected embedded OS payloads. See [Rufus media](../usage/rufus.md) for writing and test instructions.
`--inspect-existing` resumes final inspection/transfer for an already built
ISO only when the local and remote publication contexts match.

The entry point verifies or downloads the pinned .NET SDK, Tailscale archive
and Fedora builder image, starts the isolated builder, prepares the pinned
Image Builder when needed, publishes Xur, runs unit/process checks, builds
the ISO, checks embedded files and verifies the transferred ISO checksum.
`eng/prepare-fedora-builder.sh` is the Fedora-only tool preparation script.

The local QEMU/OVMF tool tree and KVM access must already be available, along
with Python 3.12+, SSH/SCP, tar and iproute2. `--check` reports missing
prerequisites. The expected QEMU tree is
`$XUR_BUILD_ROOT/qemu/usr` (default `~/.local/share/xur-build/qemu/usr`),
including QEMU, genisoimage, firmware and their shared libraries. These are
the user-local tools prepared for this workspace; the script does not install
system packages or change security policy on the Ubuntu host.

Build logs are in `.build/build-publish.log` and the builder VM
`/home/builder/iso-build-script.log`. The Fedora build also streams its output to
the calling terminal or GitHub Actions log, preserving a failed build exit code.
A local VM remains available for inspection;
CI stops its disposable builder on completion or failure.

The builder readiness helper runs both `cloud-init status --wait` and its JSON
status query through `sudo -n`. Fedora protects `/run/cloud-init/cloud.cfg`, so
an unprivileged fallback query fails even when initialization has finished.
Completed initialization with exit code 2 prints its recoverable warnings and
continues to toolchain preparation. Fatal exit codes, malformed status, unfinished
initialization or a nonempty fatal `errors` list stop the build. Read the emitted
status before retrying; this does not bypass failed package preparation.
`tests/Xur.Integration.Tests/builder-ready.py` covers these paths without a VM.
See [cloud-init exit codes](https://docs.cloud-init.io/en/latest/explanation/return_codes.html).


Build on a disposable Fedora 44 x86-64 VM with SELinux Enforcing. The development
host needs KVM, QEMU/OVMF, Python, tar, SSH and the pinned .NET SDK. No host-wide
AppArmor or SELinux changes are required. The recorded builder uses 16 GiB RAM,
8 vCPUs and a 120 GiB file-backed disk. Earlier installer tests used 24 GiB RAM and four vCPUs. The FAT32 media suite
completed offline installation with 8 GiB and the external OCI payload; see the current acceptance receipt for
the executed configuration. These are test configurations, not minimum requirements.

`eng/toolchain-lock.json`, `global.json` and NuGet lock files record selected
inputs. The live Fedora bootc base tracks the release tag
`quay.io/fedora/fedora-bootc:44` in `os/bootc/Containerfile`. Each build resolves
that tag once, builds from the resulting digest, and includes the digest, image
version and kernel in the ISO's `.embedded.json` inspection receipt. This avoids
depending on an old upstream manifest remaining available while keeping each
build's selected image identifiable. Rebuilding the same source can pick up new
Fedora 44 updates; changing the Fedora major version is an explicit source change.
The installed
Bazzite image is downloaded directly from its stable channel by Anaconda/bootc
at install time. Xur does not resolve or pin an installation digest.
`os/bootc/upstream-lock.json` retains historical
reference information; it no longer selects the online installation version. Live installer
packages are resolved by Fedora DNF during the image build; the accompanying
RPM inventories and built-image digests identify the delivered versions. This
is not a claim that rebuilding against changing RPM repositories is byte
reproducible.

1. Provision the Fedora builder with Podman, OSBuild, its dependencies and
   Image Builder 82.0.0 compiled from the verified source archive with vendored
   Go dependencies. `eng/start-builder.py` starts the development VM after its
   local toolchain and verified Fedora Cloud image have been prepared.
2. Place the checksum-verified Tailscale 1.102.4 Linux amd64 archive at
   `.build/downloads/tailscale.tgz` and run `bash eng/publish.sh`.
3. Run the tests under `tests/`. `bootstrap.py` launches the real published
   control process; its waiting Tailscale child is a test fixture. Media tests
   execute the upstream Tailscale binary from the ISO.
4. Copy the generated `.build/context/` and the three scripts
   `build-in-fedora.sh`, `context-receipt.py`, `label-live-manifest.py` into the
   Fedora builder. The build script expects the latter two in `/home/builder`.
5. In Fedora run `sudo bash build-in-fedora.sh /path/to/context`. Context
   verification fails on missing, extra or changed files. The script builds
   the live image without downloading Bazzite, obtains the maintained builder manifest,
   labels the live SELinux tree, and runs OSBuild. Its ISO output is
   `/home/builder/xur-output/bootiso/install.iso`.
6. Boot that exact ISO under UEFI and run the media tests against file-backed
   disposable storage. Rebuild after every shipped source or OS change.
7. Extract the assemblies and Kickstart template from the final ISO and verify
   them against the context receipt before creating release checksums.

The ISO keeps its boot initramfs at `images/pxeboot/initrd.img` and excludes the
redundant `usr/lib/modules/<kernel>/initramfs.img` copy from the live SquashFS.
The upstream pipeline copies the boot file before compressing the live root.
This reduces download size while retaining Zstandard compression, installer
features, drivers, firmware and the BIOS/UEFI layout. Media inspection checks
that the boot initramfs remains valid and its duplicate is absent.

The generic ISO pipeline needs explicit live-tree SELinux labeling; container
labels prevented PID 1 from starting under enforcement in an earlier candidate.
The manifest transformation changes labels and the duplicate-file exclusion,
not boot geometry or payload references. The image also provides `/usr/sbin/ldconfig` as a compatibility link
because Python's ctypes lookup uses `/sbin/ldconfig`; pyudev startup is checked
inside the live image build.

The .NET 10.0.401 Razor source-generator path failed with RZ1021 on this source.
`UseRazorSourceGenerator=false` uses the SDK's alternate Razor compiler; the
published application and generated routes are verified by running-process and
ISO tests. See the upstream [Razor issue](https://github.com/dotnet/razor/issues/13184).

Generated build contexts, VM disks, cookies, raw console output and downloaded
archives stay under `.build/` or the private local toolchain directory. They
are excluded from the source archive. Only redacted structured receipts and
anonymous UI screenshots are release evidence.

The live install launcher is labeled `install_exec_t`, so systemd starts it in
`install_t`. Labeling only the downstream bootc executable did not transition
out of the general service domain. A direct systemd execution probe confirmed
that the launcher label selects `install_t` under SELinux enforcement. The
bootc [SELinux implementation](https://github.com/bootc-dev/bootc/blob/v1.16.10/crates/lib/src/lsm.rs)
requires this domain's ability to apply labels not present in the running
policy. Xur does not enable bootc's permissive-mode fallback.

Answer scanning keeps each original device block-read-only and mounts a
read-only loop view with the same filesystem restrictions. This permits
independent scans of hybrid USB media's whole-disk ISO and child filesystems
without competing for the original mounted block device's filesystem claim.
The view is detached after unmount; failure to create, verify or clean up a
view keeps the gate locked.
Existing Btrfs volumes are scanned with `ro,rescue=nologreplay` so kernels that
reject the old standalone `nologreplay` alias can inspect them without replaying
the filesystem log. The block device and loop view remain read-only.

## Run the installer VM evidence suite

Prepare browser tools with `python3 eng/prepare-npm.py browser` and install
Chromium with `.build/browser/node_modules/.bin/playwright install chromium`.
Install the QR tools with
`python3 -m pip install --target .build/qr -r tests/Xur.Media.Tests/requirements.txt`.
After preparing Playwright/Chromium, the pinned zxing-cpp decoder in
`.build/qr`, and the answer fixtures with `make-answer-fixtures.py`, run:

```bash
python3 tests/Xur.Media.Tests/check-installer-suite.py \
  dist/xur-installer-x86_64.iso --prefix my-build --profiles
```

Use a new prefix for every build. The suite installs only on disposable
file-backed disks, reboots with USB media attached, hashes the data and media
disks, checks Settings at desktop/mobile widths, checks the visible QR across
log refreshes and installation, and exercises zero/one/multiple answer cases.
Ports 18081, 18082 and 5911 must be free. On failure the VM remains available
for inspection; its serial output, QR pixels and cookies are private.

After the normal installation and reboot assertions pass, the OS-management
tests provision an additional root test connection through QEMU's local guest
agent. `prepare-guest.py` selects a temporary GRUB debug-console boot and uses
that console to configure a test copy of the agent in `unconfined_service_t`.
This affects only the disposable test VM: the ISO, Xur services, physical-host
defaults and SELinux enforcement are unchanged. Receipts identify this test
transport. It allows observation and OS-update testing while Xur's own manager
and agent are stopped. The extra agent copy and override are never packaged.

The console uses Spectre.Console 0.57.2 on tty3 (Alt+F3); kernel output is
pinned to tty1 and logs use tty2. Console devices are opened with `O_NOCTTY`
so changing or closing a VT cannot give the web daemon a controlling terminal
and terminate it with SIGHUP. The real PTY ownership regression is checked by
`tests/Xur.Integration.Tests/terminal.py`.
The renderer holds console descriptors open for its lifetime so Linux VT
last-close termios resets cannot re-enable input echo and scroll the menu.

Both `getty@ttyN` and the `autovt@` aliases are masked for Xur's owned
terminals. A raw key parser handles arrows and consumes complete terminal
replies before accepting input. The renderer restores no-echo input mode and
the bounded screen every 500 ms independently of status/log collection.

The `--profiles` option also runs the installed profile API and browser tests,
real llama.cpp inference during a profile switch, a second reboot with an active
profile, and complete workload teardown. It downloads the current CPU engine and
model into the disposable VM. It also searches the live Unsloth catalog, serves
a selected GGUF, starts a native Plasma workstation, renders a Vulkan/XWayland
exercise, and verifies teardown and workstation recovery after reboot.

The live Fedora image supplies compatibility links for `/sbin/modprobe` and
`/sbin/sysctl`. The upstream Bazzite payload already supplies working paths and
is not modified for this. The installed real-model test exercises Podman's
network setup without manual module loading or filesystem changes.

After the full media suite and `check-api-initialization.py` pass for the same
ISO, collect build metadata and package the release:

```bash
python3 eng/collect-release-evidence.py
python3 eng/build-app-bundle.py --from-published
cp docs/usage/install.md dist/INSTALL.md
python3 eng/package-iso.py --vm my-build-install --discovery-prefix my-build
```

The collector verifies the builder ISO matches the local artifact, re-extracts
the application and installer helpers, and records image and RPM identities.
The packager rejects media evidence from a different ISO.

## Fast development loop

Run application checks without an ISO build:

```bash
./eng/test-fast.sh
```

This publishes the three applications and runs unit, authenticated API,
desktop/mobile browser, storage/file, model-lab, update, console, network and
profile lifecycle checks. Real-child switching tests exercise SQLite recovery and
streaming continuity. Logs are under `.build/fast/`; duration depends on available
native caches, downloads and machine speed. It does not establish physical GPU,
USB or ISO boot/install acceptance.

The interactive console fixture uses persistent HTTP/1.1 responses over a Unix
socket, matching the real server. Failed runs retain
`.build/fast/console-menu-transcript.log`; output assertions also print the
actual screen. Source CI runs this test early, while release CI runs the full
suite. The fixture cannot reboot or power off the host.

For browser, real-container and kernel-console testing, keep an installed test VM
and its model cache. Create a development clone once from a stopped, installed VM
that has been prepared by `tests/Xur.Media.Tests/prepare-guest.py`:

```bash
python3 eng/test-vm-app.py --name editor-dev --clone-from editor1-install --skip-publish
```

`editor1-install` is this workspace's existing baseline; replace it with the name
of your prepared installation-test VM on another checkout.

Thereafter update that running VM with the current published app bundle:

```bash
./eng/test-fast.sh
python3 eng/test-vm-app.py --name editor-dev --skip-publish
node tests/Xur.Media.Tests/check-login-format.cjs editor-dev
node tests/Xur.Media.Tests/check-profiles-ui.cjs editor-dev
python3 tests/Xur.Media.Tests/check-kernel-console.py editor-dev
```

The first clone, boot and bundle update took 75 seconds on this workspace;
updating the already-running VM took 31.5 seconds, excluding publication.

Omit `--skip-publish` to publish as part of the VM update. The script uses a
loopback-only temporary HTTP server and the VM's private QEMU guest agent,
verifies the archive and individual bundle files, switches the development
bundle and logs into the real application. It keeps the OS, SQLite state and
model cache. It restarts Xur services, so this is a development tool, not the
production application-update lifecycle. It never installs into the host or
updates `dist/`. Development VMs are marked as modified and cannot qualify a
release ISO. Stop one with `python3 tests/Xur.Media.Tests/qmp.py editor-dev quit`.

Rebuild the full ISO when installer packaging, boot configuration or OS contents
change, and once for final release verification. Do not run the entire ISO
pipeline for each CSS, form or controller edit. Keep final ISO install/reboot and
non-target-disk checks separate from this short iteration loop.

## GitHub release builds

Pushes to `main` build Nightly candidates; pushes to `release` build Stable
candidates. Source checks and the application build run in parallel. Successful
application builds always upload an update archive without approval. Nightly
updates publish as soon as both jobs pass; Stable publication requires GitHub
Environment approval after hardware testing. Branch restrictions and signing
remain enabled for both. New pushes cancel older runs on the same branch.
ISO builds run **only on manual dispatch with `build_iso` enabled**, even when
installer inputs change. They reuse the tested application context and verified
Fedora toolchain template, then publish a separate `-installer` release after
inspection (and approval for Stable). They do not delay or change the published
application update or its channel pointer.
Hosted build/inspection success is not an install/reboot test. The local commands
above remain available independently of CI. See
[installer release automation](installer-releases.md) for candidate retention,
runner resources, approval and signed ISO verification.

The console guard uses TIOCL_SETKMSGREDIRECT to pin kernel messages to VT1 and
restores console verbosity after installer tools change it. The kernel ring and
journal remain available. `console=tty1` is the only registered kernel console;
Xur writes its own menu/QR directly to tty3 and ttyS0. Kernel output therefore
cannot scroll the serial menu. The regression injects emergency-priority kernel
messages while VT3 is active, including after an external redirection reset.

## Publish an application update

After the initial updater-enabled ISO is installed, use:

```bash
./eng/update-server.sh start
./eng/package-update.sh --version 2026.09.15.6
```

Clients select **Settings → Update channel → Local build testing**, enter the
server address and trust its Ed25519 public key. Contributors can generate their
own key using `XUR_LOCAL_SIGNING_KEY`; see the application update guide below.
The single packaging command builds and tests the app, stages a signed package, verifies installation
and reboot persistence in the running disposable `editor-dev` VM, checks the
diagnostics download, and publishes the tested package on port 8088. Source,
checksums and evidence are saved to `dist/updates/<version>/`. It does not build
an ISO. Use `--vm NAME` to select another prepared development VM. See [Application updates](../usage/application-updates.md) for client APIs,
signing-key backup, rollback and the VM update tests. The full installer suite
with `--profiles` also checks application updates, native workstation and model
continuity, and interrupted-activation recovery on the newly installed system.

The app publication also bundles the lightweight DRM console. Its first build
uses `eng/build-console.py` in the disposable Fedora VM; subsequent app builds
reuse the verified native cache when source inputs match. See
[display consoles](../architecture/display-consoles.md) for its pinned source and runtime receipt.

Hosted native builds enable RPM documentation when installing build dependencies.
The Fedora container's default `tsflags=nodocs` also omits some dependency
COPYING files marked as documentation. Retaining them lets the native runtime
builders collect the original license notices for the application bundle.

For local installer application tests, add `xur.app-update=off` to the boot command line. The bundled app will run without fetching a newer public app; Bazzite installation still needs the network. See [online installer](../architecture/online-installer.md) for signature checks and fallback behavior.

See [dependency updates](dependencies.md) for Dependabot coverage and the manual
checks for custom source downloads, fonts and model checkpoints.
## Native startup utility

`tools/Xur.Util` builds `xurutil` with Microsoft `System.CommandLine` 2.0.12 and
.NET 10 Native AOT. Publishing requires clang and zlib development headers on the
Linux build host. On Ubuntu, install `clang zlib1g-dev`; the existing Fedora image
builder receives the compiled executable and needs no .NET SDK or runtime for it.

```bash
"${XUR_DOTNET:-$HOME/.local/share/xur-build/dotnet/dotnet}" run --project tests/Xur.Util.Tests -c Release
bash eng/test-xurutil.sh
```

The first command tests service migration, journal setup, signed metadata,
archives, compatibility, activation/recovery, installer preflight, Update All,
virtual display modes and HTTP/stream failures using temporary
fixtures and mocked system commands. It also runs an authentic frozen previous
updater to verify the first upgrade reaches the new agent's startup repair. The
second publishes and exercises the actual native executable, including offline
configuration, with no SDK in its execution environment. Evidence goes under
`.build/evidence/xurutil/`; native output and separate debug symbols stay under
`.build/xurutil/`.

The runtime commands are:

```text
xurutil host migrate
xurutil logs configure [--root /absolute/offline/root]
xurutil installer prepare|verify|check
xurutil installer check-runtime [--root /absolute/offline/root]
xurutil installer sync-clock
xurutil update-all status|run
xurutil display status
xurutil display resize <width> <height> <fps>
xurutil display moonlight
xurutil app-update status|check|update|rollback|recover
xurutil app-update configure <server>
xurutil app-update channel <stable|nightly|local> [server] [-- <public-key-pem>]
xurutil app-update development <true|false>
xurutil app-update compatibility <installed-bundle-id>
```

Installed operations require root. The agent runs host migration before exposing
health and applies journal defaults afterward; logging failures retry on the next
start. Fresh installations keep a root-private recovery copy under
`/var/lib/xur/updater/xurutil`. The first upgrade also migrates the recognized
legacy recovery unit, installs its executable SELinux label, and retains an
independent copy with license notices. Recovery works with the application stopped
or the current bundle link broken. Administrator-customized service units are
preserved.

Installer preparation selects the bundled application without waiting for the
network. `xur.app-update=on` explicitly requests a bounded refresh before startup;
the background timer only reports available updates. Health verification controls
disk approval and falls back to the bundled application when needed.

`src/Xur.IO` is shared by the utility and application services. It provides bounded
command execution, Unix-socket HTTP clients, conservative GET/HEAD retries, and
TeeForge 0.1.0 copy-and-hash streams. Signed downloads verify the receipt from the
same bytes written to staging, avoiding a second archive read. Read retries use
bounded backoff for transient connection errors and HTTP 408/429/500/502/503/504;
they respect short `Retry-After` values and return longer waits to the caller.
Mutations, TLS failures, authentication failures, missing files, size violations
and trust/signature failures are not retried. Interrupted download bodies restart
at most twice in a truncated staging file; cancellation and per-read timeouts
still apply. The model catalog retains its cached-data fallback and enforces an
8 MiB byte limit while streaming. The web app's local status reads and Sunshine
status requests use the same read-only retry policy.

Update All pins the selected bundle for the detached job, stages the OS before
updating Xur, persists each outcome and continues after an OS failure. It never
reboots automatically. Installer clock synchronization and RTC verification run
before Anaconda and disk erasure; image checks verify the merged `/usr/sbin`
symlink and required executables. Virtual display commands run as the station
user, force KDE's Wayland backend, and address only `Virtual-Xur-Stream`. A
versioned executable and license notices are copied into the public station-helper
directory with root ownership and an executable SELinux label. A rejected
Moonlight mode keeps streaming the existing desktop.

The `host/app-update`, `host/host-service-migrate`, `host/log-compression` and
`host/update-all` Python files are small compatibility launchers for older agents
that explicitly invoke Python. Installer compatibility paths are shell launchers;
current production callers invoke the native utility directly. `StationDisplay.py`
is removed. Other agent Python workers, the OS updater and distro Python dependencies
remain separate migration work.
Source size comparison against the previous Python implementations (physical
lines, including comments and blank lines; excludes tests and compatibility
launchers):

| Previous file | Python lines | C# implementation lines |
| --- | ---: | ---: |
| `os/bootc/host-service-migrate` | 124 | 128, plus 89 for independent recovery migration |
| `os/bootc/log-compression` | 51 | 50 |
| `os/bootc/app-update` | 327 | 711 across updater, archive, download and compatibility components |
| `os/installer/app-bootstrap` | 93 | 144 |
| `os/bootc/update-all` | 74 | 92 |
| `src/Xur.Agent/StationDisplay.py` | 74 | 53 |
| `os/installer/check-clock` and `check-runtime` | 50 + 20 | 47 shared in `InstallerPreflight` |

These are source counts, not binary size estimates. Explicit native filesystem
and SQLite interop, command dispatch and durable-file helpers are additional
shared code. `Xur.IO` adds about 202 lines reused by the CLI and services. One
Linux x64 executable contains all commands: the measured Native AOT build is
8,902,176 bytes (8.49 MiB), up 358,048 bytes from the initial startup-only utility.
Debug symbols remain separate and do not ship. Tests run without building an
installer image or deploying the application.
