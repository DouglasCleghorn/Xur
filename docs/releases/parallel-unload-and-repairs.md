# Parallel unload and workstation prep

These are source changes only. No update package or repository pointer was changed.

Explicit **Unload** now runs up to four independent workload pipelines at once.
Each pipeline drains that workload's requests before stopping its exact recorded
instance. A failed drain or stop blocks only its own pipeline. Other workloads
continue, and the UI records each completed action and labels each error by
workload. Final empty-route publication and clearing the active profile happen
only after every stop succeeds.

SQLite journals retain exact action receipts, including actions completed out
of order. Resume skips successful actions and retries unfinished actions. Old
journals with only a contiguous completion count still load. Cancellation waits
for claimed actions to finish and prevents subsequent actions from starting;
it does not forcibly interrupt active requests. Starts and ordinary profile
switches still use their existing sequential execution.

The agent permits disjoint stop operations concurrently while excluding new
starts and streaming restarts until all stops and GPU-release checks finish.
Overlapping GPU allocations and the currently shared workstation seat serialize.
This does not remove the one-workstation runtime restriction.

## Saved Qwen recipe repair

Loading a saved `lued/Qwen3.8-27B-INT8-W8A16-MTP` vLLM recipe that lacks the
concurrency limit or MTP configuration now repairs its launch settings. The
repair adds single-sequence concurrency, aligned Mamba caching, disabled prefix
caching and three-token MTP speculation when absent. It retains the checkpoint
revision, engine image, assigned GPUs, workload ID and persistent cache identity.
Already configured recipes and unrelated models are unchanged.

Preview saves a new profile revision before making its plan. Resume of a failed
legacy launch also repairs the recipe and replans from observed instances, so
an old failed container is stopped before starting its replacement. Saving the
repaired profile and replacement journal is atomic. Historical profile revisions
remain intact. Catalog verification accepts only the exact installed recipe or
its deterministic repair, not arbitrary command/image changes.

## Workstation network prompt

The supplied dialog text matches NetworkManager's
`org.freedesktop.NetworkManager.network-control` action in the installed Bazzite
policy. Its default for an unassociated session requests administrator
authentication; an automatic desktop request can therefore display a root
password prompt in a headless workstation.

Before starting a managed workstation, Xur installs a per-account polkit rule
that declines this one host-network-control action without an authentication
challenge. It removes the rule when that workstation stops. The rule does not
stop NetworkManager, disconnect networking, grant privileges, disable the
polkit agent, or change other action policies. Connection activation/deactivation
from that workstation is consequently unavailable; host networking remains
managed outside the workstation. This is not a claim that the underlying
unresponsive keyboard/mouse issue has been reproduced or repaired.

Validation: process tests use real HTTP child engines, real streaming requests,
the gateway and SQLite recovery to check parallel drains, failure isolation,
bounded concurrency and cancellation. Unit tests exercise agent locking and
catalog migration. The generated policy was also checked against the actual
polkit service in the disposable Bazzite VM: network-control changed from an
admin challenge to a refusal without a challenge; the separate system-settings
action retained its original policy. The test removed its temporary account and
rule afterward. This does not test the user's physical 3090 or Moonlight input.
