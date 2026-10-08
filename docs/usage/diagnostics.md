# Diagnostics

## Display report

Open **Diagnostics** in the web manager and choose **Download report** (JSON) or **Copy report**. The report
includes the running control/agent bundle IDs, kernel, DRM cards and connectors,
framebuffer devices, display PCI devices, VMBus bindings, and Hyper-V module
availability, device owners, and NVLink topology. The report also includes the
exact output and exit status of these fixed read-only commands:

```text
nvidia-smi --query-gpu=index,pci.bus_id --format=csv,noheader,nounits
nvidia-smi topo -m
nvidia-smi nvlink --status
```

For streaming failures it also records the NVIDIA driver version, UUID/PCI
mapping, and each GPU's `/proc/driver/nvidia/gpus/<PCI>/information` record,
including its actual device minor. GPU index and device minor may differ.
Device owners include the executable path, cgroup, all four process UIDs and
the service, executable and account checks used to exempt NVIDIA's persistence
daemon. A compute context still blocks GPU assignment. These fields explain a
failed exemption without changing ownership policy.

No SSH login is needed. Run collection on the affected machine after updating,
then share the downloaded JSON. A failed command remains visible as a failure;
it is never treated as proof that an NVLink bridge is absent. Each inventoried GPU has the exact card/output checks that decide
whether it appears in the workstation picker.

This distinguishes a missing driver, framebuffer-only display, disconnected
connector, discovery failure, and a client running an older app version. The
report uses fixed read-only probes and includes no login token, kernel command
line, network configuration, user files, or general journal dump. The page does
not reload itself while text is selected. The manager uses HTTPS, including on the LAN; manual selection remains available
when the browser blocks clipboard access.

Authenticated automation can retrieve the same JSON at
`GET /api/diagnostics/display`. Anonymous requests are denied.

Choosing **Workstation** in the profile editor automatically selects the gaming
desktop and shows the GPU field. Choose an existing named workstation to retain
its user and pairing, or **New workstation** to supply a new identity. Manage
those identities on Workstations. If no GPU passes the picker checks, the editor
links directly to Diagnostics.

## System report

Choose **Download system report** in Diagnostics, or request
`GET /api/diagnostics/system` with a manager session or Diagnostics API key.
There is no setting to enable read-only reports, and SSH can remain disabled.

The JSON includes the agent bundle ID, capture time, GPU owners with process
identity checks, and these fixed read-only probes:

```text
journalctl --boot --no-pager --output=short-monotonic --lines=2000
journalctl --boot --dmesg --no-pager --priority=warning --lines=200
systemctl --failed --type=service --output=json --no-pager
systemctl show nvidia-persistenced.service --property=MainPID,ExecStart,User,Group,ActiveState,Result,FragmentPath
```

Each command has a ten-second deadline; output is limited to 256 KiB and marked
when truncated. Exit codes, unavailable probes and GPU observation failures
remain visible. Only one system report collects at a time. Collection may take
about a minute, depending on GPU probes. Arbitrary commands and paths are not
accepted through this API.

System journals may include account names, paths and details from other services.
Recognized credentials are redacted, but review the report before sharing it.

## Diagnostic SSH

Basic installs leave SSH disabled. For troubleshooting that needs an interactive
shell, open **Settings → Diagnostic SSH**, choose **Enabled for troubleshooting**,
paste one to eight distinct Ed25519 public keys and save. This is discouraged for
normal use: anyone with an authorized private key receives unrestricted root
access. Use it temporarily with someone you trust.

Connect to the server as `root` on port `22` using the matching private key, for
example `ssh -i /path/to/key root@xur-host.your-tailnet.ts.net`. Verify the host key
through a trusted channel first. Paste only public keys in Settings; passwords,
private keys, key options and other key types are rejected. Password login,
forwarding and alternative authorized-key sources are disabled.
The diagnostic listener uses an isolated configuration, so ordinary SSH `Match`
rules cannot grant additional access. The server's existing host keys are used.

Saving Enabled replaces the authorized keys. Choose **Disabled** and save to
revoke new logins and stop existing diagnostic sessions. Authorization and the
service override live under `/run`; an agent restart or reboot disables access.
Xur opens a runtime firewall port when needed and removes its own opening when
disabled. Existing firewall openings remain in place. Network and tailnet access
rules still apply.

An already active ordinary SSH service or socket is preserved; Xur refuses to
replace it with diagnostic SSH. Check Settings and application logs if a change
fails. The read-only status endpoint is `GET /api/diagnostics/ssh`. API keys of
every scope are prevented from changing this setting; it requires a manager's
Settings form with its CSRF token.

## Game rendering

A running workstation has a **Graphics report** link. It runs `vulkaninfo --summary`
and, when Xwayland publishes DISPLAY, `glxinfo -B` as the workstation user inside
that user's device-restricted slice. It also reports graphics package versions and
architectures, device policy, and an allowlisted set of graphics environment
variables. No full environment, game files or authentication tokens are collected.
The authenticated read-only endpoint is `/api/workstations/<id>/graphics`.
A failed or timed-out probe remains visible in the report; it is not a passing
rendering test. A successful 64-bit host probe does not verify the 32-bit Steam
runtime or game compatibility.

For a game that exits or renders a blank screen, use the Steam Launch Option
`PROTON_LOG=1 %command%`, reproduce once, then remove it. Review and share the
`steam-<appid>.log` in the workstation user's home alongside the graphics report.
See [Valve's Proton logging instructions](https://github.com/ValveSoftware/Proton/wiki/Proton-FAQ).

Workstation launch configuration explicitly selects NVIDIA's GLX vendor. Mesa
workstations select their GPU by PCI identity using DRI_PRIME. Device restrictions
continue to enforce assigned GPU access. No NVIDIA Vulkan selection is guessed
from card names or runtime indices; several cards can have identical names.
The owner subsequently confirmed that game rendering, Moonlight input and sound
worked on the RTX 3090. This does not establish compatibility for every game or
multi-workstation configuration. For a new regression, collect fresh reports and
download the Proton log through **Files → Workstation files**; see [Files](files.md).
