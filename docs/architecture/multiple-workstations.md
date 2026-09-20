# Multiple workstations: development status

The published update remains **2026.09.15.7**. The changes below were made after
that package and are not published. They do not yet enable multiple workstations
through the profile loader.

## Implemented in source

- `StationDeviceInventoryReader` observes USB devices, hubs, input interfaces,
  hidraw nodes and ALSA interfaces through sysfs. The authenticated
  `/api/station-devices` endpoint exposes this inventory.
- USB identity uses vendor/product/serial when a serial exists. Otherwise it
  uses the controller and port chain, with transient USB bus numbers removed.
  Serial identity follows a device between ports. Port identity deliberately
  does not claim to follow indistinguishable devices between ports.
- `StationDevicePolicy` expands hub selections to descendants, rejects
  conflicting selections and duplicate identities, and assigns unclaimed
  input and built-in audio to the primary workstation. Display audio follows
  its GPU's sibling PCI function; USB audio follows its USB assignment.
- Every simultaneous station requires a different Unix user. Temporary users
  remain distinct by workload ID. There can be at most one explicit primary.
- Device assignments enter the runtime fingerprint in canonical order. Legacy
  fingerprints remain unchanged when no assignment object exists.
- Sunshine ports have persistent per-workstation reservations, including
  independent firewall offsets, administration addresses and Moonlight
  shortcuts. Existing single-workstation configurations reserve their old
  ports before another station can allocate them. Certificates and pairing
  state remain in each workstation's persistent directory.

The assignment contract is currently blocked at profile validation because
runtime enforcement is incomplete. Accepting it while continuing to grant
global input/audio access would silently ignore the user's choices.

## Native desktop experiment

`tests/Xur.Media.Tests/check-native-seats.py` runs only against a VM whose manifest
marks it as disposable development infrastructure. It temporarily stops Xur's
desktops, creates two test users and logind seats, starts real Plasma sessions
with separate GPU device policies, and checks that each KWin process opens only
its selected DRM card. It then stops one session and verifies that the other's
compositor and shell retain their PIDs. Cleanup removes the test users and
udev rules and restarts the agent to restore its saved workstation.

Run:

```sh
python3 tests/Xur.Media.Tests/check-native-seats.py --name editor-dev \
  --evidence .build/evidence/multiple-workstations/native-seats.json
```

This is an architecture experiment, **not** a profile-loader acceptance test.
It does not test USB hotplug, audio playback or simultaneous Moonlight streams.

## Remaining integration before removing the runtime limit

1. Replace the shared Plasma login-manager configuration with the demonstrated
   per-user, per-seat native session launch. Reconcile only the selected seat;
   stopping one must never switch another seat's VT or terminate its user.
2. Add the USB/hub and primary controls to the profile editor once they can be
   enforced. Persist selections with profiles, re-resolve them at boot/hotplug,
   and update exact device cgroup rules and logind ownership. Partial discovery
   and ambiguous identities must fail closed. Do not expose raw host storage
   merely because its USB hub was selected.
3. Route PipeWire defaults to the station's display/USB audio and reserve
   built-in/shared audio for the primary. Test unplug/replug and USB hub moves.
4. Isolate streaming input as well as video. The pinned Sunshine runtime uses
   libvirtualhid's Linux uinput and UHID backends. Each stream's virtual devices
   need an unambiguous seat identity before they are exposed to a desktop.
   The current shared `/dev/uinput` ownership policy must be replaced without
   revoking another active stream's access.
5. Integrate bounded parallel workload startup and per-workload durable errors.
   Device/GPU handoffs must retain ordering, while independent successful
   workloads continue and publish routes if another fails. Explicit unload now has per-action receipts and independent parallel stops.
   Startup and ordinary profile switching still use sequential execution.
6. Run the complete profile path with two desktops, two encrypted streams,
   physical USB/audio, reboot restoration, and independent stop/retry. Only
   then remove both one-workstation guards.

USB 2 and USB 3 branches of one physical hub may be distinct USB devices. Do
not merge them by product name; show both unless their common identity can be
established. Devices without unique serials cannot be reliably recognized after
arbitrary port moves when identical devices are present.

Upstream references used to inspect the actual session/input boundaries:
[Plasma seat management](https://github.com/KDE/plasma-login-manager/blob/master/src/daemon/SeatManager.cpp),
[the pinned Sunshine Linux input adapter](https://github.com/LizardByte/Sunshine/blob/63d35f702ee9e362e43263742981836ec0710384/src/platform/linux/input/virtualhid.cpp),
[its pinned libvirtualhid backend](https://github.com/LizardByte/libvirtualhid/blob/53e1a949fc0784af716b782ddfa6c647cafd1f05/src/platform/linux/uhid_backend.cpp).
