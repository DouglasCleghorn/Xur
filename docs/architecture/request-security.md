# Browser requests, compression and explicit exceptions

Xur serves the manager over HTTPS, including its private Tailscale Serve socket.
Plain HTTP only redirects safe navigation; it rejects mutations. The app enables
no CORS origins. Browser requests with an Origin must match the exact scheme,
host and port. Fetch Metadata rejects cross-origin and sibling-site subrequests;
opaque (`null`) origins are rejected. The referrer policy is `same-origin` so local
forms retain their real origin while external sites receive no referrer.

Cookie-authenticated mutations require ASP.NET Core antiforgery validation.
SameSite=Strict, Secure cookies, frame blocking, `form-action 'self'`, and the origin
check are additional protections. They do not replace token validation. Login
forms also require antiforgery. JSON bootstrap/login exchanges require JSON and
pass the same browser-origin check; HTML forms cannot submit those exchanges.

`app-fetch.js` supplies the per-page request token on same-origin requests only.
Authenticated read APIs can use gzip/Brotli over HTTPS only after the server
validates that token and its cookie. Invalid tokens are rejected. No-token reads
remain uncompressed. Conditional polls use weak content ETags and `304` for
unchanged bodies. The browser retains a bounded cache in page memory only, never
localStorage, IndexedDB or the service worker. GPU and network history polls send
`since` and merge only newer samples, resetting on range changes or invalid cursors.
Profile progress polls reload the page only when the operation advances.

Login/setup HTML, credential minting, configuration exports, streaming inference
and downloads are outside dynamic response compression. This avoids compressing
credential-bearing or reflected HTML and does not delay streaming tokens. JSON
responses retain `Cache-Control: no-store`; conditional polling is implemented in
page memory rather than a shared/browser disk cache. Large responses spool to a
temporary file in an owner-only directory during ETag calculation; the spool is deleted when the request completes. TLS protects transport, while the request
token and origin checks restrict access to the compression oracle (BREACH).

Public, immutable-at-publication static assets contain no user data or credentials.
.NET 10 `MapStaticAssets` serves their publish-time gzip/Brotli variants and
content-based ETags, including `304` responses. Static endpoints remain inside the
security middleware; no `ShortCircuit` bypass is used. The network-only service
worker does not retain authenticated pages or API responses.

## Deliberate exceptions

- **Issued API keys and explicit bearer sessions:** nonbrowser clients can make
  authorized `/api/*` or `/inference/*` requests without a browser antiforgery
  cookie. API-key scope, expiry and revocation still apply; keys cannot mint other
  credentials. Supplying a bearer does not bypass a conflicting browser Origin or
  Fetch Metadata header. Bearer-only responses do not opt into compression.
- **Tailscale Serve:** only the dedicated private Unix socket accepts Tailscale
  identity, matched to the confirmed administrator. Public listeners ignore that
  identity header. Tailscale browser mutations still require antiforgery tokens.
- **Local console:** `/local/*` is accessible only through the private local Unix
  socket. It is not exposed through LAN HTTPS or the Serve socket.
- **Top-level safe navigation:** external links may open a GET/HEAD page. They do
  not authorize mutations; protected data still requires authentication. The
  public health/status and login/bootstrap entry points intentionally allow access
  before login, but account creation remains restricted to a valid setup session.
- **Static assets:** fixed CSS, JS, fonts, icons and the app manifest are public
  and precompressed without antiforgery; they contain no runtime secrets.

The settings HF_TOKEN is stored in an owner-only file, never returned by an API,
shown in a form, embedded in a recipe or included in config backups. Direct model
requests attach it only to HTTPS `huggingface.co` on the default port. Redirects
use .NET's authorization-stripping behavior; signed CDN URLs need no bearer.
Model containers receive a read-only token file through `HF_TOKEN_PATH`, not the
secret value in process arguments. Updating/removing it applies to new model
containers after unloading/reloading; existing mounted files remain until stopped.
Downloaded models remain on disk. Logs redact raw Hugging Face tokens.

References: [ASP.NET antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0),
[HTTPS compression](https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression?view=aspnetcore-10.0),
[static asset mapping](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0),
[Hugging Face environment variables](https://huggingface.co/docs/huggingface_hub/main/package_reference/environment_variables).
