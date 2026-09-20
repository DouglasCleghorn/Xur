# Docker Hub cache policy

New Docker Hub references use `mirror.gcr.io`, preserving any manifest digest.
The agent installs `os/containers/99-xur-docker-hub.conf` at startup so Podman,
Buildah and Skopeo also redirect legacy Docker Hub references and user Dockerfile
bases. The Fedora builder installs the same mapping before pulling/building.
There is no Docker Hub fallback. GHCR, Quay and other explicit registries are
unchanged. The policy is also a repository rule in [AGENTS.md](../../AGENTS.md).

Google's service caches popular public Docker Hub images; it is not a complete
clone or private registry. A missing image fails the pull. Select an available
image or deliberately publish an approved copy to another registry, then pin its
digest. Do not retry against Docker Hub silently.

[Google's mirror documentation](https://docs.cloud.google.com/artifact-registry/docs/pull-cached-dockerhub-images).
