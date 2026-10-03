# Installer release automation

A push to `main` (Nightly) or `release` (Stable) runs **Build and publish release**:

1. Run the reusable **Source checks** workflow for this exact commit, then build
   and run the app's fast and browser checks. Pull requests run the same source
   checks without release permissions or signing secrets.
2. Always create and upload the tested application update archive without an
   approval gate. Compare installer inputs against the channel's last published
   ISO, including changes from canceled or unpublished runs. Application-only
   changes skip the ISO. OS files, build tooling, native helpers, licensing and
   release/installer workflows select a rebuild. Missing baselines or history and
   failed lookups select a conservative rebuild. Manual dispatch can force a build
   with `build_iso` for final release media verification.
3. When selected, transfer the tested publication context to `installer.yml` with
   commit, channel and SHA-256 receipts. The installer verifies it instead of
   repeating publication and application checks. Build and inspect the online ISO
   in an isolated Fedora VM on a disposable GitHub-hosted runner. Inspect embedded
   files, BIOS/UEFI layout, SELinux settings and absence of a Bazzite payload. The
   tested context carries the selected application channel; Bazzite uses Stable.
4. Retain unsigned app and installer candidates for seven days, pruning older
   candidates for that channel. The signing key is unavailable to both build jobs.
   Transferred build contexts expire after one day and are removed after publication.
5. Nightly publishes automatically after every required check and build passes.
   Stable waits for the maintainer's `stable` GitHub Environment approval after
   hardware testing. Both environments retain their branch restrictions (`main`
   for Nightly, `release` for Stable) and signing secrets. The publication job
   depends on source checks, the app build and any selected installer inspection.
   A failed or canceled required ISO blocks publication. It rejects superseded
   commits and verifies candidate hashes and commit/channel receipts.
6. Sign a self-contained JSON descriptor containing the app archive manifest and,
   when rebuilt, the installer hash/inspection receipt. Publish it with the app
   archive and optional ISO, then advance the channel's
   `current` pointer and remove the Actions candidates. No source tarball is
   uploaded; GitHub provides source archives for each tag.

Installer releases have **three assets**: `xur-<channel>-<version>-x86_64.iso`,
`xur-update-x86_64.tar.gz`, and `xur-update.json`. The first compact-format release on each channel also has
the legacy descriptor, detached signature and pointer (reusing the app archive). Those transition releases must remain available. Legacy
Nightly `nightly/latest` and GitHub's Stable **Latest** designation stay fixed on
these bridges. New clients use `nightly/current` or `stable/current`, which point
to immutable versioned releases. Consequently GitHub's **Latest** badge is a
migration entry point, not the newest Stable version; use Xur's channel selector
or the website download page. Do not manually move that designation or delete a
migration release. The channel alias's `migration` asset records its fixed tag.

Application-only releases contain the app archive and signed JSON descriptor.
The last installer release remains available; the website selects releases that
contain media. The descriptor never claims that an older ISO was built from the
newer application commit. New pushes cancel older runs on the same branch,
including runs waiting for Stable approval.

## Release versions

Stable uses `YY.MM.z` (for example `26.09.1`); Nightly uses `YY.MM.zzz`
(`26.09.001`). Each channel has its own monthly counter, starting at 1 in a
new UTC calendar month. `eng/release-version.py` selects one more than the
highest existing tag number for that channel and month. Gaps are not reused;
failed or cancelled builds that did not publish a tag do not consume a number.
Nightly numbers have at least three digits, so the counter continues past 999.
Keep release tags even when cleaning up old assets to preserve the counter.

Tags remain `v26.09.1` and `nightly-26.09.001`. ISO names include both the
channel and version: `xur-stable-26.09.1-x86_64.iso` and
`xur-nightly-26.09.001-x86_64.iso`. Existing long-version releases and migration
pointers remain valid. Updater ordering uses the signed publication sequence,
not a numeric comparison of the displayed version, so shortening the year
does not block upgrades. Local contributor builds may still set their own version.

Version lookup fails if the remote cannot be read. It never invents a fallback
number. Per-branch workflow concurrency and the publication check for a
superseded commit remain in effect; an existing release tag is never overwritten.

## Migration and validation

There is one transition generation per channel because old clients reject signed
metadata for the other channel. Each bridge is created by that channel's release
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
The prepared Fedora disk template is stored under ignored `.build/fedora-toolchain/`
and cached by toolchain inputs and UTC week. Cold runs provision Fedora and compile
Image Builder, then remove cloud-init state, login/host SSH keys and logs, trim free
blocks and shut down before converting the disk. Only this pre-build toolchain
template enters the cache. Warm builds verify its checksum and use a fresh overlay,
cloud-init seed and SSH identity. Weekly refreshes update provisioned RPMs.
Container layers and OSBuild output currently remain local to each disposable run;
this cache saves toolchain preparation only.
The hosted runner verifies its transient KVM ACL after package preparation and
again immediately before the ISO build. This covers device access being lost
between the cold template VM's shutdown and the next VM startup; the helper
refuses to run on developer or self-hosted machines.
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
  commit, or (for Stable) be waiting for approval. Candidates are Actions artifacts
  until the publication job attaches them to a versioned release. Earlier app-only
  releases do not acquire media retroactively.

A new push supersedes the branch's previous release workflow. For Stable, review
and approve only the newest successful candidate. Nightly has no manual approval
step. These
instructions never require disabling signature or media inspection checks.

GitHub documents 14 GB guaranteed storage for standard public Linux runners;
available space after removing unused SDKs can vary. An insufficient-space or KVM
failure fails the candidate and blocks publication, rather than publishing an
incomplete installer. See [runner limits](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

## Download and verify

Download `xur-stable-<version>-x86_64.iso` or `xur-nightly-<version>-x86_64.iso`
from the release's prominent download link or
[xur.app/download](https://xur.app/download/). The release notes include its SHA-256.
The examples below use the local name `xur-installer-x86_64.iso`; substitute your downloaded filename.
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
boot to disable background public update checks. Normal boot uses the embedded
app immediately; `xur.app-update=on` explicitly enables the older pre-start signed
refresh and its potential two-minute wait. Neither option disables signature
checking or turns the installer into offline media.
