# Release preparation

Documentation reviewed on **2026-10-04** against source commit `0c3346c` and the
release workflows in this checkout. This review updates source guidance; it does
not certify a release candidate or hardware support. Xur remains a preview.

## Documentation corrections

| Area | Updated guidance |
| --- | --- |
| Release channels | Nightly app publication is automatic after checks; Stable requires maintainer approval. ISO builds require manual `build_iso` dispatch and publish separately. App releases have two assets; installer releases add one ISO. |
| Installation and downloads | Disk selection and erase approval happen in the console; account creation happens in the browser after reboot. New root storage is Btrfs. The website no longer offers the retired September 21 ISO as a fallback. |
| Networking | Settings now links to **Manage Wi-Fi and IP settings**, with Wi-Fi scanning and connection controls. Wi-Fi changes and wired IP changes have different confirmation behavior. Answer YAML remains wired-only. |
| Profiles and models | Guides cover the keyboard/controller switcher, rolling engine refresh, cached-image fallback and automatic recovery of models in the loaded profile. Model revisions and launch settings remain selected. |
| Workstations | Guides use the current creation, Settings and Moonlight pairing controls. Physical input rules assign the event node and its input parent to the same seat. The named-timezone fix for Steam Big Picture is documented in the timezone guide. |
| Native maintenance | Application recovery and startup migration use `xurutil`; recognized legacy agent units are repaired on the first successful upgrade. Custom units retain their settings. |
| Security and input boundaries | Opt-in installer diagnostics is a separate HTTPS listener. Headless desktops use dedicated PAM sessions; Sunshine has a separate input-device grant. Recovery ZIPs include Hugging Face credentials, while lightweight configuration JSON excludes them. |
| Benchmarks | Model Lab records the saved recipe's engine reference. It does not record the actual image digest resolved at startup; comparisons need that identity collected separately. |
| Historical plans | The initial-commit plan is explicitly archived. The documentation index distinguishes dated release records from the current implementation/live-validation checklist. |

The documentation index, main README, relevant usage/architecture pages and
matching public website guides were aligned together. Dated release notes retain
their original versions and test boundaries.

Documentation checks passed: the static website build, responsive/local-link
tests at 1440/768/390/320 px, download tests (including missing media, API failure,
disabled JavaScript and clearing stale links), automated WCAG 2.2 AA checks on
nine pages at desktop/phone widths, local Markdown links/anchors and the offline
web-asset policy. Generated site captures and test output stay under ignored
`.build/`; these checks do not execute installer or workload acceptance tests.

## Before freezing the candidate

- [ ] Choose the application channel and final source commit. Let
  `eng/release-version.py` select the public version; example versions in guides
  are not reservations or evidence of publication.
- [ ] Write candidate release notes with the actual version, source commit,
  changes since the preceding release, compatibility limits and test evidence.
  Highlight the Btrfs requirement for older ext4-root installations and the
  removal of model-specific Fish runtime preparation. Do not reuse old GPU/model
  results as acceptance of refreshed engines.
- [ ] Refresh `profile-editor.jpg` and `workstations.jpg` from synthetic fixtures,
  review privacy and metadata, and update their capture version/date in
  [the screenshot register](../screenshots.md). Both predate changed controls.
  Review `control-panel.jpg` and `model-lab.jpg` for shared navigation changes;
  check the cropped `update-channel.jpg` against the current selector.
- [ ] Run the candidate's source/application checks and review its receipts.
  This documentation pass runs site and document checks only.
- [ ] Review manual dependencies in [Dependency updates](dependencies.md).
  Verify the actual bundle retains Xur's license, `licensing.md`, restored
  browser-library notices, native runtime notices and the website font's OFL.
  Hosted native builders now retain RPM documentation for license collection.

## Acceptance evidence still needed

Use [the live-validation checklist](remaining-work.md) for detailed target-host
checks and [multiple workstations](../architecture/multiple-workstations.md)
for isolation limits. Record results against the exact candidate; leave any
unexecuted checks visible in the release notes.

| Area | Candidate checks |
| --- | --- |
| Existing installation | First upgrade from the preceding updater, service/recovery migration, login continuity, workload continuity, rollback and interrupted-activation recovery. |
| Workstation input and streaming | Physical event/input-parent seat assignment, two users/GPUs, USB/hub audio isolation, hotplug, stopping one desktop while another continues, reboot and Moonlight reconnect. Retest AMD startup and NVIDIA encoder selection. |
| Keyboard and controllers | Console navigation and two-stick entry, secret masking, idle/wake, native Plasma profile shortcut, fullscreen focus, controller grabs and Moonlight-delivered input. |
| Networking and onboarding | Real Wi-Fi association and saved-profile reboot persistence, web connection changes, wired IP keep/revert, answer YAML persistence, and account creation/password-manager behavior in Safari and Chrome. |
| Models and storage | Latest-engine startup, compatible cached fallback, recovery after exit/reboot, intentional unload, refreshed endpoints, reference Qwen workload and current compatibility limits. Check Steam block sharing and independently writable user files on Btrfs. |
| Updates | Update All success/partial failure, staged OS reporting and explicit reboot. App and OS channels remain separate. |

The September 21 hosted ISO result establishes an older build and embedded
inspection only. If this release includes new installer media, request a fresh
ISO and run boot/install/reboot tests on that exact artifact, including protected
configuration media, offline/late networking, progress/failure, manual retry,
diagnostic SSH opt-in, USB boot selection and settings persistence. ISO inspection
alone does not establish those outcomes. The [automated VM suite](vm-testing.md)
covers unchanged-ISO boot, permissions, answer ambiguity, erase cancellation and
online install/reboot persistence; installer publication requires its full run.
Its receipt lists the hardware, upgrade and network-boot checks still absent.
Keep evidence in ignored
`.build/evidence/`; packaging and publication follow the user's release instruction.

## Publication-time documentation checks

- [ ] Verify the actual installer release assets and signed descriptor, SHA-256
  and size. The app channel pointer and detached installer release must describe
  their own matching artifacts. Current publication rejects ISOs of 2 GiB or more.
- [ ] Check website discovery against the published release, including app-only
  releases newer than the installer. Without JavaScript or on API failure, users
  should reach GitHub Releases with no retired ISO offered.
- [ ] Check external download/support links and provider setup instructions.
  Local link checks do not verify GitHub asset availability, Cloudflare's current
  interface or other upstream sites.
- [ ] Update the documentation source-review date/commit if the final candidate
  changes behavior, and record any new or retired screenshots before publication.
