# Online installer and public updates

The ISO contains Fedora's live Anaconda environment and a bundled Xur application.
It does not embed Bazzite. After the operator approves an exact disk, Anaconda/bootc
installs directly from `registry:ghcr.io/ublue-os/bazzite-nvidia-open:stable` using
the approved Kickstart file. Installation and `os-update` use the same stable
channel. Image pulls require the Bazzite signing key through containers/image's
sigstore policy; TLS stays enabled.
The installed update channel is saved in `/etc/xur/upstream.json`.

Fresh disk plans create a FAT32 EFI partition, an ext4 `/boot`, and a Btrfs root
volume using the remaining space. User homes under `/var/home` share that Btrfs
filesystem, enabling automatic Steam game block sharing across users. The disk
review lists Btrfs explicitly before approval. Older ext4 installations are
deprecated and unsupported by Btrfs-required releases; back up and reinstall
with current media. Application updates preserve filesystems and reject
incompatible hosts. See [Steam storage](../usage/steam-storage.md)
for sharing limits and validation.

Before starting Anaconda, the approved installation starts chronyd, requests fresh
time measurements and waits up to 30 seconds for synchronization. It writes UTC to
the hardware clock, when present, and verifies the readback before starting
Anaconda. This prevents Anaconda's initial RTC read from restoring stale time and
breaking TLS during the OS download. Failure stops before disk erasure and appears
in the console; check network access to the configured NTP servers (UDP 123).
These checks run only after disk approval, so they do not delay the setup screen.
The live image includes one SELinux permission allowing chronyd to reply to the
installer's Unix datagram socket. Enforcement remains enabled; without this
reply permission, the H 255 preflight timed out even with chronyd synchronized.

Anaconda/bootc resolves the stable tag when it downloads the OS image. Registry
or network failures can interrupt an approved installation after disk preparation;
the ISO is not an offline recovery image. Registry images are downloaded directly
from GHCR, without an Xur-hosted duplicate.

Normal boot starts the bundled Xur application without waiting for internet or
`network-online.target`. The console and Wi-Fi setup become available
as the local services start. Disk approval waits for local application health
checks, never for the online update check.

The USB boot menu waits ten seconds. A completed Xur installation publishes
`/xur/installed` on its separate boot filesystem; when that marker and its GRUB
configuration are found, the default boots the installed system. Select
**Install or repair Xur** during the countdown to enter setup. Without a completed
installation marker, setup is the default. Older installations without this
marker still require removing the USB or choosing the SSD in the firmware menu.
Both UEFI and BIOS menus use the same configuration and retain the supplied EFI
loaders. Entering setup never bypasses disk review and Yes/No approval.

Installation progress shows a five-stage bar: time synchronization, disk
preparation, download, deployment and configuration. The bar
counts completed stages. During download, the console embeds bootc's native
terminal progress: its layer bar and the current layer's byte bar, size and
transfer rate. Layers vary in size, so their count is not an overall byte
percentage. Only confirmed installation completion fills the stage bar. The
diagnostic status includes the same progress text. Without native progress, the
console shows the reported download size and layer count and tracks stages.
The screen also shows the latest three lines of Anaconda's own command-line
output, including disk preparation, bootloader installation and system
configuration messages. Terminal controls and credentials are removed. Native
tools reporting completion does not mark the whole installation complete;
Xur's successful completion marker remains authoritative.

Anaconda runs bootc through a pipe, which hides bootc's terminal transfer bars.
An installer-scoped bootc wrapper gives stderr a private terminal and saves the
native progress lines, with terminal controls removed, in a bounded, atomic
snapshot at `/run/xur/install-download.json`. Anaconda's command-line output is
also copied to `/run/xur/anaconda-output.log` while remaining in the journal.
Standard output, other stderr messages, command arguments and exit status remain
available to Anaconda. Capture failure leaves stage progress available; it never retries
the installation. The wrapper is included in installer media and selected through
Anaconda's PATH, so an application update alone cannot enable the live counters.

Local agent startup is ordered after NetworkManager startup, so answer-file
networking can be applied, but does not require successful network activation.
The optional wired activation helper runs independently. Its commands have
timeouts; adapter and profile errors are logged without preventing other
adapters or local setup from working. If a saved profile cannot be inspected,
the helper leaves that adapter alone rather than replacing its settings with DHCP.

New media and installations include this service ordering. Application updates
replace the helper on existing installations, but do not replace their copied
systemd units; changing that ordering on an existing installation requires a
separate unit migration.

A separate service checks the signed GitHub release metadata in the background.
Each attempt is bounded to 15 seconds and retries after 60 seconds, including when
a cable is plugged in or Wi-Fi is configured later. Console status shows
checking, unavailable, current or update-available messages. Checks never block
local setup. The live installer listens only on its root-private control socket;
web management and Tailscale are available after reboot into the installed system.

Background checks never download an app bundle, replace the active app, restart
services or change disk approval. When a newer app is available, use Update All
after installation or boot a newer ISO. The installer copies the selected,
healthy app bundle into the installed host. Checks stop once installation has
been approved.

Use `xur.app-update=off` to disable online app checks for a boot. For recovery or
explicit testing, `xur.app-update=on` retains the pre-start signed app refresh,
which can delay startup by up to two minutes. That opt-in path retains signature,
hash, ABI, schema, anti-downgrade and `online-installer-v1` checks, and restores
the bundled app if the download or new app health check fails. Neither option
makes Bazzite installation offline or bypasses disk approval.

The installer image preserves Fedora's `/usr/sbin` symlink to `/usr/bin`. After
copying Xur's files, its build checks that the symlink and required networking,
clock and system executables remain intact. A broken overlay fails the build
instead of shipping services that cannot start.

The source implementation is covered by clock/RTC preflight, executable-path,
stable-channel installation, manifest, signed-update,
network-failure and unhealthy-app fallback tests. Actual online Anaconda
installation, live SELinux transitions and Moonlight GPU switching still need
validation on newly built media/hardware; historical offline ISO tests do not
establish these new paths work end-to-end.

References: [Image Builder generic ISO contract](https://osbuild.org/docs/developer-guide/projects/image-builder/advanced/bootc/isos/),
[bootc install configuration](https://bootc.dev/bootc/man/bootc-install-config.5.html),
[GitHub release asset links](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases).
