# GPU monitoring

GPUs appears in the desktop navigation and More on phones. Every detected
PCI/DRM display or compute adapter is listed by stable identity, including Hyper-V
VMBus displays. Assigned workload names and states come from the observed runtime,
not from a saved profile that has not been loaded.

Current utilization, VRAM, power, and temperature are prominent. VRAM and power
history are always visible. Details contains driver/device information, fans,
clocks, performance state, process observations, and utilization/temperature
history. The shared selector offers 15 minutes, one hour, and 24 hours. Pointer
inspection shows each sampled value and timestamp. Missing samples break the line.

The agent samples about every 15 seconds and checkpoints 24-hour history every
minute under `/var/lib/xur/gpu-history.json`. API time ranges filter the history
sent to the browser. Polling pauses when the browser tab is hidden. Identity uses
PCI or VMBus/sysfs paths, never a transient GPU index. A changed device list causes
a page refresh; reading/assignment changes update in place.

NVIDIA uses the real `nvidia-smi --query --xml-format` output. AMD/Intel and other
DRM devices expose available values through native sysfs/hwmon attributes. Values
that the driver does not report remain null/Not reported, including virtual
adapters with no dedicated-memory or power sensor. NVIDIA process memory comes
from vendor output; DRM process ownership also inspects real open device nodes.
Only process names from `/proc/<pid>/comm` are displayed, never command arguments.

`GET /api/gpus?minutes=15|60|1440` is authenticated. Installer mode disables it.

Tests cover NVIDIA XML variants, native sensor unit conversion, missing values,
and stable PCI matching with fixtures. Installed VM tests cover actual DRM
inventory, workstation assignment, history sampling, authentication, and responsive
layouts. Four-card chart screenshots are explicitly browser-only layout fixtures;
they are not physical GPU measurement evidence.

Reference: [NVIDIA System Management Interface](https://docs.nvidia.com/deploy/nvidia-smi/).
