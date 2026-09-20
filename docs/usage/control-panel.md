# Home and monitoring

Installed Home is a compact control panel: Update All, confirmed reboot, a pending-reboot notice, and compact saved-profile cards with workload previews. Choosing an item does not mutate the system until Load profile is pressed. Loading uses the existing approval/observation/planner boundary. Active transitions link to Profiles and retain cancellation access.

Storage-device/RAM/VRAM meters, charts, network traffic history, workloads and system services are under `/monitoring`. It is in desktop navigation and the phone's More menu. Installer Home continues to show disk setup. Unknown device telemetry is never displayed as zero.

Update All is an authenticated, CSRF-protected POST to `/updates/all` (or bearer-authenticated `/api/update-all`). Its host helper runs in a separate `xur-update-all.service`, so restarting the application services does not stop the sequence. It checks/stages the configured OS image, then checks/applies Xur. Each result is persisted under `/var/lib/xur/update-all/operation.json` and displayed on Updates. It skips an already queued OS deployment and an unconfigured Xur repository. Failures are recorded independently. Re-running checks installed versions before updating; an interrupted sequence is shown as interrupted rather than silently replayed.

Update All does not reboot, change an upstream channel, upgrade standalone engines, or rewrite saved workload recipes. Engine versions remain pinned by each workload. Manual update actions and reboot are blocked while the aggregate operation is active. Existing OS and application locks also protect their operations.

Local checks exercise the real Razor pages at desktop/phone widths, profile selection and reboot confirmation, unavailable VRAM, and the update sequence's ordering, skip/failure behavior and interruption reporting. These prep changes have not been published or used to stage a real OS update.
