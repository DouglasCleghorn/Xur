# Container workloads

Open **Containers**, choose a registry image or Dockerfile, and prepare it. A
Dockerfile can include up to 64 extra text files (8 MiB total). Preparation uses
real Podman pull/build operations and records the resulting immutable image ID.
It runs separately from profile transitions. The job page shows its stage and
retained command output when the operation finishes. Interrupted preparation must
be retried; it never publishes a partial recipe.

Choose CPU or a GPU vendor/count. In the profile editor select **Container**, the
prepared recipe, and the actual GPUs. Exact device allocation and conflicts use
the same checks as model workloads. Images must contain userspace compatible with
those devices; selecting a vendor does not install a missing runtime in an image.

Advanced fields configure the command (a JSON argument array), environment,
internal HTTP port, and health path. Empty command uses the image defaults. Port 0
supports a long-running service without HTTP. HTTP services receive a stable
`/inference/<workload route>/` path after their health check passes. Generic web
applications may need their own base-path configuration; Xur does not rewrite
application HTML or provide a generic WebSocket proxy.

Add named persistent volumes or reuse an existing one by selecting it. Each mount
has an explicit destination. Arbitrary host directories and privileged containers
are not exposed by this form. Storage lists names, paths, measured usage, and
saved workloads using each volume. Volumes are included in Containers & engines
filesystem usage; their detailed rows are a breakdown, not additional usage.
Unload, stop, and profile deletion preserve volumes. Volume deletion and export
are not implemented in this first form.

Preparing another image creates a new immutable recipe. Selecting it changes only
that workload's fingerprint. A route-only change or profile rename keeps the
backend instance. Builds do not run during profile switching. Application updates
wait for active container preparation, and reject rollback to code that cannot
read container recipes.

Breeze TTS 2 can use this container path, but no Breeze image or 3090 deployment has
been built as part of these tests. Its upstream Dockerfile targets Hopper by
default and needs an appropriate Ampere build for the example machine.

References: [Podman build](https://docs.podman.io/en/latest/markdown/podman-build.1.html),
[Breeze TTS](https://github.com/breezeblue-ai/breeze-tts).
