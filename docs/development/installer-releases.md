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
5. Sign the app descriptor and installer descriptor, publish them in the same
   versioned GitHub release, then remove the Actions candidates. Update the
   Nightly pointer or Stable latest-release designation only after asset upload.

This workflow does **not** assert that boot/install, GPU or physical USB tests
passed. Its installer receipt explicitly records these as not run. Run the
separate media suite before declaring installer hardware support verified.
No self-hosted runner, signing key or GitHub write token is exposed to PR jobs.
As of the September 20 documentation review, the app build passed but hosted ISO
acceptance remained pending. Earlier runs stopped at builder initialization:
cloud-init's fallback JSON query lacked root access. That readiness check is now
fixed and regression-tested; do not interpret the fix alone as a completed ISO.
Check the latest branch workflow and actual release assets for current results.

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

Release assets include `installer.json`, its Ed25519 signature,
`installer-SHA256SUMS`, an embedded-file inspection report and `INSTALL.md`.
Verify the descriptor against the official public key obtained from a trusted
checkout of `os/bootc/application-update-key.pem`:

```bash
openssl pkeyutl -verify -pubin -inkey application-update-key.pem -rawin \
  -in installer.json -sigfile installer.json.sig
```

For a small ISO, download `xur-installer-x86_64.iso`. GitHub requires each release
asset to be [smaller than 2 GiB](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases),
so larger ISOs are split into 1900 MiB parts. Download all `.partNNN` files from
**the same versioned release**, then assemble in filename order:

```bash
cat xur-installer-x86_64.iso.part* > xur-installer-x86_64.iso
sha256sum xur-installer-x86_64.iso
```

On Windows, in PowerShell:

```powershell
$parts = Get-ChildItem xur-installer-x86_64.iso.part* | Sort-Object Name
$output = [IO.File]::Create("$PWD/xur-installer-x86_64.iso")
try {
  foreach ($part in $parts) {
    $partStream = [IO.File]::OpenRead($part.FullName)
    try { $partStream.CopyTo($output) } finally { $partStream.Dispose() }
  }
} finally { $output.Dispose() }
Get-FileHash xur-installer-x86_64.iso -Algorithm SHA256
```

Compare the assembled SHA-256 to `iso.sha256` in the **verified** `installer.json`.
The descriptor also records each part's name, length and hash. Checksums alone do
not authenticate a download. Write the assembled ISO following [installation](../usage/install.md).

Local builds still use `./eng/build-iso.sh`; `XUR_INSTALLER_CHANNEL=nightly` selects
Nightly, otherwise they default to Stable. Use `xur.app-update=off` at installer
boot to test the embedded app without the public refresh. This does not disable
signature checking or turn the installer into offline media.
