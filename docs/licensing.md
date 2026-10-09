# Licensing

Xur's original application code, tools, documentation and website are open source
under the [MIT License](../LICENSE). Keep that copyright and license notice with
copies or substantial portions of Xur. Contributions to Xur use the same license
unless a file explicitly states otherwise.

Third-party material retains its existing copyright and license. Xur's MIT
license does not replace licenses for Bazzite/Fedora packages, NVIDIA components,
Sunshine, the native console and its libraries, KDE protocols, fonts, NuGet
packages, container images, games or downloaded model weights.

Existing notices include:

- Sunshine: `tools/Xur.Streaming/LICENSE`, its pinned upstream information, and
  license files retained in the extracted streaming runtime.
- Virtual-display protocol: `tools/Xur.VirtualDisplay/COPYING` and the copyright
  notices in `screencast.xml`.
- Console runtime: upstream information in `tools/Xur.Console/upstream-lock.json`
  and license files collected by its build script.
- Profile switcher: original helper code is MIT; Qt, KDE KGlobalAccel, libevdev,
  libudev and their runtime dependencies retain their upstream licenses. Its
  runtime includes collected RPM license notices and `dependencies.json` with
  exact package versions, license declarations and corresponding source RPMs.
- IBM Plex Sans: `src/Xur.Control/wwwroot/fonts/OFL.txt`, also copied to the public
  website alongside the font.
- GitHub mark in the website header: GitHub Octicons, MIT; notice shipped in
  `website/assets/github-mark-LICENSE.txt`.
- USB updater: TeeForge and System.CommandLine retain their MIT licenses.
  LibArchive.Net uses [BSD-2-Clause](https://github.com/jas88/libarchive.net/blob/31e5dd26941e1d65a35818562c23ee42064498d0/LICENSE.md);
  its bundled native libarchive and dependencies retain their upstream notices.
  These NuGet packages are restored during file-based app builds and are not
  relabeled under Xur's license.
- OS packages and container images: their upstream license/source notices.
- Robotics tools: LeRobot and XLeRobot retain Apache-2.0 notices; pygame retains
  LGPL-2.1-or-later; PyTorch, torchvision and TorchCodec retain their respective
  BSD notices. The pinned XLeRobot source/license remains in the prepared image,
  Python package distributions retain their notices, and `/opt/licenses/` carries
  Xur's license and this document. Other Python/OS dependencies retain their own
  licenses, including the minimal discovery image's Feetech SDK, pyserial,
  DeepDiff, NumPy, tqdm and draccus package notices. No model weights are bundled
  or relabeled as MIT.
- Robotics marker artwork: the generator downloads unmodified
  `tagStandard41h12` patterns from AprilRobotics/apriltag-imgs at
  `f3fd9a7add5bfd82a886fc65240fdb8e3c9ac5a1`. That artwork retains its
  BSD-2-Clause notice in the generated `AprilTag-LICENSE.txt` and on the PDF
  instructions page. Xur's original printing generator remains MIT licensed.
- Robotics marker detector: the C adapter uses AprilTag at
  `b7c0ebe9aa20f82ec7a828579004f9e706bfecd9`, retaining its BSD-2-Clause notice
  in the ignored upstream checkout. Keep that notice with any distributed
  detector. The prepared robotics tools image includes the pinned source and
  `/opt/licenses/AprilTag-LICENSE.md`. The original C, Python and .NET survey
  wrappers are MIT licensed.
- Model weights: each selected repository's own license; the model catalog keeps
  the model repository, license and revision. MIT licensing of Xur does not grant
  rights to third-party models or games.

App bundles carry Xur's `LICENSE` and this notice in `licensing.md`. Installer
images also carry them under `/usr/share/licenses/xur/`. Existing bundled
third-party notices remain in their component directories.

- Files grid: AG Grid Community (MIT), selected in `src/Xur.Control/libman.json`.
  LibMan restores its JavaScript and copyright notice during the build; both
  ship under `wwwroot/vendor/ag-grid/`. No Enterprise modules are used.

`xurutil` uses Microsoft's MIT-licensed `System.CommandLine` and the .NET Native
AOT runtime, plus [TeeForge](https://github.com/DouglasCleghorn/TeeForge) 0.1.0
(MIT, copyright Doug Cleghorn). The shared `Xur.IO` library also brings TeeForge
into the application services. Published `licenses/` directories carry the
TeeForge license and third-party notices (including its .NET/System.IO.Hashing
notices). The utility additionally carries the command-line library license,
.NET license and runtime third-party notices. These ship in the
application bundle under `host/licenses/` and in the installer under
`/usr/share/licenses/xurutil/`; independent recovery and shared station helper
copies retain their notices too.

## Robot dashboard container

The original `containers/robot` application is covered by Xur's MIT license.
Its Native AOT executable includes .NET/ASP.NET Core code; the image retains
Microsoft's `LICENSE.txt` and `ThirdPartyNotices.txt` under `/app/licenses/`,
alongside Xur's license and this document. The `mcr.microsoft.com` SDK and
runtime-dependencies images retain their upstream and operating-system licenses.
No third-party browser libraries are used by this dashboard.
