# Bazzite host integration

The selected direction is upstream Bazzite KDE Desktop with Xur installed as
separate application bundles. This replaces the former custom Fedora host and
supersedes the intermediate uCore and Aurora proposals. Storage footprint is
not a constraint; idle RAM/VRAM and exact workload isolation are.

The installer starts Xur's existing console and web setup. Anaconda installs
the unmodified, verified Bazzite payload on the approved disk, then installs
the Xur bundle, identity and service configuration under /var and /etc.
The installed system follows Bazzite's signed stable update stream directly.

The installed default target is multi-user. Display-manager, Plasma Login, SDDM and GDM are
masked persistently, preventing an automatic desktop session from claiming all
GPUs. Xur's console runs on VT3; logs use VT2. Desktop packages, Steam and
libraries remain installed. Native workstation sessions must be explicitly
launched and stopped through the agent with exact GPU and device assignment;
the agent supports multiple native Plasma workstations on distinct GPUs and
Unix users, with dedicated logind seats and device restrictions. USB input and
audio follow explicit device/hub assignments; the primary workstation receives
unassigned input and built-in audio. See [multiple workstations](multiple-workstations.md)
for supported device classes, matching rules and outstanding physical isolation
checks. Separate desktops on individual outputs of one GPU are unsupported.

The four-3090 topology remains an example, not a hardcoded distro layout.
Bazzite's NVIDIA image includes the normal AMD/Intel graphics stack as well;
real device probes and allocation rules still select exact PCI devices.
The installed image currently uses the NVIDIA variant for all machines. A
smaller non-NVIDIA payload is optional future packaging, not required for use.

Preserve the upstream image's license files. Bazzite's repository uses Apache
2.0, but that does not relicense its packaged software. Public redistribution
must account for the pinned image's individual component licenses and source
obligations. Xur branding must not imply upstream endorsement.

Relevant sources:
- https://github.com/ublue-os/bazzite/blob/main/LICENSE
- https://docs.bazzite.gg/General/FAQ/
