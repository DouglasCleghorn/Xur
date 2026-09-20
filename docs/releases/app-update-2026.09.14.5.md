# Xur app update 2026.09.14.5

Install from **Updates → Check for updates → Update Xur** using
`192.168.0.134:8088` as the update server. This is an application bundle update;
it does not require installing the OS again.

- Profiles uses compact rows with Load, Edit, and More actions. More contains
  Duplicate and Delete. Delete removes a saved profile; unload the loaded profile
  first. Unload stops its workloads and retains its saved definition and user data.
- Workstations offer existing users, Temporary user, and Add user. Named accounts
  retain their homes across profile switches. Temporary accounts start fresh and
  are removed after their workstation stops. Account selection is checked against
  the actual Linux username and UID.
- Storage shows filesystem capacity, folder usage, and whole disks, including
  unmounted disks. Folder measurements run in the background and can be refreshed.
- GPUs lists detected adapters with their running workload assignments. VRAM and
  power charts remain visible, with 15-minute, one-hour, and 24-hour ranges.
  Details includes other available sensors and process ownership. Sampling is
  approximately every 15 seconds; unsupported readings say Not reported.
- Desktop layouts are denser; phone layouts retain actions in More and expandable
  details. The interface uses shared typography, control sizing, and colors.

The signed bundle declares its workstation-user feature. Activation rejects an
older bundle that would misinterpret user selections in saved or active profiles,
unfinished transitions, or workload receipts. This check runs before stopping any
service and again after requests and transitions drain.

Validation includes installed-VM signed updates with unchanged workload PIDs and
browser sessions, real llama.cpp inference and Plasma lifecycle, persistent and
temporary account behavior, delete/unload, live disk usage and DRM inventory,
history accumulation, authentication, and desktop/tablet/phone layouts. NVIDIA
XML and native sensor parsing use unit fixtures. Four-card screenshots are labeled
browser layout fixtures, not measurements from a four-GPU machine.

Generic containers, Dockerfile builds, and named-volume management are proposed
separately in [Container workloads](../development/container-workloads-proposal.md).
