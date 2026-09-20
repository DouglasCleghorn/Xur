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
