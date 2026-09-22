# Online installer and public updates

The ISO contains Fedora's live Anaconda environment and a bundled Xur application.
It does not embed Bazzite. After the operator approves an exact disk, the installer
resolves `ghcr.io/ublue-os/bazzite-nvidia-open:stable` to a digest and hands that
fixed `registry:` reference to Anaconda/bootc. The installed update reference
remains the same stable channel used by `os-update`. Image pulls require the
Bazzite signing key through containers/image's sigstore policy; TLS stays enabled.
The digest/channel receipt is copied into `/etc/xur/upstream.json`.

Resolution fails before Anaconda starts if the registry cannot be reached. A
network failure during the subsequent image download can still interrupt an
approved installation; the ISO is not an offline recovery image. Registry images
are downloaded directly from GHCR, without an Xur-hosted duplicate.

Normal boot starts the bundled Xur application without waiting for internet or
`network-online.target`. The console and Wi-Fi setup become available
as the local services start. Disk approval waits for local application health
checks, never for the online update check.

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

The source implementation is covered by digest-pinning, manifest, signed-update,
network-failure and unhealthy-app fallback tests. Actual online Anaconda
installation, live SELinux transitions and Moonlight GPU switching still need
validation on newly built media/hardware; historical offline ISO tests do not
establish these new paths work end-to-end.

References: [Image Builder generic ISO contract](https://osbuild.org/docs/developer-guide/projects/image-builder/advanced/bootc/isos/),
[bootc install configuration](https://bootc.dev/bootc/man/bootc-install-config.5.html),
[GitHub release asset links](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases).
