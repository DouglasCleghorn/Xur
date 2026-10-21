# Container workloads

For a persistent coding environment, use the built-in
[T3 Code preset](t3-code.md). It provides storage, profile selection and native
device pairing from Xur, with direct connections to T3 Code on port 3773.

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

## Unsloth Studio

In the profile editor choose **Unsloth · Model training**, select an NVIDIA GPU,
then save and load the profile. No container preparation is required. Xur pulls
`mirror.gcr.io/unsloth/unsloth:latest` before every new start and runs the resolved
immutable image ID. A mirror failure fails the start explicitly, without falling
back to Docker Hub or an older image. Unchanged running workloads keep their
image; unload and load to update.

Choose **Open Unsloth Studio** in Running workloads. Studio connects through
Tailscale Serve at `https://<tailscale-name>:8444/` and uses its own login. Xur
configures this separate HTTPS listener automatically while Unsloth runs,
verifies its proxy, and repairs it after Tailscale restarts. The HTTP backend
binds only to `127.0.0.1:8000`; no LAN HTTP port is exposed. The link uses the
server's Tailscale DNS name even when you open Xur using a LAN address.
Connect Tailscale and enable tailnet HTTPS first. If port 8444 belongs to another
service, Xur reports the conflict without replacing that service. On Xur's
Unsloth page, choose **Show first-login password**, sign in as `unsloth`, then
change the password. Xur only reveals the bootstrap password to an authenticated
browser session; API keys and redacted workload logs cannot reveal it. The
upstream first-login timeout still applies. Subsequent logins use your saved
password. Jupyter and SSH ports are not published.

All Unsloth profiles share two Xur-owned persistent volumes, listed in Storage:

- `/workspace`: training files, notebooks, downloaded models and the Hugging Face
  cache (`HF_HOME=/workspace/.cache/huggingface`). Save exported models here.
- `/opt/unsloth-studio`: Studio accounts, settings, chats and training outputs.

The existing Xur model library is mounted read-only at `/models` (files named by
SHA-256) and `/model-sets` (complete snapshots grouped by their manifest hash).
These are Xur's existing persistent directories, not copies. Use a complete
checkpoint for training; GGUF files are primarily for inference. Models fetched
directly by another engine into its private Hugging Face cache are not in these
shared directories; Unsloth maintains its own persistent download cache.

Only one Unsloth server runs per host. Changing its GPU or profile identity waits
for the previous server to stop before reusing storage and its local backend port.
Unloading removes Unsloth's Serve handler while preserving Xur's HTTPS listener
and other services. Unload,
profile deletion and image replacement preserve both volumes and the model
library. The upstream image retains its bundled code and licenses; models retain
their individual licenses. See the [upstream container documentation](https://github.com/unslothai/unsloth/tree/main/docker)
and [Tailscale Serve reference](https://tailscale.com/docs/reference/tailscale-cli/serve).
