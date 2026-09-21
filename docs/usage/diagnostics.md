# Display diagnostics

Open **Diagnostics** in the web manager and choose **Download report** (JSON) or **Copy report**. The report
includes the running control/agent bundle IDs, kernel, DRM cards and connectors,
framebuffer devices, display PCI devices, VMBus bindings, and Hyper-V module
availability, device owners, and NVLink topology. The report also includes the
exact output and exit status of these fixed read-only commands:

```text
nvidia-smi --query-gpu=index,pci.bus_id --format=csv,noheader,nounits
nvidia-smi topo -m
nvidia-smi nvlink --status
```

For streaming failures it also records the NVIDIA driver version, UUID/PCI
mapping, and each GPU's `/proc/driver/nvidia/gpus/<PCI>/information` record,
including its actual device minor. GPU index and device minor may differ.

No SSH login is needed. Run collection on the affected machine after updating,
then share the downloaded JSON. A failed command remains visible as a failure;
it is never treated as proof that an NVLink bridge is absent. Each inventoried GPU has the exact card/output checks that decide
whether it appears in the workstation picker.

This distinguishes a missing driver, framebuffer-only display, disconnected
connector, discovery failure, and a client running an older app version. The
report uses fixed read-only probes and includes no login token, kernel command
line, network configuration, user files, or general journal dump. The page does
not reload itself while text is selected. The manager uses HTTPS, including on the LAN; manual selection remains available
when the browser blocks clipboard access.

Authenticated automation can retrieve the same JSON at
`GET /api/diagnostics/display`. Anonymous requests are denied.

Choosing **Workstation** in the profile editor automatically selects the gaming
desktop and shows the GPU field. Choose an existing named workstation to retain
its user and pairing, or **New workstation** to supply a new identity. Manage
those identities on Workstations. If no GPU passes the picker checks, the editor
links directly to Diagnostics.

## Game rendering

A running workstation has a **Graphics report** link. It runs `vulkaninfo --summary`
and, when Xwayland publishes DISPLAY, `glxinfo -B` as the workstation user inside
that user's device-restricted slice. It also reports graphics package versions and
architectures, device policy, and an allowlisted set of graphics environment
variables. No full environment, game files or authentication tokens are collected.
The authenticated read-only endpoint is `/api/workstations/<id>/graphics`.
A failed or timed-out probe remains visible in the report; it is not a passing
rendering test. A successful 64-bit host probe does not verify the 32-bit Steam
runtime or game compatibility.

For a game that exits or renders a blank screen, use the Steam Launch Option
`PROTON_LOG=1 %command%`, reproduce once, then remove it. Review and share the
`steam-<appid>.log` in the workstation user's home alongside the graphics report.
See [Valve's Proton logging instructions](https://github.com/ValveSoftware/Proton/wiki/Proton-FAQ).

Workstation launch configuration explicitly selects NVIDIA's GLX vendor. Mesa
workstations select their GPU by PCI identity using DRI_PRIME. Device restrictions
continue to enforce assigned GPU access. No NVIDIA Vulkan selection is guessed
from card names or runtime indices; several cards can have identical names.
The owner subsequently confirmed that game rendering, Moonlight input and sound
worked on the RTX 3090. This does not establish compatibility for every game or
multi-workstation configuration. For a new regression, collect fresh reports and
download the Proton log through **Files → Workstation files**; see [Files](files.md).
