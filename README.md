# Xur

Xur is an x86-64, image-based operating system with web-based installation and
system management and workload profiles.

- [Install Xur](docs/usage/install.md)
- [Build the ISO](docs/development/build.md): `./eng/build-iso.sh`
- [Rufus ISO/File Copy mode](docs/usage/rufus.md)
- [Bazzite host and desktop startup](docs/architecture/bazzite-host.md)
- [OS updates](docs/usage/updates.md)
- [Signed updates and local build testing](docs/usage/application-updates.md): `./eng/publish-update.sh --version VERSION`
- [Profiles and model endpoints](docs/usage/profiles.md)
- [Storage usage](docs/usage/storage.md)
- [GPU monitoring](docs/usage/gpu-monitoring.md)
- [Model catalogs and gaming workstation](docs/usage/model-catalog.md)
- [API initialization](docs/architecture/api-initialization.md)
- [Implementation status](docs/development/rebuild-status.md)
- [Remaining work and persistent USB assignment](docs/development/remaining-work.md)

Build output is in `dist/`. Private VM disks, sessions and console output are
kept under `.build/` and excluded from release archives.

Management changes: [username/password setup](docs/architecture/manager-account.md), [custom containers](docs/usage/container-workloads.md), [app update 2026.09.15.1](docs/releases/app-update-2026.09.15.1.md).

Security and source preparation: [request security and exceptions](docs/architecture/request-security.md), [registry policy](docs/development/container-registry.md), [initial commit preparation](docs/initial-commit-plan.md).

Public application releases use [GitHub Releases](https://github.com/DouglasCleghorn/Xur/releases). The [online installer](docs/architecture/online-installer.md) downloads Bazzite from its upstream stable channel. [Named workstations](docs/usage/workstation-identities.md) keep Moonlight pairing when profiles change GPU allocation.
