# Application update 2026.09.18.3

Adds Model lab with saved performance benchmarks and a basic streaming LLM chat.
Benchmark exports retain model revisions, launch settings, running workloads,
GPU readings, timing, token counts and responses. Includes cancellation and
recovery of interrupted runs. Accessible from Models and desktop/mobile navigation.

Fixes headless workstation input by retaining KWin's DRM/libinput backend and
creating a virtual monitor within the normal desktop session. Loads uinput
before configuring device access and checks streaming input permissions.

Existing running desktops are preserved during application updates. To apply
this workstation fix, unload and reload its profile after installing the update.

The release pipeline verifies signed activation in the disposable installed VM,
workload/login continuity, reboot persistence and desktop/mobile checks before
publication. Headless keyboard and mouse injection passed in a VM; physical RTX
3090/Moonlight validation and real GPU benchmarking remain to be done.
