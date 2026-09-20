# Repository rules

## Container registries

Always pull Docker Hub images through Google's `mirror.gcr.io` cache. Use fully
qualified `mirror.gcr.io/library/<image>` or `mirror.gcr.io/<publisher>/<image>`
references in new recipes, Dockerfiles, tests and build commands. Preserve pinned
digests. Never add a silent fallback to Docker Hub: a cache miss must fail with a
clear error or be resolved by choosing an explicitly approved alternate registry.
Google caches public images; it is not a full clone and does not serve private Hub
repositories. GHCR, Quay and other explicit registries retain their own addresses.

The host and Fedora builder install `os/containers/99-xur-docker-hub.conf` to cover
legacy saved recipes and user Dockerfiles that still name Docker Hub. Keep this
mapping enabled for Podman, Buildah and Skopeo. Other builder tools must use the
explicit mirror references above. Do not bypass this rule to work around a miss.

## Generated files and secrets

Keep build output, runtime state, test captures and private credentials in ignored
`.build/` or `dist/` paths. Generated evidence belongs in `.build/evidence/`, never
in source archives. Do not package, deploy, commit or push without the user's
instruction for that action.

## Screenshots

Track every screenshot used or published in `docs/screenshots.md`, including its
location, purpose, capture version/date and privacy review. Regenerated private
test screenshots can share a documented capture-family entry. Before each
release, review images affected by UI changes and update or retire stale images.

## Licensing

Original Xur code and documentation are MIT licensed under the root `LICENSE`.
Preserve third-party copyright and license notices. Do not relabel dependencies,
OS packages or model weights as MIT. Keep Xur’s license and `docs/licensing.md`
in shipped bundles, and the font’s OFL notice in the website output.
