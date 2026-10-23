# Unsloth managed browser access

Xur's installed control service listens on the owner-only `unsloth.sock` Unix
socket. Tailscale Serve forwards HTTPS port 8444 there. `BrowserSecurity` trusts
Serve's forwarded host only on its private sockets; `UnslothProxy` then checks
that host against the configured tailnet HTTPS identity and requires a signed
Xur manager browser cookie on every request. The Studio socket branches before
Xur's API routes, API-key middleware and Tailscale administrator shortcut.

This is a shared Studio owner account, not an OIDC identity provider or separate
Studio users. A sign-in made using Xur's LAN address does not transfer its cookie
to the tailnet DNS name; Studio redirects to Xur's tailnet sign-in page. The
browser cookie remains HttpOnly. Only same-origin browser mutations and
WebSocket handshakes are accepted. HTTP streams and WebSockets are cancelled
when their browser session signs out; running training jobs are independent.
This uses Xur's existing signed-cookie lifetime and validation semantics.

Studio's model and dataset pickers call `https://huggingface.co` and
`https://datasets-server.huggingface.co` directly from the browser. The gateway's
`connect-src` policy permits those two HTTPS origins alongside its own origin.
This is optional online API access; scripts, styles and fonts remain local.
Model downloads performed inside the container are independent of this browser
policy. Custom Hub mirrors require an explicit policy update.

`UnslothSession` logs into the `unsloth` owner account and completes the required
first password change. Credentials live at `/var/lib/xur/secrets/unsloth.json`
with owner-only directory/file permissions, covered by Xur's private recovery
backup. A pending password is flushed before the upstream password change;
restart tries both sides of an interrupted rotation. Existing changed accounts
require the current password once through the CSRF-protected connect form.
Server-side refresh-token rotation renews access without browser credentials.

The adapter uses Studio's `/api/auth/login`, `/api/auth/change-password`,
`/api/auth/refresh` and `/api/health` contracts. It intercepts browser auth routes,
including alternative URL encodings, to keep upstream credentials private.
Studio password changes, desktop login and API-key management are unavailable
through this gateway. Those settings are managed by Xur.

A local script initializes Studio's `unsloth_auth_*` storage keys before its
modules execute. The access marker contains only `sub=unsloth` and `role=owner`
display claims, with an invalid signature/header; neither Xur nor Studio accepts
it as a credential. Actual authorization is added to upstream requests by Xur.
The marker's subject and role are required by Studio's owner controls and
profile selection. A 15-second browser session check clears the view after
sign-out, while the server rejects requests immediately.

These conventions were reviewed against upstream source
[`3f11b2e`](https://github.com/unslothai/unsloth/tree/3f11b2eb51afb97a105e00403ea0f345899bf79c/studio)
on 2026-10-22. The image remains `mirror.gcr.io/unsloth/unsloth:latest`; new upstream
authentication or frontend contracts may require adapter updates. Login failures
leave the Open link unavailable, and proxy connection failures return actionable
503 guidance rather than exposing a direct unauthenticated route.

Run `dotnet run --project tests/Xur.Unit.Tests -- --unsloth` for image resolution,
Serve ownership/migration, health, credential recovery, HTTP/HTML/upload,
CSRF/authentication, URL normalization, WebSocket and streaming/logout checks.
The full unit suite includes these checks. Also run the real control-service
`tests/Xur.Integration.Tests/browser-sessions.py` regression with
`XUR_CONTROL_BINARY` pointing to the built control executable.
