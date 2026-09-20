# Manager account

Before an account exists, the console displays a six-character access code. Enter
it on the web login page to reach **Create your account**. Choose a username and
password, then continue to the installer or installed dashboard. This account is
for the Xur web manager; workstation Linux users remain separate selections.

After creation the console shows the username and web addresses without an access
code. The code and earlier bootstrap sessions can no longer authenticate. Future
logins ask for username/password. Settings includes Sign out. Passwords need at
least eight characters, with no character-class rules. Both login methods retain
the existing rate limit.

The account stores a salted ASP.NET Core Identity password hash in
`/var/lib/xur/manager-account.json`, with owner-only permissions. The installer
uses `/run/xur/manager-account.json` and copies it alongside the signing key during
installation. Account sessions use the existing eight-hour signed cookie and
survive application updates and reboots. Corrupt account state fails closed.
Updates refuse older bundles that cannot understand manager accounts.

Automation:

1. `POST /api/bootstrap` with JSON `{ "token": "<console code>" }` returns a
   setup-only bearer token and `setupRequired: true`.
2. `POST /api/auth/setup` with that bearer token and JSON `username`/`password`
   creates the account and returns a manager bearer token.
3. Later, `POST /api/auth/login` with JSON `username`/`password` returns a manager
   token. Token login is disabled after setup.

Bootstrap sessions can only create the account; they cannot access disks,
profiles, or other sensitive APIs. Browser forms require CSRF tokens. The LAN manager uses HTTPS on port 8443 with a persistent machine certificate.
Port 8080 redirects GET requests to HTTPS and rejects plaintext mutations.
Session and form cookies are Secure. Form-protection keys persist alongside the
account, including installation handoff, so a restart does not invalidate an open
login form. Stale forms return to sign-in with a retry message.
Tailscale Serve terminates trusted HTTPS and proxies through its private Unix
socket; it does not need to trust the LAN certificate.

Hashing reference: [ASP.NET Core PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-10.0).
