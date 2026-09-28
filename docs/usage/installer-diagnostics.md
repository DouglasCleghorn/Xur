# Installer diagnostics and console automation

For a blank display or an installer failure, enable a separate diagnostic HTTPS
API with a configuration USB drive. It runs in the agent, independently of the
console renderer and web manager. Web management and Tailscale still start only
after installation. No diagnostic listener exists without this opt-in file, and
this configuration is never copied to the installed system.

## Enable before boot

Generate a fresh key with `openssl rand -hex 32`. At the root of an unencrypted
configuration filesystem, create **one** `xur-diagnostics.yml` (or `.yaml`).
Copy [the example file](../examples/xur-diagnostics.yml) and replace its key:

```yaml
schemaVersion: 1
apiKey: REPLACE_WITH_64_RANDOM_HEXADECIMAL_CHARACTERS
allowControl: true
```

The placeholder is intentionally invalid. Set `allowControl: false`, or omit it,
for read-only diagnostics. Keep the key private. This key grants root-level setup
control when enabled, including the ability to approve erasure of an eligible disk.

The file must be regular, not a symlink, at most 32 KiB, with one YAML document.
Unknown fields, invalid keys and duplicate configuration files disable the API.
Its containing disk is protected from selection for installation, even if the
configuration is invalid. Correct the file and reboot to rescan.

The file can share the USB drive with [xur.yml](answer-file.md). That answer can
supply static wired networking; otherwise connect Ethernet for DHCP. Wi-Fi can be
configured through the remote console after another network path is available.
A raw-written ISO is generally read-only: use a separate configuration USB or a
separate supported configuration filesystem. Do not edit files inside the ISO.

After storage discovery, the API listens at `https://<server-IP>:9443` on IPv4
and IPv6, including addresses acquired later. If firewalld is active, Xur opens
9443 in its default zone for this boot only. Other firewalls must allow that port.
Find the IP in your router's DHCP leases, or use the address in your answer file.
Internet is unnecessary for diagnostics; installation still downloads the OS.
The API cannot respond before networking and agent/config discovery are ready.
Boot journal entries from earlier in startup remain available afterward.

## Connect and collect

Every request requires `Authorization: Bearer <apiKey>`. Query-string keys,
browser cookies and browser-origin requests are not accepted. Responses are
uncached. The self-signed TLS certificate changes when the agent restarts; its
SHA-256 fingerprint is written to the agent journal and its public certificate
to `/run/xur/diagnostics-cert.pem`. Obtain that certificate through a trusted local
or SSH connection and verify its fingerprint before trusting it. For example,
with a trusted copy saved as `.build/diagnostics-cert.pem`:

```sh
read -rs XUR_DIAGNOSTIC_KEY
export XUR_DIAGNOSTIC_KEY
curl --cacert .build/diagnostics-cert.pem \
  --resolve localhost:9443:192.0.2.10 \
  -H "Authorization: Bearer $XUR_DIAGNOSTIC_KEY" \
  https://localhost:9443/v1/status
```

For first contact on an **isolated test network** when the screen is blank and no
trusted certificate is available, `curl --insecure https://<server-IP>:9443/...`
can collect diagnostics. This encrypts traffic but does not verify server identity;
do not use it on an untrusted network. Never commit the key or collected reports.

| GET route | Contents |
| --- | --- |
| `/v1/status` | Bundle, boot ID, uptime, capture time, control permission, storage discovery and installation operation |
| `/v1/boot-logs` | Last 2,000 journal lines from this boot, with monotonic timestamps |
| `/v1/logs` | Agent/application/service logs, plus installer file logs |
| `/v1/installation-logs` | Installation service and Anaconda log tails |
| `/v1/display` | DRM connectors, enabled/DPMS state, modes, GPU drivers, framebuffer, fixed display probes and the last startup recovery attempt |
| `/v1/display-history` | Up to 120 in-memory display samples: every 5 seconds initially, then every 15 seconds |
| `/v1/hardware` | PCI devices and drivers |
| `/v1/network` | Current network settings and pending changes |
| `/v1/wifi` | Wi-Fi adapters, firmware/driver details and observed state |
| `/v1/console` | Current physical-console screen, text, options, selected index, input type and revision |

Poll status and display history every five seconds while reproducing the HDMI
problem. Save boot logs before and after replugging the cable. History starts
once diagnostic configuration is discovered and is bounded to the most recent
120 samples; it is also included in automatic/manual USB installation reports.
A console failure returns 503 for console routes while agent diagnostics remain
available. Boot logs and history are volatile: collect them before rebooting.
There is no arbitrary shell execution or arbitrary file-download route.

On installer boots, a matching AMD `REG_WAIT timeout` in `disable_crtc` triggers
one HDMI re-detection and console restart after a 15-second settling period. This
covers the observed case where HDMI reports connected, enabled and DPMS On while
the monitor receives no signal. It only affects the GPU named in the kernel
warning and never takes a GPU from a workstation. The attempt is recorded in the
journal and `displayRecovery`; a boot-local marker prevents repeated attempts
across agent restarts. This workaround still needs cold-boot hardware validation.

Installation progress identifies clock synchronization failures before disk
erasure and certificate/time errors during download. Capture installation logs
before rebooting. A failed installation is never retried automatically.

## Drive the real setup flow

`GET /v1/console` returns the screen actually shown on the physical console;
it does not create an independent setup session. JSON uses camelCase:

```json
{
  "revision": "<opaque revision>",
  "screen": "computer-name",
  "title": "Step 1 of 4 · Server name",
  "body": "...",
  "options": [{"id": 48, "label": "Back to menu", "enabled": true}],
  "selected": 0,
  "acceptsText": true,
  "secret": false,
  "inputValue": "xur"
}
```

With `allowControl: true`, post to `/v1/console/action` using the revision from
the latest response and exactly one of `text` or `option`:

```json
{"revision":"<current revision>","text":"living-room"}
```

```json
{"revision":"<current revision>","option":256}
```

Option IDs are numeric character codes, **not row indexes**. Discover them from
the returned `options`, and check `enabled`. Text submission saves the field like
Enter on the console. Password fields accept text but never return their value.
The response is the next current screen. Read-only configurations return 403 for
all actions; unavailable/disabled options or invalid text return 400. A stale
revision returns 409. Physical input and background refresh share the same lock
with diagnostic actions, so they cannot race disk confirmation.

Follow name → networking → disk selection → identity review → Yes/No. On
`setup-confirm`, approving the displayed **Yes** option additionally requires:

```json
{"revision":"<current confirmation revision>","option":121,"confirmErase":true}
```

This does not bypass name, network rollback, protected-media, exact disk identity,
plan digest or expiry checks. The API consumes each action revision before
execution. Never automatically retry a timed-out mutation: read status and the
current screen first; installation may already have started. Failed installation
has no automatic retry. Account creation still happens in the installed web
manager after reboot.

Installation logs fill the available terminal height. PgUp/PgDn traverse the
remaining wrapped text; Refresh and Back remain visible. Automation receives the
complete current log text, independent of the physical display's page size.
