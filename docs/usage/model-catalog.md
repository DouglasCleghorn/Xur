# Models and gaming workstation

Create a profile and choose Gaming workstation, or type a model name in the
Workload selector. Names, workload IDs and routes are generated automatically.
The initial catalog combines the local workstation with Unsloth’s GGUF models.
Changing Catalog exposes Hugging Face text-generation models for vLLM or the
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

Selecting a model resolves its repository commit, file hashes and engine digest.
All shards of a split GGUF are downloaded and verified before launch. Cached
weights live under `/var/lib/xur`; immutable selected recipes are stored in
`/var/lib/xur/catalog-selected`. Refreshing search never modifies an existing
profile or a running model. GGUF sampling provenance does not restart a model
when the actual runtime arguments and files are unchanged.

llama.cpp has pinned CPU, CUDA, ROCm and Vulkan images. The current vLLM and
vLLM-Omni images require NVIDIA. They invoke their real upstream `vllm serve`
commands with the selected checkpoint revision; Omni uses `--omni`. vLLM uses
tensor parallelism across the allocated GPUs. Omni uses its upstream deployment
defaults; Xur does not invent multi-stage parallelism settings. Models requiring
custom deployment files or publisher patches need dedicated recipes. The model
list is discovery, not a guarantee that every upstream architecture runs in the
pinned engine. Save an authorized Hugging Face token in **Settings** for gated
repositories and obtain any required publisher access first. Xur's catalog and
model downloaders use the saved credentials; a token does not grant access the
account does not already have. Review the linked model license before applying.
Model weights are not bundled.

`eng/engine-lock.json` records engine versions and manifest digests. Updating a
catalog entry does not update a running engine. The Updates page lists component
versions; components that cannot be upgraded separately have no independent
update button. Saved workload engine versions remain pinned.

The supported Qwen MTP recipe uses `--max-num-seqs 1`, aligned Mamba cache,
disabled prefix caching and three MTP speculative tokens. Loading or resuming a
legacy saved Qwen MTP recipe repairs the known missing settings automatically;
there is no need to reselect that model solely for this migration. This is a
specific compatibility repair, not a general engine upgrade.

For `fishaudio/s2-pro` on the supported pinned vLLM-Omni image, first load builds
and caches a hash-locked codec dependency layer. It preserves the base engine's
package versions and checks codec construction before starting the model.
The first build requires network access and can take several minutes; later
starts reuse it. Workload errors report preparation failures, with build logs
under `/var/lib/xur/fish-engine/<hash>/build.log`. A different base engine needs
an explicitly supported recipe; Xur does not silently substitute an arbitrary image.

Qwen MTP and Fish speech passed the [September 20 live smoke tests](../releases/live-model-retest-2026-09-20.md).
Those tests do not establish Qwen3-ASR, every catalog model, or every GPU as
working. Model lab is a text-chat tester; Fish uses its speech API.

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
[vLLM-Omni supported models at the pinned engine revision](https://github.com/vllm-project/vllm-omni/blob/eb11446b7f2e30ca582f8aff3afe12e9a2e66f6c/docs/models/supported_models.md).

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
