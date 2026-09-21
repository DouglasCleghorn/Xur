# Timezone and NTP

Choose the system timezone under **Settings → Timezone**. Search by IANA name,
such as `America/Denver`, then save with Automatic disabled.

**Automatic timezone** looks up the server's public IP through `https://ipwho.is/`
at boot and when **Refresh** is selected beside the option. The lookup uses the
server's Internet connection, not the browser's location, and can reflect a VPN or
proxy exit location. Failed lookup retains the previous zone and reports an error;
boot retries do not delay the web manager. Previously saved manual settings stay
manual. Select a manual zone when IP geolocation is unsuitable.

The API exposes authenticated `GET /api/timezone`
and `POST /api/timezone` with JSON `{"zone":"America/Denver"}` and a manager bearer
token or an Automation API key. Use `{"automatic":true}` to enable lookup.
Browser forms retain CSRF protection. Unknown zones are rejected.

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

## Clock synchronization

Settings provides NTP enable/disable, preferred servers and synchronization
status. New configuration defaults to `time.cloudflare.com`; an existing custom
configuration is retained. Saving validates Chrony configuration before service
changes and rolls back on failure, preserving OS/DHCP fallbacks. Managed settings
persist in `/etc/chrony.conf` and `/etc/chrony.d/xur.conf`.

NTP synchronizes the host clock; timezone controls its local presentation. Model
containers and workstations share the host clock. Changing the timezone is not a
remedy for an unsynchronized clock. `GET /api/ntp` reports current status to an
authenticated client; use Settings to change the NTP configuration.
