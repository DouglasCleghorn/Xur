# Multiple workstations

Profiles support simultaneous native Plasma desktops on separate GPUs, with a
distinct Unix user and logind seat for each workstation. These changes are in
source; this development pass does not package or deploy an update.

## Sessions and streaming

Each workstation starts `startplasma-wayland` under its own PAM session and
`user-UID.slice`. KWin uses that workstation's DRM card, including in headless
mode. There is no shared Plasma login-manager configuration. Stopping a desktop
terminates only its user, Sunshine and virtual monitor; it does not change VTs.
An already-running desktop from the previous login-manager implementation must
be reloaded once to move to its dedicated seat.

Sunshine retains each workstation's reserved port, certificates and paired
clients. LAN/WAN encryption remains required. Its service has an exact device
allowlist for that GPU and virtual input creation. Per-user ACLs replace global
`/dev/uinput` ownership changes. Desktop applications do not inherit access to
`/dev/uinput` or `/dev/uhid` from the user slice.

`tools/Xur.Input/seat-input.c` tags Sunshine's uinput and UHID devices before
creation. Udev matches the stable workstation seat tag, including before the
first input event. The reconciler also adds those interfaces to the matching
user slice. Unknown virtual-input identities never fall back to primary.
The interposer is compiled into the streaming bundle by `prepare-streaming.py`;
the build host needs a C compiler and Linux input headers.

## USB and audio

The profile editor offers individual devices, external hubs and one primary
workstation. Hub assignments include input, controller/hidraw and audio
interfaces of descendants. They do not grant raw USB, cameras, disks, or
controllers. USB 2 and USB 3 branches of a hub may appear separately.

Matching uses vendor/product/serial where available. Unique serials survive
reboots and moving ports. Otherwise the controller/port chain is used, without
transient USB bus numbers. Identical serial-less devices cannot reliably follow
arbitrary port moves; reconnect them to their assigned port. Duplicate serials,
overlapping hub claims and incomplete discovery do not grant ambiguous devices.
Disconnected saved selections remain visible and reserved.

The primary receives unassigned input and built-in audio. Display audio follows
its GPU's sibling PCI function, and USB audio follows its device or hub. A lone
legacy station is implicitly primary; multiple stations require an explicit
primary to receive unassigned devices. PipeWire defaults prefer available USB
sinks, then display audio, then built-in audio. Existing streams are not moved
between sinks forcibly.

`/var/lib/xur/station-seats.json` stores the full profile's peripheral intent,
including failed station starts. A two-second reconciliation loop rediscovers
nodes, refreshes exact cgroup permissions/ACLs and udev seat properties, and
records allocations under `/run/xur/seats`. Device inode changes trigger ACL
renewal after hotplug. Workstations and Diagnostics report allocation problems;
`GET /api/station-allocations` is also available with monitoring API scope.

An assignment or effective-primary change restarts the existing desktops before
transferring peripherals: changing cgroup rules alone cannot revoke an already
open device handle. Removing a secondary with no USB claims keeps an unchanged
explicit primary running. Models remain independent. A failed prior seat stop
blocks replacement of peripheral intent. Older app bundles without
`multiseat-v1` cannot be selected once the new seat manager has been used.

## Parallel profile execution

Loading and unloading use at most four pipelines. Each old workload drains
before it stops. New workloads wait for conflicting IDs/GPUs and workstation
seat handoffs. Independent starts and health checks overlap. Cache writes are
locked by artifact hash, without serializing independent model health checks.

Completed actions and individual errors are durable in SQLite. Healthy routes
become available while siblings are still starting or have failed. Unchanged
routes retain exact instance identity. Resume retries unfinished work and keeps
successful instances. Cancel stops dispatching queued actions and lets already
claimed actions finish, reporting the resulting partial state. Successful
workstations are also restored independently after reboot.

## Validation and limits

Fast tests cover profile concurrency, bounded dispatch, failure isolation,
resume/cancel, real engine processes and gateway streaming, device inventory and
policy, seat launch arguments, udev routing, fake-device input interposition,
rollback compatibility and desktop/mobile assignment controls.

`tests/Xur.Media.Tests/check-native-seats.py` remains a separate architecture
experiment. It starts two real Plasma sessions on two virtual GPUs, then stops
one and checks the other's compositor/shell PIDs. It predates the profile-loader
integration and is not an acceptance test for the whole feature.

Physical acceptance is still required: load two distinct users/GPUs through the
profile UI; pair two Moonlight clients; verify keyboard, mouse, controller and
audio isolation; move a serial-bearing hub; unplug/replug devices; reboot; stop
one desktop and confirm the other remains usable. This development environment
currently lacks permission to use `/dev/kvm`, and this pass does not alter the
live Xur server. Separate desktops on outputs of a single GPU are not supported.

References: [systemd device properties](https://github.com/systemd/systemd/blob/main/src/core/dbus-cgroup.c),
[pinned Sunshine input adapter](https://github.com/LizardByte/Sunshine/blob/63d35f702ee9e362e43263742981836ec0710384/src/platform/linux/input/virtualhid.cpp),
[pinned libvirtualhid backend](https://github.com/LizardByte/libvirtualhid/blob/53e1a949fc0784af716b782ddfa6c647cafd1f05/src/platform/linux/uhid_backend.cpp).
