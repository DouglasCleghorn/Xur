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
`/usr/share/licenses/xurutil/`; independent recovery and virtual-display helper
copies retain their notices too.
