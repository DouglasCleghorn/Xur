# API keys

Open **Settings → Manage API keys**. Give each client a name, choose access and an expiry (Never or 1–365 days), and copy the key once. The server stores a SHA-256 hash of a cryptographically random secret in an owner-only file under the persistent Xur state directory. The full key is never returned by the list endpoint. Revocation and expiry apply to subsequent requests; already-running operations are not cancelled.

- **Diagnostics:** system/GPU/storage status, system and display reports, diagnostic SSH status, models/profiles, workstation graphics probes and logs, saved benchmark results. Logs and profiles may contain sensitive configuration.
- **Testing:** diagnostics plus inference, model-lab chat, benchmark creation and cancellation.
- **Robotics:** robot status, jobs, camera feedback, reviewed tasks/emotes,
  controller sessions, probe and stop. An operator must arm motion; this scope
  cannot change setup or prepare, record, train, evaluate or review skills.
- **Automation:** other authenticated API operations, including workload/profile changes, workstation streaming/pairing, file downloads, settings, updates and reboot. Keys cannot administer API keys, exchange login credentials, install the OS, change diagnostic SSH or access local-console routes.

Use HTTPS and send the key in `Authorization: Bearer …`. Do not put it in a URL. Use the trusted Tailscale HTTPS name, or explicitly trust the server certificate for LAN connections.

```bash
curl "$XUR_URL/api/workstations" -H "Authorization: Bearer $XUR_API_KEY"
curl "$XUR_URL/api/workloads/10/logs" -H "Authorization: Bearer $XUR_API_KEY"
curl "$XUR_URL/api/workstations/10/graphics" -H "Authorization: Bearer $XUR_API_KEY"
curl "$XUR_URL/api/diagnostics/system" -H "Authorization: Bearer $XUR_API_KEY" -o system-diagnostics.json
curl -X POST "$XUR_URL/api/workstations/10/stream" -H "Authorization: Bearer $XUR_API_KEY"
```

Streaming control requires Automation. Model testing endpoints are `/api/model-lab/targets`, `/api/model-lab/chat`, `/api/benchmarks` and `/inference/{route}/v1/...`.
Read-only system reports need no opt-in setting or SSH access. Review their
redacted journal entries before sharing; see [Diagnostics](diagnostics.md).

Key management endpoints (`GET/POST /api/api-keys`, `POST /api/api-keys/{id}/revoke`) require an existing manager session. Browser changes also require the CSRF token. Creation JSON accepts `name`, `scope` (`diagnostics`, `testing`, `robotics`, `automation`) and `days`. Its response contains `key` metadata and the one-time `token`.

The key page records authorized request count, last method/path and last-used time. It does not save query strings, bodies or secrets in that usage record. Invalid/expired/revoked keys return 401; insufficient scope returns 403, even when a browser cookie or Tailscale identity is present.

Choose **Never** for a key without an expiration date. It remains valid until
revoked and counts toward the 100-active-key limit. Existing keys retain their
expiry. The creation API uses `days: 0` for Never and returns `expiresAt: null`;
omitting days still defaults to 30 days.
