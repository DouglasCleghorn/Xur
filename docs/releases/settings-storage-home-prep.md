# Settings, storage and home-screen prep

These source changes have not been packaged or deployed.

- Cloudflare `time.cloudflare.com` is the first-run NTP default. An existing Xur-managed server list or explicit disabled setting is retained; OS/DHCP sources remain fallbacks.
- Automatic timezone uses `https://ipwho.is/` at boot, with bounded retries while the network comes up. It applies only a recognized system timezone, retains the previous zone on failure, and can be refreshed in Settings. Existing manually saved timezones stay manual. IP location can reflect VPN exit location. Workload timezone handling is unchanged: reload running containers/workstations to apply the host zone everywhere.
- Storage shows discard eligibility, scheduled TRIM service/timer information and persistent manual results. Trim targets are re-observed mounted SSD filesystems, never arbitrary device paths. Unsupported filesystems remain visible without a button. TRIM does not remove files or increase filesystem free space.
- Files starts with local mounted filesystems and free space. A bounded allocated-space scan sorts direct children by size; folder drill-down measures that subtree. Links and nested mounts are not traversed, hardlinks are counted once per scan, and incomplete totals are labeled as lower bounds. Workstation file downloads retain their existing user boundary. No deletion feature is added.
- Home contains update/reboot controls, a pending-reboot notice, compact profile/workload choices, and profile transition controls. Resource bars now live in Monitoring. Unload preview is beside Manage profiles.
- Settings downloads a versioned JSON configuration export. It includes profile definitions (which can contain private container environment variables) and selected host preferences. Account/API/pairing credentials, model data, images, build contexts and user files are excluded. Automatic restore is not part of this change.
- Mobile browsers receive a dismissible Add to Home Screen suggestion, manifest and icons. Installation works best through trusted Tailscale HTTPS; browsers may disallow installation over an untrusted self-signed origin. Authenticated responses are never cached offline.
- The terminal status dashboard displays a Tailscale login QR when space permits; the Tailscale QR menu provides a dedicated view in smaller terminals. Before account creation it carries the access code in a URL fragment, removed by the login page. Once configured, the QR points only to `/login`.

Initial GitHub layout and artifact exclusions are proposed in [initial-commit-plan.md](../initial-commit-plan.md); no commit or reorganization has been performed.

Sources: [Cloudflare NTP](https://developers.cloudflare.com/time-services/ntp/), [ipwho.is](https://ipwho.is/), [fstrim](https://man7.org/linux/man-pages/man8/fstrim.8.html), [PWA installation](https://developer.mozilla.org/en-US/docs/Web/Progressive_web_apps/Guides/Making_PWAs_installable). The mount-first Files UI was written locally; no code was copied from the Windows-only [diskusage project](https://github.com/DouglasCleghorn/diskusage).

Validation: the fast suite passed (including the existing authenticated API, HTTPS login, profile transition and terminal checks). After final UI refinements, 445 unit assertions and desktop/mobile Home, Settings, Storage and Files browser checks passed. The new storage scanner tests cover hardlinks, symlink/traversal rejection, folder drill-down and partial scan budgets. No live host settings were changed and no update was packaged.
