# Timezone

Choose the system timezone under **Settings → Timezone**. Search by IANA name,
such as `America/Denver`, then save. The API exposes authenticated `GET /api/timezone`
and `POST /api/timezone` with JSON `{"zone":"America/Denver"}` and a manager bearer
token. Browser forms retain CSRF protection. Unknown zones are rejected.

The agent uses `timedatectl set-timezone`; `/etc/localtime` persists across reboots
and OS/app updates. A choice made in the installer is handed to the installed OS.
This does not change the hardware clock or disable time synchronization.

Workstations and Sunshine read the host's localtime. New model and generic
containers use Podman's `--tz=local` and `TZ=:/etc/localtime`, overriding an image's
baked-in TZ and copying the required zone data even when it lacks tzdata. Running
processes may cache timezone data and existing containers keep their original
copy. Unload and reload their profile to apply the selection everywhere; saving a
timezone never interrupts running workloads. Apps with their own explicit timezone
setting may still use that app setting. Internal journal timestamps remain UTC.
