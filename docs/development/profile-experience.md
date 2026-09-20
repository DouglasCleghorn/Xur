# Requested profile experience

This records the broader requested profile experience. The current editor already
opens directly on creation, assigns default names and integer IDs, and presents
searchable workload/GPU selectors. The shipped single-station and external-catalog implementation is described in
`model-catalog.md`. This document retains the broader multi-seat direction.

Workstations and AI are equally central. A profile describes which workstations
and AI services should run together. Adding a workstation must be a main action.
The user should not have to create or understand a generic workload first.

The normal setup asks only for:

- AI: the model from an externally maintained catalog. GPU assignment, runtime, quantization,
  context defaults and endpoint naming come from the selected recipe.
- Workstation: the connected screen and keyboard/mouse or USB hub. Default to
  a desktop suitable for both general use and gaming, with HDMI audio selected
  automatically when available.
- Profile: a generated default name, with no name or ID entry required.

Generate internal IDs automatically and preserve them when a service is retained
or copied into another profile. Do not expose IDs, revisions, container images,
runtime flags or API routes as required setup fields. Show the usable API URL
after a model is running. Give retained services the same identity and allocation
so switching profiles does not restart them.

Show profiles as selectable sets with a short preview of what stays running,
what stops and what starts. GPU assignment should have an override, but choose
sensible defaults. Prefer a detected working NVLink pair for a two-GPU recipe;
never infer the pair from runtime GPU index order.

For the example four-3090 machine, an initial combined profile should retain the
two-GPU assistant while the other two GPUs each host a workstation. A separate
all-AI profile can use those two cards for speech services. Independent sessions
on separate outputs of the same GPU are a distinct, shared-GPU feature; do not
present them as equivalent to dedicating a whole GPU to a workstation.

## Externally maintained catalog

The owner explicitly wants a full catalog maintained by another project, with
llama.cpp, vLLM and vLLM-Omni support. A short Xur-maintained list is not the
requested product. The model-selection experience should work like Unsloth:
browse, choose a model and add it to the desired running set.

The selected GGUF settings source is Unsloth's model and sampling defaults.
For vLLM, prioritize the exact checkpoint publisher's serving settings, then
matching entries in the upstream vLLM Recipes catalog, then engine defaults.
Use publisher generation_config.json for sampling when provided. For
vLLM-Omni, use upstream model deployment configurations and matching Recipes
entries. Resolve settings to immutable revisions before applying them;
publisher shell snippets require review and translation into typed settings.

An additional catalog integration to investigate is [LocalAI's model gallery](https://localai.io/docs/models/).
Its [backend reference](https://localai.io/docs/reference/index.print.html#model-compatibility-table)
lists all three required engines. The upstream repository includes real
`backend/python/vllm-omni` and gallery-importer code. Keep Python vLLM distinct
from the separately listed vllm.cpp backend.

Reuse upstream catalog and backend integration rather than translating a large
collection of engine-specific settings into independently maintained Xur
recipes. The normal UI chooses an appropriate engine automatically; expose an
explicit llama.cpp / vLLM / vLLM-Omni override under Advanced when the model has
compatible builds. The three engines are not interchangeable for every model.

LocalAI's catalog is a source of community model configurations; the underlying
model weights are maintained by their publishers. Its published support table
is not evidence that every entry works on every Xur host. Xur still owns the
integration: device allocation, stopping and release verification, topology,
profile continuity, runtime versions and working resource limits.

Before adopting the runtime, prove that catalog refresh and model installation
cannot unload unchanged profile workloads. Isolate engine workloads as needed,
keep model downloads in shared intentional /var storage, retain resolved model
revisions and engine digests, and avoid hot-updating running deployments merely
because upstream changed. A downloaded configuration is not permission to run
arbitrary privileged commands or mount unassigned devices.

Retain the owner's Qwen3.8 INT8 W8A16/BF16 MTP, Fish S2 Pro and Qwen3-ASR choices
as reference deployments and favorites, not the entire available catalog. Do
not substitute other checkpoints automatically. The selected Qwen checkpoint's
upstream card currently describes a vLLM pin with concurrency and prefix-cache
issues; resolve these before enabling affected settings. Its published GPU
measurements use PCIe without NVLink, so its P2P-disable settings must not be
copied automatically into the owner's NVLink deployment.

Unsloth remains a useful source of model artifacts and interaction patterns.
Its [Studio documentation](https://unsloth.ai/docs/new/studio) describes local
llama.cpp/Hugging Face inference and connecting to a vLLM provider, which is
not by itself a complete managed deployment path for all three engines.

Live Unsloth/Hugging Face search and the pinned vLLM-Omni supported-model list
are now integrated directly. LocalAI was investigated but is not used. Recipe-
specific vLLM tuning, Omni deployment configurations, gated downloads, and
multi-seat device selection remain beyond the current implementation.
