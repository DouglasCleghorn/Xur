# Xur application update 2026.09.15.3

- Connected displays on unassigned GPUs show the shared terminal console.
  Loading a workstation releases its card; unloading restores its console
  after full teardown. Other cards keep their console process and display.
- GPU power page with per-card driver limits, wattage controls, saved settings,
  and a default reset. Saved limits restore after reboot or driver reset.
- Display diagnostics now include console startup errors.

Delivered through **Updates → Xur application**, using the configured LAN update
server. No reinstall is needed. Native console runtime and license/build receipts
are included in the signed app bundle. The existing ISO remains the previously
built artifact; its next build will include these changes through the build scripts.

Validation: fast unit/integration suite, three real Plasma load/unload cycles on
an installed QEMU VM with two DRM adapters, unchanged unassigned-console PIDs,
console screenshots, reboot/login persistence, and desktop/phone power-page
checks. Power command/sysfs and persistence tests use fixtures; physical GPU power
writes were not executed on this development machine.

See [display consoles](../architecture/display-consoles.md) and [GPU power](../usage/gpu-power.md).
