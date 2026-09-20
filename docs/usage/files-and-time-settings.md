# Files and time settings

The Files page opens with local mount points, their free space, and a bounded folder-size explorer. It also lists workstation homes, including hidden folders, and downloads
regular files as attachments. Workstations also link directly to their user's
home. A filter limits folder listings to 500 matches. Downloads stream without
buffering the complete file in the manager. The helper runs as the workstation
Unix user, opens paths relative to directory descriptors, rejects traversal and
symlinks, and refuses device files and FIFOs. Existing manager authentication
protects every endpoint. Steam log files can be downloaded from the workstation
home after enabling `PROTON_LOG=1 %command%` for a game.

Settings now includes NTP enable/disable, preferred servers, and synchronization
status alongside the global timezone. New NTP configuration prefers time.cloudflare.com. Automatic timezone uses ipwho.is at boot or through the Settings refresh button, retaining the previous zone when lookup fails. Existing saved manual settings remain manual. Chrony configuration is validated before
service changes, preserves existing OS/DHCP fallbacks, and rolls back on failure.
Settings persist in /etc/chrony.conf and /etc/chrony.d/xur.conf. Workloads share
the host clock; timezone changes require reloading running workloads to apply
throughout their processes.

NVIDIA workstation startup loads the modeset and UVM modules before resolving
its device allowlist. If loading the modules leaves modeset or UVM nodes absent,
startup uses `nvidia-modprobe --modeset` and
`nvidia-modprobe --unified-memory --create-nvidia-device-file=0` to create them.
The latter minor is the shared UVM base, not a GPU index. Missing required nodes
after initialization produce a visible startup error;
only the selected card's device minor and shared NVIDIA control nodes are
allowed. Graphics reports include a separate NVIDIA-only Vulkan probe. This
addresses the missing modeset node observed in the supplied report, but does
not establish which driver caused the Vulkan crash or prove games now render.
Physical RTX 3090 game and streaming validation remains necessary.

Tests cover file traversal, symlinks, special files, exact binary downloads,
bounded listings, authenticated endpoints, desktop/mobile navigation, Chrony
configuration rollback, and NVIDIA device initialization order. Packaging also
checks real downloads and NTP configuration through the installed test VM.

Prep follow-up: loading modules alone did not create /dev/nvidia-modeset on the
reported host. Regression tests now model this explicitly: only the NVIDIA
helper creates the nodes; modprobe and udev settling alone do not. They also
cover helper failures, missing helper, successful helpers that leave nodes
missing, repeat startup, and exclusion of other GPUs. This prep change has not
been packaged.
