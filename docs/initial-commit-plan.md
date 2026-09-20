# Initial GitHub commit preparation

Status: layout and ignore policy approved; source-only initial commit and push authorized. The staged manifest is audited against the packaging manifest before committing. Generated evidence stays in `.build/evidence/`. No license has been selected; this repository does not grant a software license until one is added. GitHub release publication remains an explicit separate command.

## Current size estimate

The nonignored source candidate list contains **432 files, approximately 2.08 MiB uncompressed** at the initial source audit. Git's compressed object transfer should be smaller; this is the working-file total, not a promised Git pack size. The largest file is the shipped IBM Plex Sans font (about 525 KiB). Generated evidence, runtimes, SDKs, model data and release archives are excluded. `python3 eng/source_files.py` prints the reproducible source manifest and size; it agrees with Git's nonignored candidates.

## Approved structure

Keep the working application layout. Separating production components from build tools and tests is already useful; a large source move would add risk without improving the first commit.

```text
Xur/
├── README.md                         overview, quick start and documentation links
├── LICENSE                           choose the project's license before publishing
├── .gitignore                        reviewed exclusions
├── .github/workflows/                CI and release workflows
├── global.json                       pinned .NET SDK
├── Directory.Build.props             shared build configuration
├── src/
│   ├── Xur.Domain/                   contracts and policies
│   ├── Xur.Agent/                    privileged host operations
│   ├── Xur.Control/                  web manager and local dashboard
│   │   └── wwwroot/                  shipped CSS, JS, font and app icons
│   └── Xur.Gateway/                  workload API routing
├── tools/
│   ├── Xur.Cli/                      shared terminal input source
│   ├── Xur.Console/                  native console source and protocol/license files
│   ├── Xur.VirtualDisplay/           native monitor helper source and protocol/license files
│   └── Xur.Streaming/                upstream lock and redistribution license
├── catalog/                          maintained workload recipes
├── os/                               bootc, installer and engine definitions
├── eng/                              builds, tests, packaging and cleanup scripts; dependency locks
├── tests/
│   ├── Xur.Unit.Tests/
│   ├── Xur.Profile.Tests/
│   ├── Xur.Integration.Tests/
│   ├── Xur.Media.Tests/              maintain VM/media tests as source, not their VM disks
│   └── fixtures/                    only small synthetic or sanitized regression inputs, if needed
├── docs/
│   ├── usage/                        installation, profiles, settings, streaming and APIs
│   ├── architecture/                 host boundaries, persistent state and security model
│   ├── development/                  build, test, release, cleanup and contribution instructions
│   └── releases/                     concise curated release notes, not raw acceptance captures
├── .build/                           ignored: SDK/build caches, VM disks, private state and evidence
└── dist/                             ignored: distributable archives, ISO images and release manifests
```

The `docs/` subdivisions are now applied, with README and intra-document links updated. Do not move application source or change project references. A root project license has not been selected; existing third-party licenses must remain intact regardless of that choice.

## What belongs in the first commit

- Production C#, Razor, Python, C, shell, CSS and JavaScript source; project files; service/udev/installer definitions; maintained recipes.
- Scripts that reproduce builds, validation and release creation, along with upstream/toolchain/engine lock files. Review existing CI from a clean checkout before enabling publication.
- Maintained tests, including slower media tests. Replace dependence on a local machine capture with a small synthetic fixture where a test truly requires one.
- App icons and the bundled font: these are shipped web assets, not build-output accidents. Keep the SVG mark, icon export script, required PNG sizes, font provenance and font license.
- `os/bootc/application-update-key.pem`: this is the **public verification key**, required for the current update trust chain. Keep it. Private signing keys and local credentials remain outside Git.
- Curated current documentation. Consolidate obsolete prep notes/proposals and machine-specific investigation notes before staging.

## What stays out

- `bin/`, `obj/`, `.build/`, `dist/`, dependency installations, copied SDKs and extracted Sunshine/OS runtimes.
- VM disks, rootfs images, container layers, ISO files, update archives, signatures generated for a release, reports, screenshots, crash dumps and benchmark output.
- Local API credentials, browser profiles/storage state, bootstrap codes/QR captures, workstation logs, system configuration backups, private keys, signed-in sessions and real user data.
- All of the current `.build/evidence/` tree. At the prep audit it contained **499 Git-visible generated files totaling 13.55 MiB** (about 16 MiB allocated on disk). Excluding it leaves roughly **405 files / 2.0 MiB** of source and documentation before final curation. Counts will change as prep continues.

## Applied evidence and packaging migration

1. Existing captures were moved intact to `.build/evidence/`; producers and consumers now use that ignored location.
2. Application and ISO source archives share `eng/source_files.py`. It excludes evidence, dependencies, compiled output and private files even when Git is unavailable. Its regression test plants representative artifacts and verifies that only source remains.
3. The source manifest and Git's nonignored candidate list must agree; staging uses explicit source directories and the exact manifest is checked before committing.
4. Release evidence remains separate from source. Sanitize evidence before attaching it to a release/CI run; never attach `.build/private/`.
5. The local fast suite and browser tests have run. Source checks are repeated from an isolated source snapshot before the initial push. The new online ISO still requires KVM builder access and a real boot/install test; source checks do not replace that validation. Single-command packaging and its cleanup step remain intact.

## Before staging

- Finish documentation curation: retain reproducible guidance and concise release history; review older proposals and machine-specific investigation notes.
- Inspect `git status --short --untracked-files=all` and the exact staged file list; stage explicit source directories, not the entire working directory blindly.
- Scan the staged content and the source archive for credentials/private keys and inspect unusually large/binary files. Keep test placeholders distinct from real machine captures.
- Review hardcoded hostnames, usernames, network addresses, local absolute paths and stale release claims in docs/scripts. Replace machine-specific operational history with reproducible instructions.
- Confirm license choice and bundled dependency license notices; choose whether GitHub release publishing should be enabled immediately or remain manual.
- Run the fast checks from the clean checkout and inspect the archive manifest. Initial commit and GitHub push remain separate actions after this review.
```
# Review aids once the plan is applied; these commands do not commit or push:
git status --short --untracked-files=all
git diff --cached --stat
git diff --cached --name-only
git diff --cached --check
```
