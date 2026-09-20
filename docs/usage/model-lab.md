# Model lab

Included in application update 2026.09.18.3.

Open **Models → Benchmark & test chat** or **Model lab** in navigation. Load a
llama.cpp or vLLM text-chat workload first. Only running, published endpoints
are offered; arbitrary URLs and unloaded downloads are not test targets.

## Benchmarks

Choose a prompt, request count (1–64), concurrency (1–8), output token limit
(1–4,096), temperature, and optional warm-up. One benchmark runs at a time;
individual requests have a three-minute limit and the run has a fifteen-minute
limit. The warm-up response is retained but excluded from summary metrics.

Results are written atomically to `/var/lib/xur/benchmarks/<id>.json`, with
owner-only permissions. They survive page navigation, profile changes and
reboots. On startup, unfinished runs become Interrupted and retain partial data.
The page lists the latest 100 runs; older files remain on disk. JSON export
includes the original settings and all recorded context:

- Model recipe, pinned repository revision or model files, immutable engine
  image, launch arguments, workload fingerprint and instance identity.
- Profile, Xur bundle, GPU hardware, and other workloads at the start and end.
- GPU VRAM, utilization, power, driver version and process readings approximately
  every two seconds, with simultaneous workload snapshots.
- Individual timing, server input/output token counts, finish reason, response,
  reasoning output, and request failures.

Time to first token is measured at the first nonempty content/reasoning chunk,
not at a role-only event. Output throughput is server-reported completion tokens
divided by the request wall-time interval, including concurrent overlap. It is
not decode-only speed or a quality evaluation. Missing token counts are unknown;
chunks are never treated as tokens. Summary throughput is unavailable if measured
requests fail. Peak VRAM is a **sampled** peak and can miss short spikes.

The same prompt is reused. Prompt caching, warm-up, concurrent users, other
workloads and measurement overhead affect results. Compare runs with matching
settings and context. A route/instance change stops or rejects subsequent
requests instead of silently following a different model.

A benchmark keeps the application's update guard active until it finishes.
Cancel run stops in-flight requests and saves completed ones. Reboots can still
interrupt a run. Container environment variables are not copied into snapshots.

## Test chat

The native Razor/JavaScript page supports multi-turn messages, streaming text,
separate reasoning, a system prompt, output limit, temperature, stop, clear,
and copy. It uses the existing authenticated HTTPS app and private inference
gateway, with no additional public listener or external service. Output is
rendered as text; model-provided HTML is never executed. Conversations remain
only in the current tab. An interrupted answer is not added to subsequent context.

For a richer React chat UI, assistant-ui is an option:
https://www.assistant-ui.com/docs . The current basic tester keeps Xur's existing
frontend and has no new browser dependency or CDN requirement.

## Validation

`bash eng/test-fast.sh` includes streamed fixture-engine tests, real HTTP model
lab API tests, gateway route-pinning tests and desktop/mobile Playwright checks.
Fixtures verify cancellation, restart recovery, partial/error results, unknown
usage, context retention, output escaping and responsive controls. These are
functional checks, not benchmark measurements of the user's physical GPUs.
