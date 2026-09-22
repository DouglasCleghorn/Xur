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
installed system generates its signing and form-protection keys on first start. Account sessions use the existing eight-hour signed cookie and
survive application updates and reboots. Corrupt account state fails closed.
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
account, so a restart does not invalidate an open
login form. Stale forms return to sign-in with a retry message.
Tailscale Serve terminates trusted HTTPS and proxies through its private Unix
socket; it does not need to trust the LAN certificate.

Account setup has browser/password-manager hints (`username`, `new-password`,
and length rules) and an accessible eye toggle; Xur does not generate passwords.
If setup fails, its error page includes a request ID and a protected log download.
The same setup session can return to the form to retry. Log access expires with
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
