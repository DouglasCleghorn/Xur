# Models and gaming workstation

Create a profile and choose **Workstation**, **llama.cpp**, **vLLM**,
**vLLM-Omni**, or **Container** under **Workload type**. Names, workload IDs and
routes are generated automatically. The model selector searches Unsloth’s GGUF
models for llama.cpp, Hugging Face text-generation models for vLLM, or the
upstream vLLM-Omni supported-model list. Search returns up to 100 matches, ordered
by upstream downloads where available; typing narrows the upstream search.

GGUF choices include the published quantizations. Q4_K_M is preferred when
available. Xur imports numeric sampling defaults from Unsloth’s default YAML,
matching family, and matching model YAML, and records the exact settings commit.
It uses a 4096-token context and one concurrent slot initially. These resource
defaults are Xur’s starting settings, not measured per-model optimums. GPU count
is estimated from checkpoint size, observed capacity and a memory allowance.
The editor selects unoccupied compatible GPUs and permits an explicit override.
Actual peak memory can exceed the estimate; startup failures remain visible.

Selecting a model resolves its repository commit, file hashes and engine channel.
All shards of a split GGUF are downloaded and verified before launch. Cached
weights live under `/var/lib/xur`; immutable selected recipes are stored in
`/var/lib/xur/catalog-selected`. Refreshing search never modifies an existing
profile or a running model. GGUF sampling provenance does not restart a model
when the actual runtime arguments and files are unchanged.

llama.cpp has current upstream CPU, CUDA, ROCm and Vulkan image channels. vLLM and
vLLM-Omni support NVIDIA CUDA, AMD ROCm and Intel XPU. Choose **AMD GPUs** or
**Intel GPUs** under **Run on**, or use **Automatic** to select an available
GPU vendor. GPU engines require healthy devices with observed dedicated memory;
Intel capacity comes from the i915/xe kernel memory-region query. Integrated
GPUs without dedicated memory are not assigned capacity from system RAM.
They invoke their upstream `vllm serve`
commands with the selected checkpoint revision; Omni uses `--omni`. vLLM uses
tensor parallelism across the allocated GPUs. Omni uses its upstream deployment
defaults; Xur does not invent multi-stage parallelism settings. Models requiring
custom deployment files or publisher patches need dedicated recipes. The model
list is discovery, not a guarantee that every upstream architecture runs in the
current engine. Save an authorized Hugging Face token in **Settings** for gated
repositories and obtain any required publisher access first. Xur's catalog and
model downloaders use the saved credentials; a token does not grant access the
account does not already have. Review the linked model license before applying.
Model weights are not bundled.

vLLM validates the checkpoint's root `config.json`. vLLM-Omni also accepts
Diffusers pipelines, such as Qwen-Image-2.1, that publish `model_index.json`
and component configurations instead. Xur checks these files at the selected
checkpoint revision during selection and startup. Missing configuration and
upstream HTTP errors are reported separately from connection failures; denied
access points to repository permissions and the Hugging Face token in Settings.

`catalog/engines/Containerfile` selects the upstream rolling channels: `server`
and its GPU variants for llama.cpp, and `latest` for vLLM's CUDA, ROCm and XPU
images and vLLM-Omni's CUDA image. Omni ROCm uses the published `v0.28.0` image
because upstream does not publish a ROCm `latest` tag. Intel Omni is built on
the host from the mirrored vLLM XPU `v0.30.0` base and the matching pinned Omni
source using `catalog/engines/omni-xpu.Containerfile`; upstream has no prebuilt
Omni XPU image. The first Intel Omni start needs network access and disk space
for the base image and build layers. Later starts reuse those layers. These
two pinned Omni variants are updated through changes to their manifests.
Radeon 780M (`gfx1103`) text vLLM uses a separate native image built on the
host from `catalog/engines/vllm-rocm-gfx1103.Containerfile`. Xur matches the
selected PCI address to the kernel's KFD compute target. The pinned AMD
TheRock SDK and PyTorch device packages provide native kernels, and vLLM
`v0.31.0` is compiled for the same target. The first build needs network
access and additional disk space; later starts reuse build layers. All GPUs
selected for this variant must use `gfx1103`. GPU memory checks still apply.
Xur starts this variant with `--enforce-eager`: the compiled warm-up caused
GPU hangs on the physical Radeon 780M, while eager inference passed. This
disables graph compilation and capture, so do not assume a throughput benefit
over llama.cpp. When running the native image directly, include this flag.
This native variant has its own entry on Updates and its own compatible
cached-image fallback. A failed build cannot reuse a stock ROCm image that
lacks the target. Omni continues to use its separate upstream image.
Before each model-container start, Xur refreshes the chosen image or builds the
Intel Omni layer. This includes restarting a stopped container or loading an
older saved selection. If the
image changed, Xur recreates the stopped container and keeps its model-cache
volume. Running engines continue using their current image until their next
start. If the pull fails, Xur uses the newest downloaded image for the same
engine and device variant. If no tagged base remains locally, it can reuse the
image retained by the stopped container. Model-specific Fish runtime preparation
was removed; the generic Omni channel does not supply that former integration.
Intel Omni build failures use the same compatible-image fallback; a failed first
build without a cached image fails clearly. Image downloads always use
`mirror.gcr.io`, with no Docker Hub fallback.
The workload logs record the failed pull and cached image identity. Startup
fails only if no usable image is available locally.
Model weights and launch settings remain the selected versions.

The Updates page identifies these engines as updating at container start.
Models from the loaded profile restart automatically after an exit or reboot.
Every automatic start follows the same update check and cached-image fallback.
Once the model is healthy, Xur publishes its current local port to the gateway.
Recovery leaves healthy peers running and retries failed starts after one minute.
An intentional unload keeps models stopped; saving a profile does not start it.
Load selects a profile, and Resume retries a failed manual profile change.
User-prepared containers keep their selected image and existing restart behavior.

The supported Qwen MTP recipe uses `--max-num-seqs 1`, aligned Mamba cache,
disabled prefix caching and three MTP speculative tokens. Loading or resuming a
legacy saved Qwen MTP recipe repairs the known missing settings automatically;
there is no need to reselect that model solely for this migration. This repairs the saved launch settings independently of the engine image update.

Qwen MTP passed the [September 20 live smoke tests](../releases/live-model-retest-2026-09-20.md).
Those tests do not establish Qwen3-ASR, every catalog model, or every GPU as
working. Model lab is a text-chat tester.

Gaming workstation starts the installed Bazzite Plasma desktop in its own PAM
session, Unix user and logind seat. KWin uses the selected DRM card. Persistent
users retain their home and Steam data; temporary users have separate disposable
homes. Successful workstations return after reboot.

Profiles support simultaneous workstations on distinct GPUs and users. USB
selection supports devices and hubs; display audio follows the GPU, USB audio
follows its assignment, and built-in audio belongs to the primary workstation.
See [multiple workstations](../architecture/multiple-workstations.md) for exact
matching behavior and outstanding physical acceptance tests. Separate desktops
on outputs of a single GPU remain unsupported.

Authenticated catalog APIs:

- `GET /api/catalog/search?q=Qwen&engine=llama.cpp`
- `GET /api/catalog/options?model=unsloth/SmolLM2-135M-Instruct-GGUF&engine=llama.cpp`
- `POST /api/catalog/resolve` with `model`, `revision`, `variant`, `engine`, and
  `device` (`Auto`, `CPU`, `NVIDIA`, `AMD`, or `Intel`).

Resolve returns the registered immutable recipe to use in a profile. Browser
requests require the same session and antiforgery protection as other mutations.
API clients should use an **Automation** API key for catalog resolution; see
[API keys](api-keys.md). The one-time setup code is not a lasting API credential.

Sources: [Unsloth inference defaults](https://github.com/unslothai/unsloth/tree/main/studio/backend/assets/configs),
[Hugging Face Hub API](https://huggingface.co/docs/hub/api),
[vLLM-Omni supported models](https://github.com/vllm-project/vllm-omni/blob/main/docs/models/supported_models.md).

GPU installation references: [vLLM CUDA/ROCm/XPU](https://docs.vllm.ai/en/latest/getting_started/installation/gpu/),
[Omni ROCm](https://github.com/vllm-project/vllm-omni/blob/main/docs/getting_started/installation/gpu/rocm.inc.md),
and [Omni XPU build](https://github.com/vllm-project/vllm-omni/blob/v0.30.0/docker/Dockerfile.xpu).
GPU backend support does not establish compatibility for every model or GPU
architecture. AMD and Intel serving still need acceptance tests on physical
hardware, including the Fish checkpoint shown in the reported configuration.

## Packed token embeddings

The current generic vLLM recipe does not support checkpoints with packed token
embeddings. Metadata validation rejects them before creating a recipe or launching
a saved workload. The reported `embed_tokens.weight_packed` loader error is a
runtime/checkpoint mismatch, not a slow download. Terminal initialization errors
are surfaced from engine logs during readiness checks; model containers only gain
automatic restart after passing health checks.

`lued/Qwen3.8-27B-INT8-W8A16-DFlash2` at revision
`2971c64ba386dd3faa6884cc215f67b3b2477a3e` requires its author's patched vLLM runtime
and a separate drafter. That custom runtime is not implemented here. It is not the
owner's MTP recipe, and Xur does not substitute between the two.
See the [pinned model instructions](https://huggingface.co/lued/Qwen3.8-27B-INT8-W8A16-DFlash2/blob/2971c64ba386dd3faa6884cc215f67b3b2477a3e/README.md).
