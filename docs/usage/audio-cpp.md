# audio.cpp

Add a workload in **Profiles**, choose **audio.cpp · Audio models**, and select
its CPU, NVIDIA, AMD or Intel preset. GPU presets reserve one GPU; select it in
the editor. Load the profile, expand the running workload's **Details**, then
choose **Open audio.cpp**. The native WebUI downloads complete model packages and
provides speech synthesis, transcription, music and other supported audio tasks.

The presets use upstream's rolling `full-cpu`, `full-cuda12` and `full-vulkan`
images from GHCR. AMD and Intel use Vulkan. Backend and model coverage varies;
select a model supported by your backend and available memory. Xur reserves the
GPU but cannot estimate memory before you select a model inside audio.cpp.
There is no model splitting across GPUs. A successful health check means the
server is ready; model loading and inference can still fail separately.

Downloads live in the Models storage location under `models/audio.cpp/<workload-id>`
and are mounted writable at `/app/models`. Keep the WebUI's model directory at
that default. Model packages and the container user's home/cache/config survive
restarts and image replacement, and follow Xur's Models storage migration.
Recreating a workload with a different ID gives it a new directory; the original
data is retained. WebUI history and saved voices held in browser storage belong
to that browser. Export important audio outputs explicitly.

Xur checks the rolling image channel on startup and leaves running workloads
alone. If a download fails, an available cached engine can run with a visible
warning. The first start and model downloads require internet; the bundled WebUI
assets and already downloaded models can be used offline. No Xur release is
needed for engine updates. Upstream updates can change model compatibility.

The page uses the signed-in manager session and Xur's CSRF protection. Audio APIs
are available at `/inference/<route>/v1/` through the existing gateway and its
API-key authorization. Use audio.cpp model IDs and audio endpoints, rather than
the text-only Model Lab or coding-provider integrations.

See upstream's [container documentation](https://github.com/0xShug0/audio.cpp/blob/main/docs/docker.md)
and [server API](https://github.com/0xShug0/audio.cpp/blob/main/app/server/api.md)
for supported models and request formats. audio.cpp is Apache-2.0 licensed,
copyright ShugoAI LLC; its dependencies and model weights retain their own
licenses. Review model terms before downloading or using them.
