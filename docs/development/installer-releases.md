# Installer release automation

A push to `main` (Nightly) or `release` (Stable) runs **Build and publish release**:

1. Run the reusable **Source checks** workflow and the application build in
   parallel for this exact commit. The application build runs fast and browser
   checks and always uploads the tested update archive without approval. Pull
   requests run source checks without release permissions or signing secrets.
2. Publish the signed application update as soon as both checks and the app build
   pass. Nightly publishes automatically. Stable waits for the maintainer's
   `stable` GitHub Environment approval after hardware testing. Both environments
   retain their branch restrictions (`main` for Nightly, `release` for Stable) and
   signing secrets. Publication rejects superseded commits and verifies candidate
   hashes and commit/channel receipts. It never waits for an ISO job.
3. **ISO builds are manual only.** Use **Run workflow**, select `main` or `release`,
   and enable `build_iso` to build and publish media. A push never builds an ISO,
   even when installer or OS inputs change. Review those changes and request new
   media when needed; the previous installer remains available until then.
4. When requested, transfer the tested publication context to `installer.yml`
   with commit, channel and SHA-256 receipts. The installer verifies it instead of
   repeating application publication and checks. Build and inspect the online ISO
   in an isolated Fedora VM on a disposable GitHub-hosted runner. Inspect embedded
   files, BIOS/UEFI layout, SELinux settings and absence of a Bazzite payload. This
   runs alongside application publication; failure does not block or undo the
   update. The context carries the selected application channel; Bazzite uses Stable.
   After inspection, qualify that exact candidate in fresh UEFI/KVM VMs with
   offline startup, no eligible disk, read-only diagnostics, conflicting answer
   files, canceled erasure, and online install/reboot/account/profile persistence
   checks. A failed VM qualification blocks installer publication.
   See [automated VM qualification](vm-testing.md) for commands and evidence.
5. After inspection, VM qualification and application publication succeed, publish a separate
   `nightly-<version>-installer` or `v<version>-installer` release. Stable installer
   publication also requires environment approval. Download and verify the signed
   app descriptor and archive from the matching, already-published app release,
   verify the inspected ISO candidate, then sign a new descriptor that adds the ISO
   receipt. The original app archive bytes and signed update sequence are retained.
   This release never changes the app release, channel's
   `current` pointer or GitHub's **Latest** designation.
6. Retain unsigned candidates for seven days, pruning older candidates for that
   channel. Build jobs cannot access signing keys. Contexts expire after one day;
   app publication removes its app candidate, and installer publication removes
   its installer candidate and context. No source tarball is uploaded; GitHub
   provides source archives for each tag. New pushes cancel older runs, including
   runs waiting for Stable approval, and each publisher rejects superseded commits.

Installer releases have **three assets**: `xur-<channel>-<version>-x86_64.iso`,
`xur-update-x86_64.tar.gz`, and `xur-update.json`. Application releases contain
only the archive and signed JSON descriptor, including the first release on a
channel. Public clients use `nightly/current` or `stable/current` to resolve an
immutable versioned application release. Each Stable application publication
advances GitHub's **Latest** designation; Nightly and detached installer releases
never advance it. The website download page selects the newest release with media.

Ext4-root releases and their public updater bridges were retired on 2026-10-03.
The publisher no longer creates legacy descriptors, `nightly/latest` or
`migration` markers. On the next successful publication for a channel, it removes
any leftover `latest` and `migration` alias assets after uploading `current`.
A channel without a `current` pointer is unavailable; public clients report that
no release is available instead of following retired discovery paths. Stable
requires a new Btrfs application release before its channel can be used again.

Application releases contain the app archive and signed JSON descriptor.
The last installer release remains available; the website selects releases that
contain media. The descriptor never claims that an older ISO was built from the
newer application commit. New pushes cancel older runs on the same branch,
including runs waiting for Stable approval.

## Release versions

Stable uses `YY.MM.z` (for example `26.09.1`); Nightly uses `YY.MM.zzz`
(`26.09.001`). Each channel has its own monthly counter, starting at 1 in a
new UTC calendar month. `eng/release-version.py` selects one more than the
highest existing tag number for that channel and month. Gaps are not reused;
failed or cancelled builds that did not publish an app tag do not consume a number.
Installer tags reuse the matching app version and do not consume another number.
Nightly numbers have at least three digits, so the counter continues past 999.
Keep release tags even when cleaning up old assets to preserve the counter.

Application tags remain `v26.09.1` and `nightly-26.09.001`; manually requested
media uses `v26.09.1-installer` and `nightly-26.09.001-installer`. ISO names include both the
channel and version: `xur-stable-26.09.1-x86_64.iso` and
`xur-nightly-26.09.001-x86_64.iso`. These filenames illustrate the version format;
the corresponding ext4-root downloads were retired. Historical Git tags remain
available for source history and release numbering. Updater ordering uses the
signed publication sequence, not a numeric comparison of the displayed version, so shortening the year
does not block upgrades. Local contributor builds may still set their own version.

Version lookup fails if the remote cannot be read. It never invents a fallback
number. Per-branch workflow concurrency and the publication check for a
superseded commit remain in effect; an existing release tag is never overwritten.

## Validation

Signed metadata binds an application release to its channel. Each channel uses
its own publication workflow; publishing Nightly does not promote it to Stable.

Installer publication now requires the separate VM qualification job to pass.
The signed build/inspection receipt still records installation as not run at its
creation; the later VM receipt is retained separately in Actions artifacts.
Neither receipt establishes GPU or physical USB/audio support. Run physical
acceptance before declaring installer hardware support verified.
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
  until the installer publisher creates a separate versioned media release.
  Application releases do not acquire media retroactively. Check whether the run
  was manually dispatched with `build_iso`; pushes intentionally skip media.

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

Publication refuses an ISO of 2 GiB or larger. The retired ext4-root releases,
including historical split media, are no longer offered for download. Preserve
Git tags when retiring downloads so source history and version counters remain
available. New installations use a Btrfs root; `/boot` remains ext4 and EFI is FAT32.

Local builds still use `./eng/build-iso.sh`; `XUR_INSTALLER_CHANNEL=nightly` selects
Nightly, otherwise they default to Stable. Use `xur.app-update=off` at installer
boot to disable background public update checks. Normal boot uses the embedded
app immediately; `xur.app-update=on` explicitly enables the older pre-start signed
refresh and its potential two-minute wait. Neither option disables signature
checking or turns the installer into offline media.
