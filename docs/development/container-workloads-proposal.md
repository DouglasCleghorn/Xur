# Generic container workloads — design direction

The first implementation is documented in [Container workloads](../usage/container-workloads.md).
Git build contexts, arbitrary binary uploads, secret inputs, resource-limit fields,
volume export, and volume deletion remain future extensions.

Reuse the existing Podman runtime and profile reconciliation. Add Container as a
separate workload type beside Workstation and the model engines. The same stable
workload ID and runtime fingerprint determine Keep, Restart, Start, and Stop.
Do not introduce a separate container manager that can race Xur's GPU allocations.

Two image sources:

- Prebuilt image: enter a registry reference, inspect its defaults, and resolve the
  selected version to an immutable digest. Suggest a name; never ask for an ID.
- Build: supply a Git revision plus Dockerfile path, or upload a Dockerfile with
  its build context. Build separately from the active workload, show build logs,
  and save the resulting image ID before offering to load it. Do not rebuild on
  every profile switch. Podman uses Buildah internally and accepts Dockerfiles.

The basic form asks for image/build source, CPU or selected GPU(s), and persistent
folders. Advanced contains command/entrypoint, ports, environment/secrets, health
probe, and resource limits. Show an Open button for ordinary web apps; routing a
custom HTTP service does not imply its API is OpenAI-compatible. Exact GPU access
uses the same vendor/device allocation rules as model workloads.

Persistent volumes have a friendly name, stable ID, mount destination, observed
size, backing disk/filesystem, and a list of workloads using them. Storage lists
volumes separately from images and build cache. Unload and Delete profile retain
volume data. Data deletion is a separate explicit action. Image updates stage a
new digest and restart only the changed workload; preserve volumes and previous
image metadata. Image rollback cannot promise to undo an application's database
migration, so backup/export belongs beside volume management.

Breeze TTS 2 is a suitable first template. Its official repository includes a
Docker build and a PyTorch streaming API on internal port 7860. Suggested volumes
are model weights, reference voices, and generated audio. The default upstream
Docker build targets H100/Hopper (sm90), so the RTX 3090 needs an appropriately
built Ampere runtime. The project's documented eager memory estimate is not a
measurement on the user's machine. Its source and model licenses differ; link the
model's research/non-commercial terms at download selection.

Sources:
[Podman builds](https://docs.podman.io/en/latest/markdown/podman-build.1.html),
[Podman volumes](https://docs.podman.io/en/latest/markdown/podman-volume.1.html),
[Breeze TTS 2 upstream](https://github.com/breezeblue-ai/breeze-tts).

This document records the broader direction. See the implementation document for
the fields and limits available in the current app update.
