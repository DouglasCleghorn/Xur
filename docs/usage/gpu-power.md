# GPU power

Open **GPU power** in the web manager, or use **Power limits** from GPU monitoring.
Each detected card shows its current limit and saved setting. Supported cards
have a wattage input, the driver's minimum/maximum/default, **Save limit**, and
**Use default**. Unsupported cards explain why there is no control.

Settings belong to the machine, independently of profiles. They are saved in
`/var/lib/xur/gpu-power.json` (root, mode 0600), retained through app/OS updates,
and checked at agent startup and every 30 seconds. Driver resets are reconciled
too. A saved default follows the default reported by the driver after reboot.
A failed write or readback remains visible; the saved intent is retried.

NVIDIA uses `nvidia-smi` to query the PCI device's UUID, limit, default and bounds,
then writes by UUID and verifies the reported limit. AMD uses GPU-local `hwmon` `power1_cap` with reported bounds and a default.
Intel i915/xe uses the card’s sustained PL1 control, `power1_max`, and
`power1_rated_max` for its default. Xur accepts at least 1 W and limits Intel
requests to the rated default; it does not invent driver-reported bounds or
change the separate PL2 burst limit. The driver validates the requested limit. CPU/package power controls are not used for integrated GPUs.

Authenticated API:

- `GET /api/gpu-power` returns observed controls and saved settings.
- `POST /api/gpu-power` accepts `{ "pci": "0000:01:00.0", "identity": "NVIDIA:GPU-…", "watts": 275 }`.
- Set `watts` to `null` to save the driver's default. Read the actual identity
  from GET; stale/replaced/duplicate identities and invalid limits are rejected.
- Cookie requests also require the normal antiforgery token; bearer API sessions
  use the existing API authentication flow.

The Intel interfaces follow the kernel’s [i915 ABI](https://raw.githubusercontent.com/torvalds/linux/master/Documentation/ABI/testing/sysfs-driver-intel-i915-hwmon)
and [xe ABI](https://raw.githubusercontent.com/torvalds/linux/master/Documentation/ABI/testing/sysfs-driver-intel-xe-hwmon).

The automated suite covers NVIDIA command requests/readback, AMD/Intel sysfs
controls, validation, replacement identities, failed writes, and restoration by
a fresh agent. Physical power-limit writes still need to be exercised on the
owner's GPUs; virtual display adapters expose no adjustable power control.
