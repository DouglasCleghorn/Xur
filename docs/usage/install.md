> Internet is required to download Bazzite. The bundled console starts without
> waiting for internet; Wi-Fi setup and disk review are local.
> See [online installation](../architecture/online-installer.md).

# Install Xur

Choose a whole disk of at least 64 GiB for the Bazzite host and OS deployments.

1. Boot the ISO using UEFI. For Rufus, choose **GPT**, **UEFI (non CSM)**,
   **FAT32**, and **ISO Image mode (file copy)**. Raw/DD hybrid writing also works.
   See [USB media details](rufus.md).
2. In **Setup and installation**, save the server name and configure networking.
   Ethernet uses saved profiles or DHCP. **Network and Wi-Fi** lets you choose
   an adapter, SSID and password. An [answer YAML file](answer-file.md) can supply
   static wired networking. Saved settings, including Wi-Fi credentials and
   automatic reconnection, persist into the installed system.
3. Choose the installation disk. Review its identity and the erase plan, then
   choose **Yes** at the erase confirmation (**No** is selected by default). Boot/configuration media are protected;
   Xur rechecks the selected disk before writing. Other disks remain unchanged.
4. Follow console progress. When complete, confirm reboot and remove the USB.
5. After reboot, open the displayed HTTPS web-manager address on port **8443**
   and enter the console access code. For LAN HTTPS, accept this machine's
   self-signed certificate. Create the **required administrator account** in
   your browser. Use Chrome's suggested strong password or your password manager
   and save it. Suggestions depend on browser settings and site trust.
6. Use the administrator username and password for subsequent logins. Creating
   the account disables the setup code and all setup-only sessions.

There is no web device installer and no Tailscale enrollment on live media.
The console or `xur setup` handles naming, networking, disk approval and progress.
Administrator credentials are created only after installation; no password
must be typed on the physical console. The initial access code expires after
30 minutes and allows five attempts per 30 seconds; reboot generates a fresh
code if needed. An answer-supplied code is carried privately into the installed
system and removed once the account is created.

## Console

The full-screen menu opens on **Alt+F3**. Use **Up/Down** and **Enter**;
Escape or **0** returns to the parent screen. **PgUp/PgDn** scroll content.
**Logs** has a Back action; the separate **Alt+F2** log terminal also accepts
Enter or Escape to return. Kernel messages remain on **Alt+F1**.

The display goes black after five minutes without keyboard input, even during
installation. Installation continues. The first key only wakes the display.

## Tailscale

Tailscale is available after booting the installed system. Use **Tailscale QR**
on the console, or sign in to the web manager and open **Tailscale**.
The saved server name is required before enrollment. Authorize this machine and
use its HTTPS Serve address. The console token is still required to create the
administrator account; Tailscale identity alone cannot bypass that step.
The account form uses `autocomplete="new-password"` for browser password managers.

## Installed system

Home provides update/reboot actions and a profile picker. Monitoring shows CPU,
memory, storage, network usage and running workloads.
Xur-managed workload/station services can be started, stopped and restarted;
core system services are shown separately. Installation controls are removed.
Settings lists all non-loopback IPv4 and IPv6 addresses.

## Logs and CLI

Installation progress and logs are available in the console. Operational messages
are retained; authentication secrets are omitted.

`xur` opens the console menu. Commands include `xur setup`, `xur status --json`,
`xur network show`, `xur login show`, `xur tailscale status`, `xur tailscale qr`,
`xur hardware show --json`, `xur logs show`, `xur reboot`, and `xur shutdown`.

After installation, **Create profile** opens an editor with a default name.
Choose workloads and GPUs using the searchable selectors, save, then select
**Load profile** to start switching. **Preview** separately shows the plan.
Names can be edited; IDs are assigned automatically. **Edit** is under **More**.
Choose **Workstation**, **llama.cpp**, **vLLM**, **vLLM-Omni**, or **Container** under
**Workload type**. Workstation offers the local Plasma gaming desktop; the model
types search Unsloth GGUF, Hugging Face, and the upstream Omni supported-model
list respectively. Each type shows only its own choices. Model format and GPU
selectors have defaults. First start downloads the pinned model and engine.
Workstations offer existing users, adding a user, or a temporary user. Named
users retain their home and Steam logins. Prepare container images or Dockerfiles
on **Containers**; persistent volumes also appear on **Storage**.
Multiple workstations use distinct GPUs and Unix users. Assign USB devices or
hubs and choose one primary workstation for otherwise unassigned input and built-in
audio. Physical multiseat acceptance is still in progress; see
[multiple workstations](../architecture/multiple-workstations.md),
[model catalog](model-catalog.md), and [profiles](profiles.md).

The console keeps a five-percent margin for TV overscan. Use Up/Down and Enter
to select the highlighted menu row. The Tailscale QR screen has a highlighted
**Back to menu** option; Enter, Escape or `0` returns while enrollment continues.
Installation remains locked until storage checks finish. The access code is
displayed after booting the installed system, until the account is created.

## OS updates

The installed system uses Bazzite KDE Desktop, with desktop sessions stopped
by default. Xur starts automatically from its separate application bundle.
OS and NVIDIA updates come directly from Bazzite, independently of Xur releases.

Open **Updates** in the web manager or **Updates → Operating system** in the terminal menu to
check, stage, pause automatic updates, or queue the previous OS version.
Updates are staged automatically; reboot explicitly to activate them. Reboot
interrupts all workloads. `xur updates status --json` and `xur updates check`
provide the same status and check operation for automation.

## Xur application updates

Open **Updates → Xur application**. Public GitHub Releases are selected by default.
Local build testing is an option in Settings → Update channel; enter the local server and its signing public key.
Choose **Check for updates**, then **Update Xur**. Future app changes can be
installed this way without another ISO or OS installation.

The Updates page reconnects after activation and keeps the existing login
session. Active requests drain before the manager restarts; model containers
and workstations remain running. **Roll back Xur** restores the previous app.
The terminal menu has **Updates → Xur application**. Details and authenticated API
examples are in `docs/usage/application-updates.md` in the source archive.

## Release download verification

Current releases contain one ISO, one app archive and one signed JSON descriptor, with the ISO
checksum in the release notes and authenticated metadata inside the JSON.
See [verification instructions](../development/installer-releases.md#download-and-verify). Historical ISOs larger
than the per-file upload limit are split into numbered parts; assemble and verify
them before writing a USB drive. See [download and verification instructions](../development/installer-releases.md#download-and-verify).
