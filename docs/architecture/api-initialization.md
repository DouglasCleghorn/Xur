# Automated initialization

Use `https://<machine>:8443` or the Tailscale HTTPS URL. Trust the machine certificate
for automation; plaintext POST requests are rejected.

The JSON API uses the same reusable six-character setup token as the browser.
On private test networks, put this token-only answer file at the root of a
separate configuration disk as `xur.yaml`:

```yaml
schemaVersion: 1
bootstrapToken: A7K-2M9
```

Choose your own token. Characters use Crockford Base32: `0123456789ABCDEFGHJKMNPQRSTVWXYZ`.
Login is case-insensitive and accepts the code with or without its middle hyphen. A random local code works immediately while the read-only scanner runs. Once discovery completes, the configured token replaces it; existing browser/API sessions remain valid. The default 30-minute expiry and five attempts per
30 seconds also apply to configured tokens. The initial setup token is reset
on reboot. Signed API sessions survive installation and reboot until their eight-hour expiry. The token file is read only by the live installer and is not copied
to the installed system.

The scanner checks all eligible storage read-only. Multiple answers are
ambiguous; none is selected. The supported token-only file enables explicit
browser/API disk review, while its parent disk remains protected. Unknown
provisioning fields are not treated as installation approval.

1. `POST /api/bootstrap` with `Content-Type: application/json` and
   `{"token":"A7K2M9"}`. A successful response contains `accessToken`,
   `tokenType: "Bearer"`, and `expiresIn: 28800`. The configured token may not be active until answer discovery finishes; retry
   after 30 seconds during startup. HTTP 401 rejects invalid/expired tokens;
   HTTP 429 asks the client to wait 30 seconds.
2. Create the manager account with `POST /api/auth/setup`, JSON username/password,
   and the setup bearer token. Use the returned manager `accessToken` as
   `Authorization: Bearer <accessToken>` for subsequent API requests.
   `GET /api/disks` returns real whole-disk inventory. `GET /api/installer`
   returns discovery and operation state; neither returns the setup token.
3. `POST /api/install/plan` with `{"path":"<observed whole-disk path>"}`.
   Review the returned target identity, unaffected disks, actions, expiry,
   plan `id`, and `digest`.
4. `POST /api/install/approve` with the reviewed `id` and `digest`.
   No serial confirmation field is required.
   This uses the same privileged approval and revalidation operation as the UI.
5. Poll `GET /api/installer` and `GET /api/logs`. After completion,
   `POST /api/power/reboot` with `{}` reboots into the installed deployment.

API mutations authenticate with an explicit bearer header. Browser cookie
mutations retain their CSRF checks. No endpoint accepts an unattended erase
instruction from the answer file.

Run the complete API-only installation test with:

```bash
python3 tests/Xur.Media.Tests/check-api-initialization.py \
  dist/xur-installer-x86_64.iso --name api-test-1
```

Use a new VM name per test. This creates a private random token and file-backed
configuration disk, initializes through JSON without reading the live console
secret, approves only the disposable target without serial entry, installs, and reboots with media
attached. It verifies both configuration and data disk hashes and that the bearer session remains valid. The private
token, disk contents, raw console, and sessions remain under `.build/`.
