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
- IBM Plex Sans: `src/Xur.Control/wwwroot/fonts/OFL.txt`, also copied to the public
  website alongside the font.
- GitHub mark in the website header: GitHub Octicons, MIT; notice shipped in
  `website/assets/github-mark-LICENSE.txt`.
- OS packages and container images: their upstream license/source notices.
- Fish codec dependencies: upstream notices retained in the installed Python
  packages in the derived engine image; see `os/engines/fish/README.md`.
- Model weights: each selected repository's own license; the model catalog keeps
  the model repository, license and revision. MIT licensing of Xur does not grant
  rights to third-party models or games.

App bundles carry Xur's `LICENSE` and this notice in `licensing.md`. Installer
images also carry them under `/usr/share/licenses/xur/`. Existing bundled
third-party notices remain in their component directories.
