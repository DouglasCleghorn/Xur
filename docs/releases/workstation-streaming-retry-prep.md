# Workstation streaming retries (prep)

The September 18 report shows the new virtual monitor starting and KWin/PipeWire
capture reaching streaming. NVENC then fails at OpenEncodeSessionEx with
unsupported device (2). The Vulkan fallback subsequently crashes. This log does
not identify the cause of the NVENC failure or prove an unsupported physical GPU.

The Graphics report now tests host FFmpeg H.264 NVENC with 30 synthetic frames,
the assigned GPU UUID and the existing workstation user/device slice. It retains
the exit status and output, plus NVIDIA index/PCI/UUID/minor/driver mapping and
encoder sessions. No device permissions are widened. Missing FFmpeg/encoder
support remains a visible probe limitation; this is not Sunshine's bundled codec.

A similar multi-GPU driver failure under restricted device access is reported in
https://github.com/NVIDIA/nvidia-container-toolkit/issues/1249. It is a hypothesis
for this machine, not a confirmed diagnosis. Current driver/device diagnostics
and a direct encoder result are still needed. Do not remove GPU isolation or
grant CAP_SYS_ADMIN based solely on these logs.

Retries now retire stale transient child services before replacing an inactive
desktop service, resetting failure state and waiting for systemd collection.
Streaming services use --collect. Active desktop units and administrator-written
unit fragments are preserved. Unit tests cover these boundaries; a real user
systemd test reproduces the already-loaded error and verifies two recreate cycles:

```sh
dotnet run --project tests/Xur.Unit.Tests -- --station-units-smoke
```

These are source changes only; no new update has been published.
