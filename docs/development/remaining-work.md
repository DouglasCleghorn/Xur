# Implementation and validation checklist

Updated October 4, 2026 against pushed commit `c9ef907` and the reviewed
follow-up changes recorded below. Checked items below
mean source work and the stated local checks are complete, not that the H 255
has received or validated the change. The older planning snapshot is retained
below separately.

## Reviewed and pushed

- [x] Keep local control available when optional network activation fails;
  preserve saved profiles and bound NetworkManager calls (`1e0395f`).
- [x] Deliver installed service dependency repair on the first successful app
  upgrade, including from the old updater (`2569b9f`). Completed migrations skip
  systemd work; failure and compatibility fixtures pass. An agent already blocked
  from starting still needs local recovery.
- [x] Recover a published account after directory-sync failure without issuing
  a password session before durability succeeds (`2569b9f`). The complete unit
  suite passed 943 checks. This edge was not established as the original account
  creation error.
- [x] Isolate post-account-creation cleanup/console-refresh errors from successful
  account creation (`4fcdbf4`); original reported error remains unconfirmed.
- [x] Tolerate absent workstation runtime files while preserving genuine cleanup
  errors (`4cff271`); retain details for failed stop requests (`8cad05a`).
- [x] Use native option buttons and explicit labels for dropdowns (`4cff271`),
  expose Add user directly (`8cad05a`), and prevent the open User popup from
  covering that button (`2569b9f`). Chromium mouse/touch checks passed at 1440,
  900, 390 and 320 px; shared-browser check passed at 320 px. Actual Safari is open.
- [x] Implement stage-based installation progress, timed installed-OS USB boot
  selection, live console address refresh and enrollment return home (`2079775`).
  Physical acceptance is still required below.

## Sol 6.1 follow-up — reviewed and validated

- [x] **S1 — Workstation teardown/recovery:** regression coverage now exercises
  missing runtime directories, cleanup failures, retained errors/receipts and
  persisted-journal resume. Fixed failed device lookup being treated as absence:
  uncertain results retain the cleanup receipt and block replacement; confirmed
  missing/replaced devices still permit cleanup. Full host-bound teardown is open.
  Owner: `sol61_account_recovery`; worktree `.build/sol61-station-recovery`.
- [x] **S2 — Network persistence:** fixed superseded-profile cleanup errors
  invalidating an already-saved Wi-Fi/static profile. Activation and new-profile
  save failures still fail normally. Tests cover console/YAML input, generated
  keyfiles, the actual installer copy block, permissions and autoconnect settings
  using fake networking. Actual Wi-Fi association/reboot is open; disk approval stays.
  Owner: `sol61_network_review`; worktree `.build/sol61-network-persistence`.
- [x] **S3 — Console behavior:** added actual-frame and shared input-gate checks
  for idle/wake, background updates, Logs/Back and address/QR replacement. Native
  client tests cover clearing all rows, polling without repaint while idle and
  showing the newest frame on wake. No console behavior defect was reproduced;
  the extracted input gate preserves existing behavior. Physical display checks
  remain open.
  Owner: `sol61_browser_review`; worktree `.build/sol61-console-acceptance`.

These source tasks passed parent and independent review, 1,098 combined unit
checks after integrating the concurrent monitor-sleep/reboot and storage changes,
network-startup integration and native console transport/PTY and monitor-power checks.
Tests use isolated fixtures, not live host changes;
none closes a live acceptance item below.

## October 4 validation

- [x] Independently tested `c9ef907`: 1,553 unit checks, 143 profile checks across
  20 recovery cycles, and 437 managed utility checks passed. Account HTTP/restart
  checks preserved manager sessions, password login, revoked keys and closed
  bootstrap access. These were local isolated tests, not live lifecycle tests.
- [ ] Saved-profile identity fix: the October 3 test pass on `f8d27c0` reproduced
  UUID-looking connection names affecting boot profile selection. A path-based
  fix and adversarial regression cases are under PR review; deployment remains
  pending.

## Live verification still open

- [ ] **V1 — Update H 255:** install the reviewed build, confirm its identity and
  management health, and verify the installed service dependency migration.
  Read-only authenticated observation on October 4 found installed and available
  versions both `nightly26.10.013`, with the same bundle ID and a completed app
  update. This establishes the deployed version, not physical acceptance or
  successful migration of the installed service unit. The profile-identity fix
  under review is not part of that release.
- [ ] **V2 — Workstation recovery:** stop/start Workstation 1 and switch profiles;
  verify devices are released, ownership checks remain effective and failures
  contain useful details. October 4 read-only snapshots showed a running
  workstation with Sunshine ready, then no running workloads, with completed
  profile operations in both snapshots. Another actor appeared to be changing
  state; this agent did not initiate a lifecycle test or verify physical release.
- [ ] **V3 — Safari dropdowns/Add user:** select workload, workstation, user and
  GPU; create/select/save a user and cancel without losing an unsaved profile.
- [ ] **V4 — Account onboarding:** verify immediate management after creation;
  capture diagnostics if the original error recurs. Check generated-password,
  save and sign-in behavior in actual Safari and Chrome over HTTPS.
- [ ] **V5 — H 255 HDMI startup:** cold boot with HDMI untouched, collect early
  display diagnostics and confirm the setup screen appears. Cable replugging
  during earlier tests prevents attributing recovery to the source fix.
- [ ] **V6 — 4K usability:** check font size and input/rendering latency on H 255.
- [ ] **V7 — Address/QR refresh:** unplug/reconnect Ethernet and confirm displayed
  addresses and QR update without menu navigation.
- [ ] **V8 — Tailscale console:** verify completed enrollment returns home, QR
  stays in bounds and wording is clear. HTTPS access worked after the user's ACL
  change; repeat after deployment. Tailscale remains post-install only.
- [ ] **V9 — Wi-Fi:** authenticate the RTL8852BE to a real network, install with
  the saved profile and verify reboot reconnection. Scan previously found five
  SSIDs; that does not verify association or persistence.
- [ ] **V10 — Setup flow:** name → network → disk → review/Yes-No erase approval
  → install, with working Back navigation. Server name persists; administrator
  creation remains required in post-install web setup.
- [ ] **V11 — Answer YAML:** apply static networking, verify it persists on the
  installed system, and confirm disk erasure still requires explicit approval.
- [ ] **V12 — Offline/late network:** confirm console appears promptly offline
  and reacts when networking arrives; installation explains download needs.
- [ ] **V13 — Install progress/failure:** observe stages on a real installation;
  preserve the no-automatic-retry behavior after failure.
- [ ] **V14 — USB boot default:** with an installed-system completion marker,
  verify timed default boots the SSD and manual installer selection works.
  The older H 255 install predates the marker; do not assume it has one.
- [ ] **V15 — Logs:** use full available screen, escape/back reliably, export
  manually and verify automatic USB export on installation failure.
- [ ] **V16 — OLED protection:** verify monitor sleep/black after ten idle
  minutes and that the first key only wakes the screen, including during
  background updates. The ten-minute policy arrived in the concurrent monitor
  sleep change and is preserved by the follow-up tests.
- [ ] **V17 — Update all:** exercise success, partial failure and reboot-required
  reporting on a real installed system.

Suggested live order: V1 → V2 → V3/V4, then V5/V9. Installation and failure
checks need a disposable target or a separately reviewed test plan; do not erase
the working H 255 just to clear this checklist.

## Historical backlog — September 15, 2026

For current features and validation boundaries, use the [documentation index](../README.md).

This is an archived planning snapshot, not the current implementation checklist.
Later work added multiple workstation runtime support, model benchmarking, engine
version display, HF credentials and tested Tailscale/Moonlight access. USB selection, native multi-seat integration and parallel profile loading are now
implemented in source. Their physical acceptance checks remain outstanding; see
[multiple workstations](../architecture/multiple-workstations.md). Separate
outputs on one GPU and shared-GPU AI scheduling remain future work.

For the current identity/update design and its remaining hardware validation, see
[workstation identities](../usage/workstation-identities.md) and
[online installer](../architecture/online-installer.md).

Reviewed against source on 2026-09-15. This distinguishes missing product behavior
from behavior implemented but not yet exercised on the four-3090 machine.

## Still to implement

- Independent desktops on different outputs of one GPU. A whole selected card
  and its connected outputs currently belong to one desktop.
- The complete reference AI set: Qwen INT8 W8A16 with BF16 MTP on the NVLink pair,
  and Qwen3-ASR through core vLLM on the other pair.
  Discovering a model in search is not a model-specific deployment recipe.
- NVLink-aware automatic allocation, speech co-residency and measured shared-GPU
  scheduling. NVLink discovery and selection hints are implemented. GPU allocations
  are currently exclusive, so capacity-sharing behavior is absent.
- Per-model vLLM tuning and Omni stage configurations from reviewed upstream
  recipes; driver/architecture compatibility checks beyond file format and the
  current engine/device checks; gated-model credentials and license acceptance.
- Display installed and running engine image identities more clearly. Model
  starts now refresh the latest upstream channel automatically, including vLLM
  and vLLM-Omni; failed pulls use the newest compatible local image. Rollback
  controls remain future work.
- vLLM ROCm and Intel XPU engine choices. llama.cpp has CPU/CUDA/ROCm/Vulkan image
  choices; vLLM and Omni currently choose NVIDIA images.
- A managed benchmark recipe with measured GPU baseline return. Sunshine is now
  tied to workstation start/stop; headless capture on the physical GPUs remains
  to be exercised.
- Container volume deletion/export, richer build contexts, generic WebSockets
  and a prepared Breeze TTS 2 recipe. Podman image pulls, text Dockerfile builds,
  persistent volumes and storage accounting are already implemented.
- OS reboot scheduling and automatic failed-boot recovery. Automatic OS staging,
  explicit reboot and explicit rollback are already implemented.

## Implemented, with physical end-to-end checks remaining

- Selected GPU restriction: KWin receives one DRM card and the user's device
  policy restricts GPU access. Direct rendering/scanout on each physical 3090,
  HDMI audio and absence of cross-GPU frame copies still need observation.
- GPU monitoring has real NVIDIA and sysfs readers and history graphs, but the
  four-card physical run has not taken place.
- Model/workstation profile continuity passes installed VM tests with real
  llama.cpp, Podman and Plasma. The owner's complete GPU workload set has not run.
- Tailscale QR and browser authorization startup work; completed owner enrollment
  and access through its tailnet URL have not been tested here.

## Persistent USB recommendation

Represent each physical desk as a persistent assignment, separate from its Linux
user account and from transient device numbers. A profile's workstation selects
that desk plus a user (or temporary user). The user's home retains Steam logins;
the desk retains its display and peripheral assignment.

Default to assigning a USB hub or physical port subtree. Match controller PCI
identity plus USB port chain, not bus/device numbers or `/dev/input/eventN`.
Optionally match an individual device by vendor/product plus a unique serial so
it can follow that device between ports. Ambiguous or missing devices remain
unassigned rather than selecting a similar device.

Use udev/logind seat assignment with explicit per-user device access and hotplug
reconciliation. Include all interfaces of composite devices and hub descendants;
handle USB audio, controllers/hidraw, cameras and removable storage permissions
explicitly. Seat labels alone do not isolate every USB device class. Prevent two
active desks from owning the same peripheral, and preserve assignments while a
station is stopped. A temporary user's deletion must not delete the desk mapping.

Systemd supplies persistent seat attachment through
[loginctl attach](https://github.com/systemd/systemd/blob/main/man/loginctl.xml).
The implemented selection UI and runtime are described in the multi-workstation
architecture document linked above; physical acceptance is still outstanding.

## Added in application update 2026.09.15.3

Unassigned displays now mirror the terminal console, with selected-card handoff
and return after desktop teardown. Per-GPU power controls persist through reboot
where the driver exposes supported limits. See `display-consoles.md` and
`gpu-power.md` for behavior and test coverage.

## Current workstation and model update

See `workstations-and-models.md` for encrypted streaming, disconnected display
selection, model inventory and persistent caches, network charts, NVLink hints,
email usernames and GPU ownership diagnostics.
