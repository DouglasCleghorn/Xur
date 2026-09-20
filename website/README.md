# xur.app

The public Xur project site and short guides. Plain HTML, CSS and a small optional
profile-diagram script; Python's standard library assembles shared navigation and
copies the existing brand assets. No npm install, external fonts, tracking,
Functions, Workers, database, or paid storage service.

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

## Cloudflare Pages Git integration

Connect `DouglasCleghorn/Xur` to a **Pages** project in Cloudflare, then set:

| Setting | Value |
| --- | --- |
| Framework | None |
| Production branch | `main` |
| Root directory | Repository root (leave blank) |
| Build command | `python3 website/build.py` |
| Build output directory | `website/dist` |
| Build watch include paths | `website/*`, `docs/assets/*`, `src/Xur.Control/wwwroot/icons/*`, `src/Xur.Control/wwwroot/fonts/*` |
| Preview branches | None initially; enable selected branches when needed |

Add `xur.app` under the Pages project's Custom domains and follow Cloudflare's
DNS instructions. Optionally attach `www.xur.app`; `_redirects` sends it to the
apex. Register the domain in Pages before changing DNS. Git integration handles
subsequent website pushes; no Cloudflare API token needs to be stored in this
repository or GitHub Actions. These account/DNS steps are required before the
site is live.

Keep app downloads and installer images on GitHub Releases; do not copy release
artifacts into this site. Release approval gates govern app distribution, while
this static documentation site deploys only on changes to its watched paths.

## Free-plan budget

Cloudflare documentation checked 2026-09-20: Free allows 500 builds/month, one
concurrent build, a 20-minute build timeout, 20,000 files/site and 25 MiB/asset.
The builder checks file and per-asset limits and prints the actual output size.
Watch paths avoid consuming a site build for unrelated app changes. No Pages
Functions are used, so this site does not consume a Workers invocation quota.
Static site requests and bandwidth are free under the current Pages pricing;
this is not a promise that provider terms will never change.

Sources: [limits](https://developers.cloudflare.com/pages/platform/limits/),
[build configuration](https://developers.cloudflare.com/pages/configuration/build-configuration/),
[watch paths](https://developers.cloudflare.com/pages/configuration/build-watch-paths/),
[pricing](https://developers.cloudflare.com/pages/functions/pricing/).

## Review

Run `node tests/Xur.Integration.Tests/website-ui.cjs` after building. It checks
local links, profile controls, responsive overflow and guide navigation. Private
captures go in `.build/website/` and are registered in `docs/screenshots.md`.
Use only synthetic examples and the reviewed project artwork. Do not copy live
server addresses, users, prompts, credentials or screenshots into the site.
