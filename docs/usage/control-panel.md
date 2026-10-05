# Home and monitoring

Installed Home is a compact control panel: Update All, confirmed reboot, a pending-reboot notice, and compact saved-profile cards with workload previews. Choosing an item does not mutate the system until Load profile is pressed. Loading uses the existing approval/observation/planner boundary. Active transitions link to Profiles and retain cancellation access.

Storage-device/RAM/VRAM meters, charts, network traffic history, workloads and system services are under `/monitoring`. It is in desktop navigation and the phone's More menu. Installation runs in the local console; web management starts after reboot into the installed system. Unknown device telemetry is never displayed as zero.

**Switch profile** opens a picker with an explicit workload review and Load
profile or Unload all action. **Settings → Profile access** chooses web-only,
server-console or all-workstation access. The web keyboard shortcut is **Ctrl + Alt + P**; a native Plasma
helper provides the same shortcut on workstations. See [Profiles](profiles.md)
for controller controls and desktop-helper setup.

Update All is an authenticated, CSRF-protected POST to `/updates/all` (or bearer-authenticated `/api/update-all`). Its native `xurutil update-all run` command runs in a separate `xur-update-all.service`, so restarting the application services does not stop the sequence. It checks/stages the configured OS image, then checks/applies Xur. Each result is persisted under `/var/lib/xur/update-all/operation.json` and displayed on Updates. It skips an already queued OS deployment and an unconfigured Xur repository. Failures are recorded independently. Re-running checks installed versions before updating; an interrupted sequence is shown as interrupted rather than silently replayed.

Update All does not reboot, change an upstream channel, restart running model
engines, or rewrite saved workload recipes. Model engines check for their latest
image at their next start; selected weights and launch settings stay unchanged.
Conflicting update actions are blocked while the aggregate operation is active.
Home's confirmed reboot remains available except during installation, OS staging
or rollback; see [OS updates](updates.md). Existing OS and application locks also
protect their operations.

Local checks exercise the real Razor pages at desktop/phone widths, profile selection and reboot confirmation, unavailable VRAM, and the update sequence's ordering, skip/failure behavior and interruption reporting. These checks validate the control flow; verify the installed and pending OS
deployments on the target machine before reporting an update as applied.
