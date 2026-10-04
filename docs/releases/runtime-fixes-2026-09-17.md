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
