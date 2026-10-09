# Dependency updates

Dependabot checks all supported manifests weekly and groups version updates into
one PR. Rolling model-engine channels are refreshed by the client at startup;
Dependabot covers their container manifests but does not turn `latest` into a
version-update PR. Security updates use GitHub's separate scheduling. The source checks run
`eng/check-dependency-coverage.py` to reject supported manifests outside the
configured directories, local images without unconditional Docker ignore rules,
and copied version information that needs refreshing.

The Docker updater ignores `localhost/*` images built by Xur. Dependabot otherwise
looks these names up on Docker Hub and fails because they are not published there.
Their upstream base images remain covered through the corresponding Containerfiles;
source/package pins and matching local tags require the manual review below.

| Dependencies | Authoritative files | Dependabot ecosystem |
| --- | --- | --- |
| NuGet packages | Project files and `packages.lock.json` in all configured project directories | `nuget` |
| .NET SDK | `global.json`; workflows read that file directly | `dotnet-sdk` |
| GitHub Actions | `.github/workflows/*.yml` | `github-actions` |
| llama.cpp variants, vLLM and Omni | `catalog/engines/Containerfile`, embedded by `EngineImages`; rolling channels refresh on start, with a pinned Omni ROCm release | `docker` |
| Intel Omni layer | `catalog/engines/omni-xpu.Containerfile`, embedded by the agent; builds on the mirrored, matching vLLM XPU release | `docker` for the base; manual source/package pins |
| Native gfx1103 vLLM layer | `catalog/engines/vllm-rocm-gfx1103.Containerfile`, embedded by the agent; AMD TheRock device wheels and source-built vLLM | `docker` for the base; manual source/package pins |
| Robotics application and tools | `containers/robot/Containerfile`, which combines the Native AOT app with `tools/Xur.Robotics/Containerfile`; `tools/Xur.Robotics/Discovery.Containerfile` remains an isolated toolkit recipe | `docker` for the bases; manual source/package pins |
| Recording backup receiver | `containers/robot-backup/Containerfile`, shared snapshot protocol and locked .NET projects | `docker` for the bases; `nuget` for locked packages |
| Hosted Fedora native builder | `eng/Containerfile`, read by `eng/ci-native.py` | `docker` |
| Live installer Fedora base | `os/bootc/Containerfile`; resolved once to a digest in a private build recipe | `docker` |
| Playwright and axe-core | `eng/browser/package.json` and lock | `npm` |
| Website deployment CLI | `website/package.json` and lock | `npm` |
| Source-check YAML parser | `eng/requirements.txt` | `pip` |
| Private screenshot/QR test tools | `tests/Xur.Media.Tests/requirements.txt` | `pip` |
| Robotics tag-mount CAD tools | `tools/Xur.Robotics/cad/requirements.txt` | `pip` |

Install browser or website tools with `python3 eng/prepare-npm.py browser` or
`python3 eng/prepare-npm.py website`. Install media Python tools with
`python3 -m pip install --target .build/qr -r tests/Xur.Media.Tests/requirements.txt`.
All installations, caches and private test evidence remain under `.build/`.

`src/Xur.IO` pins TeeForge 0.1.0 and shares its stream, HTTP and process helpers
with the native utility and the application services. Package locks record its
System.IO.Hashing dependency; Dependabot covers the library and utility projects.

Third-party browser libraries are managed in `src/Xur.Control/libman.json`.
Normal Control builds restore AG Grid Community and its MIT notice through
`Microsoft.Web.LibraryManager.Build` into `src/Xur.Control/.build/libman/`, then
copy them to `wwwroot/vendor/ag-grid/` in build and publish output. There is no
separate asset refresh command. Provider downloads happen during the build;
pages serve only local assets and work without internet access. The LibMan build
package is covered by NuGet Dependabot updates; library versions in `libman.json`
need the manual review below because Dependabot does not support LibMan.

The default CPU recipe uses `@engine/server`, resolved to the upstream channel
before validation or saving. Every model-container start pulls the current
channel, including saved selections with older tags or digests. A stopped
container is recreated if its resolved image changes; persistent model-cache
volumes are reused. A running container is left alone. Failed pulls use the
newest locally downloaded Linux amd64 image for the same engine/variant, or the
image retained by the stopped container if no named base remains cached. The
selected image and pull error are recorded in the workload's update log. A pull
failure with no local engine fails startup.

Model containers use `--restart=no`: the control service automatically restores
models from the committed loaded profile through the same `Start` path after an
exit or reboot, instead of letting Podman bypass the update check. Recovery
publishes the current endpoint after health checks, preserves healthy peers, and
backs off for a minute after startup failures. It waits for manual transitions,
pauses during application maintenance, and respects intentional unloads. Saved
profiles alone do not grant automatic startup. Generic prepared containers retain
their selected image and restart policy.

## Manual checks

Workstation previews invoke the installed host's `/usr/bin/spectacle`; no copy
is bundled with Xur. When updating the host image, verify Spectacle's background
capture options against its Plasma/KWin version and check a running workstation's
preview. An absent or failed capture tool must show an unavailable preview.

Dependabot cannot interpret the following custom locks or update vendored source
and model weights. Review these during the weekly dependency PR and before a
release; they are deliberately manual checks, with no companion update service.

| Source | Manual review |
| --- | --- |
| `catalog/engines/omni-xpu.Containerfile` | Keep the vLLM XPU base, pinned Omni Git commit, Triton XPU version and `omni-xpu` local tag in `catalog/engines/Containerfile` aligned. Follow the corresponding upstream Omni XPU build, retain its source/license files in the image, and run Intel GPU acceptance tests after updates. The AMD Omni release uses its separately published ROCm image; verify its tags before changing the manifest. |
| `catalog/engines/vllm-rocm-gfx1103.Containerfile` | Keep the vLLM source commit, matching base release, AMD TheRock PyTorch/vision/audio/Triton/SDK versions, native device extra and local image tag aligned. Install AMD SMI bindings from that SDK, retain upstream notices, and verify allocation, GEMM and inference on a physical gfx1103 device. Do not use an architecture override or substitute the stock ROCm image when a native build fails. |
| `eng/update-usb/app.cs` | Review the pinned TeeForge, LibArchive.Net and System.CommandLine `#:package` versions with upstream releases, keeping System.CommandLine aligned with `tools/Xur.Cli/Xur.Cli.csproj`, then run `tests/Xur.Integration.Tests/update-usb.cs` against disposable FAT32 media. Check ISO hardlinks and forward-only extraction when updating LibArchive.Net; the small metadata adapter uses its public SafeHandle and protected Entry constructor. The file-based app's package directives are maintained explicitly. |
| `src/Xur.Control/libman.json` | Check AG Grid Community releases during the weekly dependency review; update the library version, build Control to restore JavaScript and its MIT notice, and run Files UI checks. LibMan manifests are unsupported by Dependabot. |
| `eng/toolchain-lock.json` | Refresh the SDK archive URL/checksum whenever `global.json` changes; also review Fedora cloud builder images/checksums, Image Builder source releases, Tailscale archives and recorded toolchain metadata. Historical host/engine version records do not select runtime images. |
| `tools/Xur.Console/upstream-lock.json` | Check kmscon releases, refresh the source archive/checksum, and exercise console patches and PTY tests. |
| `tools/Xur.Streaming/upstream-lock.json` | Check Sunshine releases, refresh the AppImage URL/checksum, and verify streaming and input adapters. |
| `tools/Xur.Robotics/upstream-lock.json`, `tools/Xur.Robotics/Containerfile` and `containers/robot/Containerfile` | Keep the LeRobot release, XLeRobot commit, pygame and compatible CPU PyTorch/torchvision/TorchCodec versions aligned. Install TorchCodec from the explicit CPU index; the default Linux wheel requires CUDA libraries. Verify the decoder during the image build and run `tests/Xur.Integration.Tests/robotics-tools.py` offline without device mappings to check CLI/helper imports, CPU ACT loss/inference, native marker detection and a synthetic video round trip. Keep the AprilTag commit, marker family and native build aligned; check physical print rasters, duplicate IDs, image geometry and camera-only inspection after updates. Build/test the combined application image, preserve upstream licenses, and keep robotics dependencies out of the main Xur package; verify calibration, nested torque contexts, controller mappings, dataset recording/resume and the train/replay/rollout CLIs after updates. Run source safety tests and supervised physical acceptance before release. |
| `tools/Xur.VirtualDisplay/README.md` and vendored `screencast.xml` | Review KDE protocol releases, compare the vendored XML, preserve its license, and test against supported KWin. |
| `docs/font-source.json` and vendored font/OFL files | Review the selected Google Fonts/IBM Plex source, refresh checksums and the font together with its OFL notice, and review affected screenshots. |
| `catalog/models/smollm2-135m-cpu.json` | Review Hugging Face checkpoint revisions, file size/checksum and upstream license; the container image is managed through the engine manifest. Model weights have no Dependabot ecosystem. |
| `os/bootc/upstream-lock.json` | Historical Bazzite reference metadata; online installation and OS updates follow the upstream signed stable channel and record the resolved digest for each operation. |
| Documentation links to upstream versioned source | Reference snapshots; refresh links when changing the corresponding runtime or source dependency. |
| OS packages, firmware and drivers; CI runner tools | Managed by Fedora/Bazzite or the hosted runner, rather than package versions in source manifests. Review the selected Fedora/runner release and test new deployments. |
| `tools/Xur.ProfileSwitcher/CMakeLists.txt`, `build.sh` and `tools/Xur.ProfileSwitcher/upstream-lock.json` | Qt 6, KDE KGlobalAccel, libevdev and libudev come from the Fedora native builder. Rebuild with updated packages, retain the generated dependency/license receipt, and check native keyboard/controller behavior on the supported Plasma host. The lock records the matching XCB keysyms notice omitted by Fedora; refresh it with that package's version. |

GitHub's [supported ecosystems reference](https://docs.github.com/en/code-security/reference/supply-chain-security/supported-ecosystems-and-repositories)
describes the supported manifest formats. Download/package checksums and signed
release identities continue to verify artifacts; they are not version selectors.
