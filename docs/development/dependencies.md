# Dependency updates

Dependabot checks all supported manifests weekly and groups version updates into
one PR. Rolling model-engine channels are refreshed by the client at startup;
Dependabot covers their container manifests but does not turn `latest` into a
version-update PR. Security updates use GitHub's separate scheduling. The source checks run
`eng/check-dependency-coverage.py` to reject supported manifests outside the
configured directories and copied version information that needs refreshing.

| Dependencies | Authoritative files | Dependabot ecosystem |
| --- | --- | --- |
| NuGet packages | Project files and `packages.lock.json` in all seven configured project directories | `nuget` |
| .NET SDK | `global.json`; workflows read that file directly | `dotnet-sdk` |
| GitHub Actions | `.github/workflows/*.yml` | `github-actions` |
| llama.cpp variants, vLLM and Omni | `catalog/engines/Containerfile`, embedded by `EngineImages`; the client pulls the rolling channels on each start | `docker` |
| Hosted Fedora native builder | `eng/Containerfile`, read by `eng/ci-native.py` | `docker` |
| Live installer Fedora base | `os/bootc/Containerfile`; resolved once to a digest in a private build recipe | `docker` |
| Fish image and Python codec layer | `os/engines/fish/Containerfile`, `os/engines/fish/requirements.txt` | `docker`, `pip` |
| Playwright and axe-core | `eng/browser/package.json` and lock | `npm` |
| AG Grid | `src/Xur.Control/wwwroot/vendor/ag-grid/package.json` and lock | `npm` |
| Website deployment CLI | `website/package.json` and lock | `npm` |
| Source-check YAML parser | `eng/requirements.txt` | `pip` |
| Private screenshot/QR test tools | `tests/Xur.Media.Tests/requirements.txt` | `pip` |

Install browser or website tools with `python3 eng/prepare-npm.py browser` or
`python3 eng/prepare-npm.py website`. Install media Python tools with
`python3 -m pip install --target .build/qr -r tests/Xur.Media.Tests/requirements.txt`.
All installations, caches and private test evidence remain under `.build/`.

An AG Grid manifest update needs a reviewed refresh of the vendored source assets:
run `python3 eng/update-ag-grid.py`, review the JavaScript and license changes,
then run the Files UI checks. Coverage validation rejects a stale vendored version.
Fish and catalog Omni references must agree; coverage validation checks both.
The default CPU recipe uses `@engine/server`, resolved to the upstream channel
before validation or saving. Every model-container start pulls the current
channel, including saved selections with older tags or digests. A stopped
container is recreated if its resolved image changes; persistent model-cache
volumes are reused. A running container is left alone. Failed pulls fail startup
rather than starting an old cached image. Fish's cache includes the downloaded
Omni image ID, so its codec layer rebuilds whenever that base changes.

Model containers use `--restart=no`: Podman cannot automatically restart an old
image and bypass Xur's check. Use Load or Resume after an engine exits or after
rebooting. Generic prepared containers retain their selected image and restart
policy; these are user-created environments rather than managed model engines.

## Manual checks

Dependabot cannot interpret the following custom locks or update vendored source
and model weights. Review these during the weekly dependency PR and before a
release; they are deliberately manual checks, with no companion update service.

| Source | Manual review |
| --- | --- |
| `eng/toolchain-lock.json` | Refresh the SDK archive URL/checksum whenever `global.json` changes; also review Fedora cloud builder images/checksums, Image Builder source releases, Tailscale archives and recorded toolchain metadata. Historical host/engine version records do not select runtime images. |
| `tools/Xur.Console/upstream-lock.json` | Check kmscon releases, refresh the source archive/checksum, and exercise console patches and PTY tests. |
| `tools/Xur.Streaming/upstream-lock.json` | Check Sunshine releases, refresh the AppImage URL/checksum, and verify streaming and input adapters. |
| `tools/Xur.VirtualDisplay/README.md` and vendored `screencast.xml` | Review KDE protocol releases, compare the vendored XML, preserve its license, and test against supported KWin. |
| `docs/font-source.json` and vendored font/OFL files | Review the selected Google Fonts/IBM Plex source, refresh checksums and the font together with its OFL notice, and review affected screenshots. |
| `catalog/models/smollm2-135m-cpu.json` | Review Hugging Face checkpoint revisions, file size/checksum and upstream license; the container image is managed through the engine manifest. Model weights have no Dependabot ecosystem. |
| `os/bootc/upstream-lock.json` | Historical Bazzite reference metadata; online installation and OS updates follow the upstream signed stable channel and record the resolved digest for each operation. |
| Documentation links to upstream versioned source | Reference snapshots; refresh links when changing the corresponding runtime or source dependency. |
| OS packages, firmware and drivers; CI runner tools | Managed by Fedora/Bazzite or the hosted runner, rather than package versions in source manifests. Review the selected Fedora/runner release and test new deployments. |

GitHub's [supported ecosystems reference](https://docs.github.com/en/code-security/reference/supply-chain-security/supported-ecosystems-and-repositories)
describes the supported manifest formats. Download/package checksums and signed
release identities continue to verify artifacts; they are not version selectors.
