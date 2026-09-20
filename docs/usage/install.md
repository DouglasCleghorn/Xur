> Online installer: Internet access is required to download Bazzite. Xur first
> attempts a signed app refresh and falls back to the bundled app if unavailable.
> See [online installation](../architecture/online-installer.md).

# Install Xur

Choose a whole disk of at least 64 GiB for the Bazzite host and OS deployments.

1. Boot the ISO using UEFI. For Rufus, choose **GPT**, **UEFI (non CSM)**,
   **FAT32**, and **ISO Image mode (file copy)**. Raw/DD hybrid writing also works.
   Ethernet adapters request DHCP automatically. See [USB media details](rufus.md).
2. Open the HTTPS console URL on port **8443** (accept the machine’s self-signed certificate) and enter the access code shown as `ABC-DEF`. The form adds the hyphen as you type.
3. Create your manager username and password. The console token is then disabled
   and hidden; use the password for future logins.
4. Select a disk, continue, then click **Erase disk and install Xur**.
5. Follow the operation box and live logs. When complete, click **Reboot into Xur**.
   The page waits for the server to return and opens the system dashboard.
   Your signed-in session remains valid across reboot for its eight-hour lifetime.

The disk list contains available whole disks only. The installer still protects
boot/configuration media and rechecks disk identity immediately before writing.
No serial entry is required. The selected disk is erased and receives an EFI
partition, `/boot` and the root filesystem. Other disks are left unchanged.

Until an account is created, the setup code is reusable and case-insensitive. It expires after 30 minutes;
five attempts are allowed per 30 seconds. Answer files can supply the code for
[API account initialization](../architecture/manager-account.md). The signed JWT uses a machine-specific
key, copied to the installed system with root-only permissions. A fresh live
boot from the ISO generates a new key.

## Console

The full-screen menu opens on **Alt+F3**. Use **Up/Down** and **Enter**;
**0** returns to the menu. **Alt+F2** opens logs. **PgUp/PgDn** scroll content.
Numeric shortcuts select an item, then Enter opens it. Tailscale QR enrollment
is available locally. Logs and background status updates use separate views.
Kernel messages are directed to the boot console on **Alt+F1**, away from the menu.

## Tailscale

Open **Tailscale**, select **Sign in to Tailscale**, then follow **Authorize this
machine** in your browser. The LAN session remains available. Once connected,
confirm the account to enable management through the tailnet. Tailscale remains
in the navigation after installation.

## Installed system

The dashboard shows CPU, memory, storage and uptime, plus running workloads.
Xur-managed workload/station services can be started, stopped and restarted;
core system services are shown separately. Installation controls are removed.
Settings lists all non-loopback IPv4 and IPv6 addresses.

## Logs and CLI

Installation logs update without reloading the page. Scrolling upward pauses
following new log output until you return to the bottom. Operational messages
are retained; authentication secrets are omitted.

`xur` opens the console menu. Commands include `xur status --json`,
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
One local workstation is supported; it uses the selected display GPU and local
input/audio devices. See `docs/usage/model-catalog.md` and `docs/usage/profiles.md` in the
source archive for the runtime boundaries and API examples.

The console keeps a five-percent margin for TV overscan. Use Up/Down and Enter
to select the highlighted menu row. The Tailscale QR screen has a highlighted
**Back to menu** option; Enter, Escape or `0` returns while enrollment continues.
The initial access code is available immediately, even while storage discovery
is still running. Installation remains locked until storage checks finish.

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
Local build testing can be enabled separately in Settings.
Choose **Check for updates**, then **Update Xur**. Future app changes can be
installed this way without another ISO or OS installation.

The Updates page reconnects after activation and keeps the existing login
session. Active requests drain before the manager restarts; model containers
and workstations remain running. **Roll back Xur** restores the previous app.
The terminal menu has **Updates → Xur application**. Details and authenticated API
examples are in `docs/usage/application-updates.md` in the source archive.
