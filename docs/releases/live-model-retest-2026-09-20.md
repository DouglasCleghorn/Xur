# Live model retest — 2026-09-20

The initial retest used app bundle `471f61717fde`. Profile 3 preserved its
workstation and Qwen processes and published healthy routes despite Fish failing
its missing-codec preflight. Fish was then repaired and tested on signed app
bundle `da888b84c4263d55e7ec4f1435d9d58cd64f1df765618f01f8b6f05244d876c4`.

| Workload | Result | Evidence |
| --- | --- | --- |
| Qwen3.8-27B-INT8-W8A16-MTP | Passed inference | One warmup and two measured requests returned the correct arithmetic answer without request errors; 47.2 output tokens/s, 168 ms mean first token, 19,640 MiB peak VRAM per assigned GPU |
| Fish S2 Pro | Passed speech synthesis | The automatically prepared, hash-locked codec image loaded the saved model revision on one RTX 3090. Profile 3 completed and the normal authenticated `/inference/workload-9/v1/audio/speech` route returned HTTP 200 with mono 44.1 kHz PCM WAV audio. The first request generated 6.18 seconds of non-silent audio in 7.91 seconds; a second request generated 5.53 seconds in 3.93 seconds. |
| Existing workstation and Qwen | Preserved | Both retained their process IDs and instance identities across app installation and the Fish retry; this was a process-continuity check, not a new Moonlight input/audio test |

Fish's saved recipe, model revision, persistent cache, GPU assignment and API
route were retained. Its generic Omni image is now supplemented automatically
with the required codec packages; all package archives have SHA-256 hashes and
the build verifies the active versions of the base packages are unchanged.
A CPU-only codec preflight runs without network or GPU access before serving.
The locked image build and live inference were tested on this server; unrelated
Omni models do not use the Fish dependency layer.

The complete Qwen benchmark remains in Model Lab on the server. Private runtime
receipts and speech output are retained under `.build/private/`; no credentials,
server addresses, user files or live screenshots were added to source. These are
small functional smoke tests, not representative throughput or speech-quality
benchmarks. Voice cloning, streaming audio and simultaneous speech requests were
not tested in this run.
