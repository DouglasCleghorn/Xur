# Xur application update 2026.09.15.4

Includes the display consoles and persistent GPU power controls introduced in
[2026.09.15.3](app-update-2026.09.15.3.md), with the Intel control corrected during
final review. Intel i915/xe now uses sustained PL1 (`power1_max`), preserving the
separate burst PL2 setting. Its default comes from `power1_rated_max`; Xur limits
requests to 1 W through that default and verifies readback.

The NVIDIA and AMD adapters, console renderer and workstation handoff behavior
are unchanged. New tests cover i915/xe PL1 writes, reboot restoration, preservation
of PL2, and rejection of zero or above-default limits.

Install through **Updates → Xur application**. No reinstall is required.
