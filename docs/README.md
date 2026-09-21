# Xur documentation

Current source review: **2026-09-20**, including file management, Fish speech
runtime preparation, and the installer CI readiness fix. Xur remains a preview;
implemented behavior and hardware validation are separate.

## Start here

| Task | Guide |
| --- | --- |
| Install, sign in and load a profile | [Getting started](usage/getting-started.md), [installation](usage/install.md), [Rufus](usage/rufus.md) |
| Create profiles and recover partial changes | [Profiles](usage/profiles.md), [Home and Monitoring](usage/control-panel.md) |
| Configure desktops, pairing and peripherals | [Workstation identities](usage/workstation-identities.md), [multiple workstations](architecture/multiple-workstations.md) |
| Choose models, connect clients and benchmark | [Model catalog](usage/model-catalog.md), [model lab](usage/model-lab.md), [API keys](usage/api-keys.md) |
| Browse, download and manage files | [Files](usage/files.md), [storage and TRIM](usage/storage.md) |
| Configure networking and time | [Network and answer YAML](usage/answer-file.md), [timezone and NTP](usage/timezone.md) |
| Update, roll back or test a contributor build | [OS updates](usage/updates.md), [application updates](usage/application-updates.md) |
| Diagnose graphics or streaming | [Diagnostics](usage/diagnostics.md) |
| Build and release | [Local builds](development/build.md), [installer release automation](development/installer-releases.md), [build cleanup](development/build-cleanup.md) |
| Maintain the public site | [Website setup and checks](../website/README.md), [screenshot register](screenshots.md) |

## Implemented and exercised

- Profiles load and unload independent workloads in parallel, with per-workload
  errors, cancellation and resumable partial changes. Tests cover preserving
  successful workloads while a sibling fails.
- Named workstations retain Moonlight pairing across profiles and GPU changes.
  The owner confirmed desktop, input, game rendering and sound on an RTX 3090.
- Qwen MTP inference and Fish S2 Pro speech synthesis passed live smoke tests.
  Fish now prepares and caches its pinned codec dependency image automatically.
  See the [dated retest and its limits](releases/live-model-retest-2026-09-20.md).
- File management includes storage/home tabs, a sortable/filterable grid,
  streamed file and ZIP downloads, rename/move, confirmed deletion and cached
  folder sizes. Directory listings are read afresh.
- API keys have Diagnostics, Testing and Automation scopes, with optional Never
  expiry. Contributor update servers can use their own Ed25519 public key.

## Release and validation boundaries

Nightly candidates are triggered by commits to `main`; Stable candidates by
commits to `release`. Both require GitHub Environment approval before publication.
App and installer candidates must pass before that approval stage is reached.
A green source check alone does not mean an ISO exists or a release was published.
Older published app releases may contain no installer media. Check the assets of
an actual release; the website download page handles missing or multipart media.

Remaining acceptance work includes a completed hosted online ISO build and its
boot/install/reboot tests, simultaneous physical workstations with USB/audio
isolation and hotplug/reboot checks, and broader GPU/model compatibility testing.
The Fish test did not cover voice cloning, streamed audio or concurrent speech;
it did not validate Qwen3-ASR. GPU allocations remain exclusive: separate desktops
on different outputs of one GPU and shared-GPU workload scheduling are unsupported.

Files in `docs/releases/` are dated records. The historical
[implementation snapshot](development/rebuild-status.md) and
[September 15 backlog](development/remaining-work.md) are retained for context,
not current feature lists. Earlier offline installer results do not validate the
current online installer. Review the receipts for the exact candidate being shipped.
