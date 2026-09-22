# Answer YAML and static networking

An answer file can configure wired networking and optionally set the post-install bootstrap
login code. **It does not approve disk installation.** Select the exact target disk, review it, and approve installation in the
console. After reboot, use the code in the browser to create the required
administrator account.

Download or copy [the example xur.yml](../examples/xur.yml). Replace every example
MAC address, IP address, gateway, DNS server and bootstrap code with values for
your machine. The example IPs belong to a documentation-only network.

```yaml
schemaVersion: 1
bootstrapToken: A7K-2M9
network:
  interfaces:
    - macAddress: "02:00:00:00:00:10"
      ipv4:
        method: manual
        addresses: [192.0.2.10/24]
        gateway: 192.0.2.1
        dns: [192.0.2.53]
      ipv6:
        method: auto
```

Put **one** file named `xur.yml` or `xur.yaml` at the root of a filesystem on a
configuration USB drive or other configuration disk. Do not provide both names,
even on the same disk. The installer scans all eligible storage read-only;
multiple answers are ambiguous and leave installation locked. The answer's
parent disk is protected from erasure. The file must be a regular file, not a
symlink, at most 32 KiB, with one YAML document. Unknown fields and malformed
configuration are rejected rather than ignored.

The bundled installer can apply the answer's networking without first
downloading an application update. Signed update checks run in the background
and retry when a connection becomes available.
Once discovery and network configuration succeed, the console shows the current
addresses and the installer can download Bazzite using the configured network.
Internet access is still required for that OS download.

## Fields

| Field | Meaning |
| --- | --- |
| `schemaVersion` | `1` |
| `bootstrapToken` | Optional six-character Crockford Base32 code; the middle hyphen is optional. Carried privately into the installed system for account creation, then deleted. Omit for a random code on the installed console. |
| `network.interfaces` | One to sixteen wired-adapter configurations. Omit `network` to use the normal saved-profile/DHCP startup. |
| `macAddress` | Adapter MAC, preferably used for stable matching across installer and installed interface names. |
| `interface` | Optional observed interface name, such as `enp3s0`. At least a MAC or interface name is required. If both are given, both must match. |
| `ipv4.method`, `ipv6.method` | `auto`, `manual`, or `disabled`; an omitted IP family defaults to `auto`. At least one family must remain enabled. |
| `addresses` | A YAML list of up to eight CIDR host addresses, required for `manual`. IPv4 example: `192.0.2.10/24`; IPv6: `2001:db8:1::10/64`. |
| `gateway` | Optional gateway for `manual`, in an address subnet. An IPv6 link-local gateway such as `fe80::1` is supported without a `%interface` suffix. |
| `dns` | Optional list of up to eight DNS server IPs of the matching family. Explicit DNS replaces automatic DNS for that family. |

`auto` means DHCP for IPv4 and normal NetworkManager automatic IPv6 configuration.
Addresses and a gateway require `manual`; disabled families cannot have DNS.
Loopback, multicast, unspecified, invalid-prefix and IPv4 network/broadcast
addresses are rejected. Bridges, bonds, Wi-Fi credentials, custom routes and
VLAN creation are outside this answer schema. Duplicate or missing adapter
identities leave installation locked.

For automatic networking with a preferred DNS server:

```yaml
schemaVersion: 1
network:
  interfaces:
    - interface: enp3s0
      ipv4:
        method: auto
        dns: [192.0.2.53]
      ipv6:
        method: auto
```

Answer-file networking is explicit boot configuration and is kept automatically
after successful activation. If activation fails, Xur restores the preceding
connection and leaves installation locked. Review the console or installer
status, correct the answer, and reboot to rescan. The answer file is not copied.
Its optional bootstrap code is staged privately for post-install account creation
and deleted after signup. Confirmed NetworkManager profiles
are copied, bound to the adapter's MAC, and persist across reboot and OS updates.

## Change networking after boot

Open **Settings → IP addresses → Manage automatic and static IPs**, or select
**Network settings** in the local console / interactive `xur` menu. Select a
wired adapter, edit IPv4 and IPv6, then apply the draft.

Interactive changes have a **two-minute rollback window**. The browser may
disconnect; open the new HTTPS address, sign in if needed, then choose **Keep
settings**. The console can also keep or revert the change. If no one keeps it,
NetworkManager restores the preceding connection even if the manager restarts.
An unconfirmed candidate does not autoconnect after a reboot. The installer
requires you to keep or revert pending changes before approving a disk.

The keyboard console supports direct text entry for addresses, gateway and DNS.
Typing replaces the selected field value; Backspace edits it, Enter saves the
draft field, and Escape cancels. The line-oriented `xur` menu accepts comma-separated
addresses and `/cancel` to leave a field. Blank gateway/DNS fields clear those values.

Authenticated automation uses `GET /api/network/settings`,
`POST /api/network/settings` with the same interface/IP structure as one YAML
adapter entry, and `POST /api/network/keep` or `/api/network/revert` with
`{"id":"<pending change id>"}`. JSON property names are camelCase. Mutation
requests require an authenticated bearer token or browser session with CSRF
protection. Local equivalents are under `/local/network/` on the root-private
control socket.

See [API initialization](../architecture/api-initialization.md) for the account,
post-install account steps. Disk review and approval take place in the console. Network rollback uses NetworkManager's
[checkpoint API](https://networkmanager.dev/docs/api/latest/gdbus-org.freedesktop.NetworkManager.html).
