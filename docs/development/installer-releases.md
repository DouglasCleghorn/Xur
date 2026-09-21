# Installer release automation

A push to `main` (Nightly) or `release` (Stable) runs **Build and approve release**:

1. Build and run the app's fast and browser checks.
2. Call `installer.yml` to build the online ISO in an isolated Fedora VM on a
   disposable GitHub-hosted Ubuntu runner. Inspect embedded files against the
   build context, BIOS/UEFI layout, SELinux settings and absence of a Bazzite
   payload. The ISO carries the selected application channel into boot-time app
   refresh and the installed system. Bazzite itself still uses its stable channel.
3. Retain unsigned app and installer candidates for seven days, pruning older
   candidates for that channel. The signing key is unavailable to both build jobs.
4. Wait for the maintainer's GitHub Environment approval (`nightly` or `stable`).
   Review both builds before approving. The publication job rejects superseded
   commits and verifies both candidates' hashes and commit/channel receipts.
5. Sign a self-contained JSON descriptor containing the app archive manifest and
   installer hash/inspection receipt. Publish it with the app archive and ISO, then advance the channel's
   `current` pointer and remove the Actions candidates. No source tarball is
   uploaded; GitHub provides source archives for each tag.

Normal versioned releases have **three assets**: `xur-installer-x86_64.iso`,
`xur-update-x86_64.tar.gz`, and `xur-update.json`. The first compact-format release on each channel also has
the legacy descriptor, detached signature and pointer (reusing the app archive). Those transition releases must remain available. Legacy
Nightly `nightly/latest` and GitHub's Stable **Latest** designation stay fixed on
these bridges. New clients use `nightly/current` or `stable/current`, which point
to immutable versioned releases. Consequently GitHub's **Latest** badge is a
migration entry point, not the newest Stable version; use Xur's channel selector
or the website download page. Do not manually move that designation or delete a
migration release. The channel alias's `migration` asset records its fixed tag.

There is one transition generation per channel because old clients reject signed
metadata for the other channel. Each bridge is created by that channel's approved
workflow; publishing Nightly does not silently promote it to Stable.

This workflow does **not** assert that boot/install, GPU or physical USB tests
passed. Its installer receipt explicitly records these as not run. Run the
separate media suite before declaring installer hardware support verified.
No self-hosted runner, signing key or GitHub write token is exposed to PR jobs.
The September 21 `nightly-2026.09.21.26.1` release completed the hosted build and
published a single 1.78 GiB ISO. This proves the build and embedded inspection,
not physical installation or GPU validation.

## Runner resources and cleanup

The builder uses four virtual CPUs and 8 GiB RAM. KVM and at least 40 GiB free
space are checked before building. The preparation script removes unrelated
preinstalled Android/Swift/Haskell/CodeQL SDKs **only on disposable GitHub-hosted
runners**; it refuses to run on a developer or self-hosted machine. Builder disks,
keys and caches stay in `RUNNER_TEMP`; the builder is stopped even on failure.
There is no paid-runner or alternate-registry fallback. All Docker Hub pulls use
Google's mirror, as required by `AGENTS.md`.

## When a candidate fails

- **Source checks passed, release build failed:** inspect the failed release step.
  The release job runs additional browser, packaging and installer checks. A source
  check is not a published update.
- **Console menu assertion:** download the failed-check artifact and inspect
  `console-menu-transcript.log`. The fixture uses persistent HTTP/1.1 responses;
  assertions retain the screen output and do not retry update/power mutations.
- **Cloud-init permission or readiness error:** both status commands must use
  `sudo -n`. Review the printed JSON and exit codes. Recoverable warnings are
  visible; fatal failures and malformed or unfinished state block the build.
  See [builder readiness](build.md) for details.
- **No ISO in Releases:** the installer may have failed, been superseded by a new
  commit, or be waiting for approval. Candidates are Actions artifacts until the
  approved publication job attaches them to a versioned release. Earlier app-only
  releases do not acquire media retroactively.

A new push supersedes the branch's previous release workflow. Review and approve
the newest successful candidate, not a cancelled or superseded run. These
instructions never require disabling signature or media inspection checks.

GitHub documents 14 GB guaranteed storage for standard public Linux runners;
available space after removing unused SDKs can vary. An insufficient-space or KVM
failure fails the candidate and blocks publication, rather than publishing an
incomplete installer. See [runner limits](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

## Download and verify

Download `xur-installer-x86_64.iso` from the release's prominent download link or
[xur.app/download](https://xur.app/download/). The release notes include its SHA-256.
On Windows use `Get-FileHash xur-installer-x86_64.iso -Algorithm SHA256`; on Linux
use `sha256sum xur-installer-x86_64.iso`.

For authenticated verification, download the matching `xur-update.json`
from **the same release** and use a trusted source checkout/public key:

```bash
python3 eng/verify-release.py xur-update.json --iso xur-installer-x86_64.iso
```

This verifies the Ed25519 signature and ISO size/hash without downloading the app
archive. Add `--archive xur-update-x86_64.tar.gz` to verify that too. The signed
JSON contains the installer build receipt and inspection report digest. Full
embedded reports remain in the build's CI inspection output rather than adding
more download assets. Checksums in release notes alone do not authenticate media.

Publication refuses an ISO of 2 GiB or larger so a size regression cannot silently
reintroduce split downloads. Historical split releases still work: concatenate
all `.partNNN` files in filename order, then verify their old `installer.json`
with its `.sig` using `openssl pkeyutl -verify -pubin -inkey
os/bootc/application-update-key.pem -rawin -in installer.json -sigfile
installer.json.sig`. Compare the assembled hash to `iso.sha256` in that verified
JSON. Never write an individual part to USB.

Local builds still use `./eng/build-iso.sh`; `XUR_INSTALLER_CHANNEL=nightly` selects
Nightly, otherwise they default to Stable. Use `xur.app-update=off` at installer
boot to test the embedded app without the public refresh. This does not disable
signature checking or turn the installer into offline media.
