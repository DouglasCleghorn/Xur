# Historical backlog — September 15, 2026

This is an archived planning snapshot, not the current implementation checklist.
Later work added multiple workstation runtime support, model benchmarking, engine
version display, HF credentials and tested Tailscale/Moonlight access. USB identity
and allocation policy code exists, but the full peripheral assignment interface and
host integration still need completion. Separate outputs on one GPU and shared-GPU
AI scheduling remain future work.

For the current identity/update design and its remaining hardware validation, see
[workstation identities](../usage/workstation-identities.md) and
[online installer](../architecture/online-installer.md).

Reviewed against source on 2026-09-15. This distinguishes missing product behavior
from behavior implemented but not yet exercised on the four-3090 machine.

## Still to implement

- Multiple simultaneous native workstations. The planner and agent currently
  enforce one station; the runtime uses one Plasma login configuration.
- Persistent USB hub/port/device selection and per-station input/audio routing.
  The current station permits all local input, ALSA and hidraw devices.
- Independent desktops on different outputs of one GPU. A whole selected card
  and its connected outputs currently belong to one desktop.
- The complete reference AI set: Qwen INT8 W8A16 with BF16 MTP on the NVLink pair,
  Fish S2 Pro through vLLM-Omni, and Qwen3-ASR through core vLLM on the other pair.
  Discovering a model in search is not a model-specific deployment recipe.
- NVLink-aware automatic allocation, speech co-residency and measured shared-GPU
  scheduling. NVLink discovery and selection hints are implemented. GPU allocations are currently exclusive, so the requested Fish/ASR
  capacity-sharing behavior is absent.
- Per-model vLLM tuning and Omni stage configurations from reviewed upstream
  recipes; driver/architecture compatibility checks beyond file format and the
  current engine/device checks; gated-model credentials and license acceptance.
- Clear installed/available/running engine versions and separate llama.cpp,
  vLLM and vLLM-Omni Update buttons. Images are pinned internally; application
  updates preserve the selected engine image.
- vLLM ROCm and Intel XPU engine choices. llama.cpp has CPU/CUDA/ROCm/Vulkan image
  choices; vLLM and Omni currently choose NVIDIA images.
- A managed benchmark recipe with measured GPU baseline return. Sunshine is now
  tied to workstation start/stop; headless capture on the physical GPUs remains
  to be exercised.
- Container volume deletion/export, richer build contexts, generic WebSockets
  and a prepared Breeze TTS 2 recipe. Podman image pulls, text Dockerfile builds,
  persistent volumes and storage accounting are already implemented.
- OS reboot scheduling and automatic failed-boot recovery. Automatic OS staging,
  explicit reboot and explicit rollback are already implemented.

## Implemented, with physical end-to-end checks remaining

- Selected GPU restriction: KWin receives one DRM card and the user's device
  policy restricts GPU access. Direct rendering/scanout on each physical 3090,
  HDMI audio and absence of cross-GPU frame copies still need observation.
- GPU monitoring has real NVIDIA and sysfs readers and history graphs, but the
  four-card physical run has not taken place.
- Model/workstation profile continuity passes installed VM tests with real
  llama.cpp, Podman and Plasma. The owner's complete GPU workload set has not run.
- Tailscale QR and browser authorization startup work; completed owner enrollment
  and access through its tailnet URL have not been tested here.

## Persistent USB recommendation

Represent each physical desk as a persistent assignment, separate from its Linux
user account and from transient device numbers. A profile's workstation selects
that desk plus a user (or temporary user). The user's home retains Steam logins;
the desk retains its display and peripheral assignment.

Default to assigning a USB hub or physical port subtree. Match controller PCI
identity plus USB port chain, not bus/device numbers or `/dev/input/eventN`.
Optionally match an individual device by vendor/product plus a unique serial so
it can follow that device between ports. Ambiguous or missing devices remain
unassigned rather than selecting a similar device.

Use udev/logind seat assignment with explicit per-user device access and hotplug
reconciliation. Include all interfaces of composite devices and hub descendants;
handle USB audio, controllers/hidraw, cameras and removable storage permissions
explicitly. Seat labels alone do not isolate every USB device class. Prevent two
active desks from owning the same peripheral, and preserve assignments while a
station is stopped. A temporary user's deletion must not delete the desk mapping.

Systemd supplies persistent seat attachment through
[loginctl attach](https://github.com/systemd/systemd/blob/main/man/loginctl.xml).
Xur still needs the selection UI, stable matching and runtime enforcement above.

## Added in application update 2026.09.15.3

Unassigned displays now mirror the terminal console, with selected-card handoff
and return after desktop teardown. Per-GPU power controls persist through reboot
where the driver exposes supported limits. See `display-consoles.md` and
`gpu-power.md` for behavior and test coverage.

## Current workstation and model update

See `workstations-and-models.md` for encrypted streaming, disconnected display
selection, model inventory and persistent caches, network charts, NVLink hints,
email usernames and GPU ownership diagnostics.
