# xur.app

The public Xur project site and short guides. Plain HTML, CSS and a small optional
profile-diagram and release-download scripts; Python's standard library assembles shared navigation and
copies the existing brand assets. No npm install, external fonts, tracking,
server-side Functions or Worker code, database, or paid storage service. Xur’s
original site code and content use the root MIT license; the font retains its OFL
notice. The footer links to the repository’s [MIT license](https://github.com/DouglasCleghorn/Xur/blob/main/LICENSE).
The build also includes `/license.txt` in the static output.

## Local preview

From the repository root:

```sh
python3 website/build.py
python3 -m http.server 8090 --directory website/dist
```

Open http://localhost:8090. `website/dist/` is generated and ignored. Never serve
the repository root. Source content is in `website/pages/`; common layout and
styles are in `template.html` and `assets/`. Brand assets are copied from the
application and documentation at build time rather than duplicated in Git.

## Cloudflare Workers Static Assets

Use **Workers & Pages → Create application → Continue with GitHub**, then select
`DouglasCleghorn/Xur`. Cloudflare recommends Workers for new projects. This site
uses static assets only, with no Worker script.

| Setting | Value |
| --- | --- |
| Project name | `xur` |
| Build command | `python3 website/build.py` |
| Deploy command | `npx wrangler deploy --config website/wrangler.json` |
| Advanced path | `/` (repository root) |
| Non-production branch builds | Off initially |
| Protect with Cloudflare Access | Off for the public site |
| API token | Create new token (Cloudflare manages it) |
| API token name | `xur-website-builds` |

Paste commands without surrounding quotes or Markdown backticks. The checked-in
Wrangler configuration pins the compatibility date and uploads only `website/dist`.
Its asset directory is relative to `website/wrangler.json`.

Workers initially uses the repository's default branch. After creating the project,
verify `main` under **Settings → Build → Branch control**. Under **Build watch paths**,
replace the default `*` include with `website/*`, `docs/assets/*`,
`src/Xur.Control/wwwroot/icons/*`, `src/Xur.Control/wwwroot/fonts/*`, and `LICENSE`.
Leave excludes empty. This skips ordinary app-only pushes; Cloudflare documents
exceptions for empty pushes and pushes containing many commits or files.

Add `xur.app` under **Settings → Domains & Routes → Add → Custom Domain** after
deployment. If also using `www.xur.app`, configure a zone-level Redirect Rule that
matches only that hostname and redirects to `https://xur.app`, preserving the path
and query string. Ensure the source hostname has a proxied DNS record. Do not add
an absolute source URL to `_redirects`: Workers rejects it. A relative `/*` rule
pointing at `https://xur.app/:splat` would also match the apex and cause a loop.

Do not store Cloudflare API tokens in the repository. Keep application and installer
artifacts on GitHub Releases. Website deployment is separate from the app release
approval gates; `build.py` only generates files and never deploys them.

For a local configuration check after building:

```sh
npx wrangler deploy --config website/wrangler.json --dry-run
```

This checks the deployment configuration without publishing; server-side upload
validation and domain setup still require a real Cloudflare deployment.

## Free-plan budget

Static asset requests are free and unlimited, with no additional asset-storage
cost. Workers Builds has a separate build-minute allowance; watch paths reduce
unnecessary builds. The builder keeps the site below 20,000 files and 25 MiB per
asset and prints its actual size. No dynamic Worker script is deployed. Provider
limits and pricing can change.

Sources: [new-project guidance](https://developers.cloudflare.com/pages/),
[build configuration](https://developers.cloudflare.com/workers/ci-cd/builds/configuration/),
[build branches](https://developers.cloudflare.com/workers/ci-cd/builds/build-branches/),
[watch paths](https://developers.cloudflare.com/workers/ci-cd/builds/build-watch-paths/),
[redirects](https://developers.cloudflare.com/workers/static-assets/redirects/),
[asset pricing](https://developers.cloudflare.com/workers/static-assets/billing-and-limitations/),
[build limits](https://developers.cloudflare.com/workers/ci-cd/builds/limits-and-pricing/).

## Review

Run `node tests/Xur.Integration.Tests/website-ui.cjs` after building. It checks
local links, profile controls, responsive overflow and guide navigation. Private
captures go in `.build/website/` and are registered in `docs/screenshots.md`.
Use only synthetic examples and the reviewed project artwork. Do not copy live
server addresses, users, prompts, credentials or screenshots into the site.

## Search, accessibility and downloads

Pages include unique titles/descriptions, canonical URLs, social cards, WebSite /
WebPage / SoftwareSourceCode and breadcrumb structured data. `404.html` is noindex;
it is excluded from the sitemap. Structured JSON has explicit CSP hashes. Once
`xur.app` is live, verify domain ownership in Google Search Console and submit
`https://xur.app/sitemap.xml`. Metadata cannot guarantee indexing or ranking.

The dark palette matches the app. Keyboard focus, skip navigation, semantic
landmarks, image alternatives, live download status and reduced-motion support
are checked alongside automated WCAG 2.2 AA rules. Automated checks do not replace
a full accessibility audit. Guides describe steps in text as well as screenshots.

`/download/` fetches public metadata from `api.github.com` with no credentials and
selects the newest published installer by publication date (including pre-releases,
clearly labeled). It checks up to 300 recent releases, requires the installer
descriptor asset, and only links to this repository's GitHub release assets.
`/download/?start=1` additionally requests one automatic ISO download after the page
loads; normal navigation to `/download/` does not start one. Downloads go directly
from GitHub, never through Cloudflare or browser-memory blobs. Empty releases,
rate limits, missing media and multipart media have explicit fallback messages.
Multipart media must be assembled according to the release instructions. No live
installer is claimed to exist until a release actually contains it.

Only the download page contacts GitHub's API. CSP permits that API origin for
connections and retains strict local script/style rules. No tracking is added.
The GitHub header mark is from [Octicons](https://github.com/primer/octicons), with
its MIT notice shipped in `assets/github-mark-LICENSE.txt`.

Regenerate public screenshots from synthetic app fixtures:

```sh
node eng/capture-website.cjs
```

Review every image and `docs/screenshots.md` before publishing. This command
requires the local .NET SDK and Playwright test environment; it is not part of
Cloudflare's build. Only reviewed JPEGs under `docs/assets` enter the static site.

Run site checks (test dependencies stay in ignored `.build`):

```sh
npm install --prefix .build/browser --no-save --package-lock=false playwright axe-core
python3 website/build.py
node tests/Xur.Integration.Tests/website-ui.cjs
node tests/Xur.Integration.Tests/website-download.cjs
node tests/Xur.Integration.Tests/website-accessibility.cjs
npx wrangler deploy --config website/wrangler.json --dry-run
```
