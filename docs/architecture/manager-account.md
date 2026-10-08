# Manager account

After installation, before an account exists, the console displays a six-character
access code. Enter
it on the web login page to reach **Create your account**. Choose a username and
password, then continue to the installed dashboard. Account creation is required;
the token grants access only to this step. Chrome can offer a generated password
through the standard username and new-password fields. This account is
for the Xur web manager; workstation Linux users remain separate selections.

After creation the console shows the username and web addresses without an access
code. The code and earlier bootstrap sessions can no longer authenticate. Future
logins ask for username/password. Settings includes Sign out. Passwords need at
least eight characters, with no character-class rules. Both login methods retain
the existing rate limit.

The account stores a salted ASP.NET Core Identity password hash in
`/var/lib/xur/manager-account.json`, with owner-only permissions. The live installer has no web listener or administrator-account form. The
installed system generates its signing and form-protection keys on first start.
Browser account sessions are signed with the distinct `manager-browser` purpose
and have no server expiry. Their Secure, HttpOnly, SameSite=Lax cookie lasts
400 days and is renewed on authenticated visits, preserving sign-in across browser
restarts, application updates and reboots. An eight-hour cookie from an older
bundle upgrades on its next visit, including when its old expiry or not-before
claims would reject it as a bearer. Browser cookies validate the machine's
signature, issuer, audience, account and purpose without a clock limit. API
bearer validation remains separate and enforces its lifetime.
Lax allows opening Xur from external links without losing sign-in. A restored
login page checks the existing session through a same-origin request and returns
to the dashboard automatically; this also recovers older Strict cookies withheld
on the initial navigation. Antiforgery cookies remain Strict and mutations still
require their request token.
Browsers can still remove cookies, including after prolonged inactivity or when
site data is cleared. Sign out removes this browser's cookie. Setup and API bearer
sessions retain their eight-hour expiry. Corrupt account state fails closed.
Updates refuse older bundles that cannot understand manager accounts.

Automation:

1. `POST /api/bootstrap` with JSON `{ "token": "<console code>" }` returns a
   setup-only bearer token and `setupRequired: true`.
2. `POST /api/auth/setup` with that bearer token and JSON `username`/`password`
   creates the account and returns a manager bearer token.
3. Later, `POST /api/auth/login` with JSON `username`/`password` returns a manager
   token. Token login is disabled after setup.

Bootstrap sessions can create the account and download redacted setup logs at
`GET /setup-account/logs`; they cannot access disks,
profiles, or other sensitive APIs. Browser forms require CSRF tokens. The LAN manager uses HTTPS on port 8443 with a persistent machine certificate.
Port 8080 redirects GET requests to HTTPS and rejects plaintext mutations.
Session and form cookies are Secure. Form-protection keys persist alongside the
account, so a restart does not invalidate an open login form. A restored page's
antiforgery cookie or identity can still change. Before submission, `login.js`
refreshes its request token from the same-origin, uncached `GET /auth/login-token`
endpoint. Where `PasswordCredential` is supported, it submits the form with JSON
response negotiation and asks `navigator.credentials.store()` to save the password
only after successful authentication. Xur keeps credentials in page memory,
never in URLs, localStorage or sessionStorage; persistence belongs to the browser's
password manager. Save failures do not block login. Other
browsers submit the native form for password-manager detection. Both paths retain
server antiforgery validation and preserve input on token-refresh failures. Without
JavaScript, stale forms return to sign-in with a retry message.
Tailscale Serve terminates trusted HTTPS and proxies through its private Unix
socket; it does not need to trust the LAN certificate.

Account setup has browser/password-manager hints (`username`, `new-password`,
and length rules) and an accessible eye toggle; Xur does not generate passwords.
Password saving is controlled by the browser. Certificate errors can prevent
password-manager prompts; bypassing a LAN certificate warning does not establish
certificate trust. Use the trusted Tailscale HTTPS address or explicitly trust
the machine certificate. Both forms explain password-manager settings and trust.
With a trusted certificate, a missing prompt still depends on browser settings,
site exclusions and detection. The application cannot force a native save prompt.
If setup fails, its error page includes a request ID and a protected log download.
If the account has not been published, the same setup session can return to the
form to retry. Once the account file is published, bootstrap access is disabled
even if syncing its directory fails. Sign in with the saved username and password
to retry the sync; a manager session is issued only after it succeeds. Loading a
saved account also requires a successful directory sync and fails closed otherwise.
Log access expires with
the setup session and is revoked when the account is created; authenticated
managers can then use Diagnostics. No anonymous log access is provided.

The web manager, agent and gateway send redacted warnings and exceptions to
stderr, which their systemd units send to the journal. Request bodies, passwords,
cookies and authorization headers are not intentionally logged. The current
manager's bounded in-memory log is included even if the agent/journal is
unavailable. The console Logs view and web Diagnostics include Xur service logs;
the downloaded diagnostic JSON includes application logs as well as GPU details.
The in-memory fallback lasts only until the process restarts; journal retention
controls older records. These changes cannot recover errors that older builds
discarded before logging was enabled.

Hashing reference: [ASP.NET Core PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-10.0).
