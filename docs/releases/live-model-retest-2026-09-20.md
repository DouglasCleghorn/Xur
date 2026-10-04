# Live model retest — 2026-09-20

The initial retest used app bundle `471f61717fde`. Profile 3 preserved its
workstation and Qwen processes and published healthy routes.

| Workload | Result | Evidence |
| --- | --- | --- |
| Qwen3.8-27B-INT8-W8A16-MTP | Passed inference | One warmup and two measured requests returned the correct arithmetic answer without request errors; 47.2 output tokens/s, 168 ms mean first token, 19,640 MiB peak VRAM per assigned GPU |
| Existing workstation and Qwen | Preserved | Both retained their process IDs and instance identities across app installation and a subsequent profile retry; this was a process-continuity check, not a new Moonlight input/audio test |

The complete Qwen benchmark remains in Model Lab on the server. Private runtime
receipts are retained under `.build/private/`; no credentials,
server addresses, user files or live screenshots were added to source. These are
small functional smoke tests, not representative throughput benchmarks.
