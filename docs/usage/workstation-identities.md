# Named workstations and Moonlight

In a profile's workstation row, select **New workstation**, enter a name and pick
a user. In another profile, select that same named workstation and choose its GPU.
The workload ID, Sunshine state directory, certificate, paired clients and port
remain the same. GPU allocations still belong to profiles.

Existing IDs are adopted without changing their pairing files. Separate existing
IDs are not automatically merged: select the identity already paired in Moonlight
in each profile where you want to reuse it. Changing GPUs restarts the desktop;
it cannot move a running game between GPUs. Moonlight may need to reconnect but
should not need to pair again. A rename is global and reaches Sunshine the next
time the workstation starts. Different users require separate identities.

Deleting a profile retains its named workstation identity and pairing data, so it
can be selected again later. Temporary-user identities retain pairing, but their
disposable desktop data still follows the temporary-account lifecycle. Multiple named workstations can run together with distinct GPUs and users. See
[multiple workstations](../architecture/multiple-workstations.md) for assignment
rules and the remaining physical validation.

Manage entries on **Workstations → Manage workstations**: create a workstation,
rename it for all profiles, or delete an unused entry. Remove profile references
and unload it before deletion. Deletion keeps user files and pairing data; IDs
are never reused. Existing entries in the profile editor only select the
workstation. Name and user fields appear when creating a new entry.

USB selection shows the reported serial and port. Duplicate serials are flagged;
matching vendor/product/serial identities cannot safely be assigned separately.
Use a hub with a unique serial or different devices in that case.

The server blocks system sleep, while physical workstation displays can dim and
turn off when idle. Headless desktops keep their virtual streaming output awake.
Switching back to a physical display restores display idle behavior on startup.

GPU labels such as **GPU 1** persist in `/var/lib/xur/gpu-labels.json`. NVIDIA UUIDs
keep the label attached to the same card across PCI changes; adapters without a
hardware UUID use their bus address. Replacing a UUID-bearing card gets a new
number, and removing one never renumbers the others. PCI addresses remain the
actual profile assignment, so review assignments after physically moving cards.
