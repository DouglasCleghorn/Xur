# Xur documentation

Current source review: **2026-10-04**, through commit `0c3346c`, including the
native startup utility, controller input, profile switcher and web Wi-Fi setup.
Xur remains a preview; implemented behavior and hardware validation are separate.

## Start here

| Task | Guide |
| --- | --- |
| Install, sign in and load a profile | [Getting started](usage/getting-started.md), [installation](usage/install.md), [Rufus](usage/rufus.md) |
| Create profiles and recover partial changes | [Profiles](usage/profiles.md), [Home and Monitoring](usage/control-panel.md) |
| Configure desktops, pairing and peripherals | [Workstation identities](usage/workstation-identities.md), [multiple workstations](architecture/multiple-workstations.md) |
| Choose models, connect clients and benchmark | [Model catalog](usage/model-catalog.md), [model lab](usage/model-lab.md), [API keys](usage/api-keys.md) |
| Browse, download and manage files | [Files](usage/files.md), [storage and TRIM](usage/storage.md) |
| Configure networking and time | [Wi-Fi, IP settings and answer YAML](usage/answer-file.md), [timezone and NTP](usage/timezone.md) |
| Update, roll back or test a contributor build | [OS updates](usage/updates.md), [application updates](usage/application-updates.md) |
| Diagnose graphics or streaming | [Diagnostics](usage/diagnostics.md) |
| Installer troubleshooting | [Boot diagnostics and console automation](usage/installer-diagnostics.md) |
| Build and release | [Release preparation](development/release-preparation.md), [local builds](development/build.md), [installer release automation](development/installer-releases.md), [build cleanup](development/build-cleanup.md) |
| Refresh an existing installer USB | [USB updater](usage/update-installer-usb.md) |
| Qualify an installer ISO in disposable VMs | [Automated VM testing](development/vm-testing.md) |
| Plan a hardware test farm | [PXE test server design](development/pxe-test-server.md) |
| Maintain the public site | [Website setup and checks](../website/README.md), [screenshot register](screenshots.md) |

## Implementation and validation status

- Profiles load and unload independent workloads in parallel, with per-workload
  errors, cancellation and resumable partial changes. Tests cover preserving
  successful workloads while a sibling fails.
- Named workstations retain Moonlight pairing across profiles and GPU changes.
  The owner confirmed desktop, input, game rendering and sound on an RTX 3090.
- Qwen MTP inference passed live smoke tests.
  See the [dated retest and its limits](releases/live-model-retest-2026-09-20.md).
- File management includes storage/home tabs, a sortable/filterable grid,
  streamed file and ZIP downloads, rename/move, confirmed deletion and cached
  folder sizes. Directory listings are read afresh.
- API keys have Diagnostics, Testing and Automation scopes, with optional Never
  expiry. Contributor update servers can use their own Ed25519 public key.
- The web manager and native Plasma helper provide a profile switcher with
  keyboard and controller shortcuts. The console supports Xbox One navigation
  and a two-stick keyboard. Target-hardware acceptance remains open.
- Installed Settings provides Wi-Fi scanning and connection controls alongside
  wired IP settings. The installer console also saves connections for the installed host.
- Model engines check the latest upstream image at each start and use a
  compatible local image when a pull fails. Models in the loaded profile recover
  automatically after an exit or reboot; intentionally unloaded models stay stopped.
- Startup migration, app updates, recovery and installer preflight use Native AOT
  `xurutil`; runtime compatibility launchers support upgrades from older bundles.

## Release and validation boundaries

Nightly application candidates are triggered by commits to `main` and publish
automatically after source and application checks pass. Stable candidates from
`release` require maintainer approval in the `stable` GitHub Environment.
Installer builds require manual dispatch with `build_iso`; inspected media
publishes in a separate release after the matching app publication. Stable media
also requires approval. An installer failure does not block the app update.
A green source check alone does not mean an ISO exists or a release was published.
Application releases contain only an app archive and signed descriptor; installer
releases add an ISO. The website selects releases with media. Ext4-root downloads
and public updater bridges were retired; new installs require Btrfs root storage.

The September 21 hosted online ISO build completed, but that historical result
does not validate current media. Remaining acceptance work includes a build and
boot/install/reboot tests for the exact release candidate, simultaneous physical
workstations with USB/audio isolation and hotplug/reboot checks, and broader
GPU/model compatibility testing.
Qwen3-ASR remains unvalidated. GPU allocations remain exclusive: separate desktops
on different outputs of one GPU and shared-GPU workload scheduling are unsupported.

Files in `docs/releases/` and the historical
[implementation snapshot](development/rebuild-status.md) are dated records.
The [implementation and validation checklist](development/remaining-work.md)
tracks open live checks and retains its September 15 backlog separately.
Earlier offline installer results do not validate the current online installer.
See [release preparation](development/release-preparation.md) for documentation
follow-ups, screenshot refreshes and candidate evidence still needed.
