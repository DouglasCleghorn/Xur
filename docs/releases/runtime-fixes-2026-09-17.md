# Runtime preparation, 2026-09-17

These are source changes, not a published update.

## Sunshine

The reported headless session successfully captures Virtual-0, then fails opening `/dev/dri/card2` for CUDA encoding. Its subsequent Vulkan fallback crashes. The log proves the denied open, but does not by itself prove which PCI device card2 names on that boot.

The workstation user slice already restricts devices; headless users also need Unix permissions, since they do not receive the active-seat logind ACL. Xur now grants per-user access only to the observed assigned DRM card/render nodes, records previous user entries, and restores those entries after workstation teardown. Receipts are bound to the boot and device major/minor. It does not add users to the broad video group or grant CAP_SYS_ADMIN. A real open check runs in the same user slice before Sunshine starts. A remaining cgroup, driver mapping, or SELinux denial must be diagnosed rather than opening other GPUs.

Unit tests cover grant scope, retry receipts, prior-entry restoration, boot changes and error classification. Physical NVENC streaming still needs a retry with the prepared build; it has not been reproduced on this development host.

## Qwen MTP

Reported vLLM 0.29.0 loaded the weights, but CUDA graph setup required 256 Mamba cache blocks and only 115 were available. The saved command also had no speculative config.

New generic vLLM recipes explicitly cap max-num-seqs at 16. The exact `lued/Qwen3.8-27B-INT8-W8A16-MTP` recipe uses one sequence, three speculative tokens, aligned Mamba cache and disabled prefix caching. Context remains 4096 and memory utilization 0.85 pending measurement. These are conservative test settings, not measured throughput claims. No model weights or engine digest were changed.

The [model README at the selected revision](https://huggingface.co/lued/Qwen3.8-27B-INT8-W8A16-MTP/blob/7c12373712d1363e2b76655cb3332c9c124627d7/README.md) documents MTP, aligned cache, and caveats for concurrent requests and prefix caching. Its reference runtime differs from Xur's current engine pin; inference must be tested on Xur's engine before claiming compatibility.

Saved recipes remain immutable. Re-select the model in the workload editor and save/load to adopt the new recipe. Old Qwen recipes omitting concurrency or speculation now stop with that instruction instead of repeatedly attempting the same bad command.

## Fish S2 Pro

The root failure is `ModuleNotFoundError: No module named 'fish_speech'`. The [pinned vLLM-Omni instructions](https://github.com/vllm-project/vllm-omni/blob/eb11446b7f2e30ca582f8aff3afe12e9a2e66f6c/examples/online_serving/text_to_speech/README.md) require this extra dependency.

`eng/build-fish-engine.sh` prepares a local derived engine image from the existing digest. The Fish wheel is hash-pinned. The build installs codec dependencies with constraints preserving the base environment, initializes the DAC codec on CPU, and records a dependency install report and package list. This preparation script does not publish or change the catalog. Transitive dependencies still need locking from that report, the image needs a registry manifest digest, and actual TTS requests need testing before catalog selection can use it. No weights are included.

The current generic Omni image remains unchanged. A CPU-only, network-disabled codec preflight now detects missing dependencies before starting Fish with GPU access. This improves failure reporting; it does not supply the missing engine image.
