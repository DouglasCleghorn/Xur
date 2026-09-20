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

At boot, before starting the manager/agent/gateway, the live environment tries a
signed GitHub app refresh. The complete attempt is bounded to two minutes. It
requires the normal signature/hash/ABI/schema checks and `online-installer-v1`
compatibility. A release older than the bundled build is rejected. The bundled
application stays on the read-only ISO; refreshed files are in `/run/xur/app`.
The three services are health-checked together; failure restores the bundled app.
Disk approval stays locked until those checks finish. Once installation can be
approved, no background application updater runs. The installer copies the
selected, healthy app bundle into the installed host.

Use `xur.app-update=off` on the kernel command line when testing a locally built
ISO. This only skips the boot-time app download; it does not disable any signature
checks or make OS installation offline. Installed systems expose **Settings →
Local build testing** for signed local application updates. GitHub release assets
are published by `eng/publish-github.py` after a separate package/test step.

The source implementation is covered by digest-pinning, manifest, signed-update,
network-failure and unhealthy-app fallback tests. Actual online Anaconda
installation, live SELinux transitions and Moonlight GPU switching still need
validation on newly built media/hardware; historical offline ISO tests do not
establish these new paths work end-to-end.

References: [Image Builder generic ISO contract](https://osbuild.org/docs/developer-guide/projects/image-builder/advanced/bootc/isos/),
[bootc install configuration](https://bootc.dev/bootc/man/bootc-install-config.5.html),
[GitHub release asset links](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases).
