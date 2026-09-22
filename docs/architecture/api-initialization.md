# Account initialization after installation

Device setup runs locally through **Setup and installation** or `xur setup`.
The live installer has no HTTP/HTTPS listener or Tailscale enrollment.
Web disk-plan, disk-approval and installation-progress endpoints have been removed.

After rebooting into the installed system, open the displayed HTTPS address and
enter the console access code. Create the required administrator account there;
Chrome and other password managers can generate and save its password.

An optional answer YAML can supply wired networking and a predefined access code:

```yaml
schemaVersion: 1
bootstrapToken: A7K-2M9
```

Choose your own code. Use six Crockford Base32 characters, optionally separated
by a middle hyphen. The answer is discovered read-only; ambiguous answers block
installation. It never grants disk approval. See [networking and answers](../usage/answer-file.md).

The optional code is carried into the installed system with root-only permissions.
It expires 30 minutes after manager startup. Creating an account deletes the
staged code and invalidates every setup session. Without an answer-supplied code,
the installed manager generates a fresh code at startup.

For account automation after installation:

1. `POST /api/bootstrap` with JSON `{"token":"<console code>"}` returns a
   setup-only bearer token. It cannot manage the machine.
2. `POST /api/auth/setup` with that bearer and JSON `username`/`password`
   creates the required account and returns a manager bearer token.
3. Use `POST /api/auth/login` with username/password on subsequent logins.

Use HTTPS on port 8443 or the installed Tailscale Serve URL. Browser mutations
require CSRF protection; JSON automation uses explicit bearer headers.
The live installer exposes installation controls only on its root-private
Unix socket, used by the local console. Disk approval always requires a reviewed
plan with the exact target identity and digest.
