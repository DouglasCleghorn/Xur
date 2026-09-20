# Getting started with Xur

Xur manages a Bazzite host through HTTPS. Its profiles describe what should run,
which GPUs each workload owns, and which workstation users and peripherals to use.
Home is the control panel; Monitoring contains resource and network details.

## Install and sign in

Follow [Installation](install.md) to create and boot UEFI media. The online
installer requires Internet access and a target disk of at least 64 GiB. Choosing
**Erase disk and install Xur** destroys existing data on that disk.

Open the console's `https://<host>:8443` address, accept its self-signed certificate,
and enter the setup code. Create a manager account before installing. Afterward,
use that account to sign in; the setup token is no longer a login method.

For remote access, open **Tailscale**, authorize the machine, and finish its setup.
Use the resulting HTTPS Serve address. Keep credentials and setup QR codes out of
screenshots and issue reports.

## Create your first profile

1. Open **Profiles → Create profile**, name it, and add workloads.
2. Choose a workload type, recipe and GPUs. Check VRAM capacity and any NVLink hints
   on the GPU page. Each GPU belongs to one workload at a time.
3. Save, return to **Home**, select the profile preview, and load it.
4. Watch each action. Independent workloads start in parallel. Failed actions have
   their own errors; successful siblings remain available. **Resume** retries
   unfinished actions. **Cancel change** stops queued work and lets claimed actions
   finish; it does not undo actions that already completed.

Profiles store pinned model/engine selections. Loading a large model for the first
time can take substantially longer while downloading and warming up.

## Workstations, USB and sound

Give each desktop a named workstation identity and a distinct Unix user and GPU.
Choose a persistent user to keep Steam logins, games and home files. Temporary
users are disposable. Reuse the same named workstation in other profiles to keep
its Moonlight pairing; changing its GPU restarts that desktop.

Select USB devices individually or assign a hub with its supported descendants.
A unique serial is the most portable identity. Without one, selection follows a
physical controller/port path; moving ports can require reassignment. Raw USB
storage/controller passthrough is not part of this feature.

Choose one primary workstation for unassigned input and built-in audio. USB audio
and display audio follow their workstation assignments. See the
[multiseat design and validation limits](../architecture/multiple-workstations.md)
before relying on isolation between multiple people.

A connected screen is optional. Disconnected GPUs show **No display detected.**
The workstation can create a virtual monitor for streaming. Open **Workstations**
for its current status, Sunshine pairing instructions and **Open Moonlight** link.
Install Moonlight on the client, pair it, and start the desktop. Streaming requires
encryption. Use the graphics report and workstation logs for capture, encoding or
input failures.

## Models and client endpoints

Use the model selectors in a profile to choose a compatible recipe and GPU set.
Add an HF token in **Settings** when required by the model repository, and obtain
any necessary repository access from its publisher. **Models** lists local model
locations and can search attached storage. Downloads stay on disk between reboots.

After a model becomes ready, open **LLM endpoints**:

- Copy its **Base URL** into an OpenAI-compatible client.
- Create a **Testing** key under **Settings → API keys** and use it as the API key.
- Open **Model list** to find the exact served model ID.
- Use the Tailscale HTTPS address when the client is on another network.

Only running language models with published routes are shown. A running container
alone does not establish endpoint readiness. If the page is empty, check the
profile operation and workload logs. **Model lab** provides test chat and saved
benchmarks, including model identity, running workloads, results and sampled VRAM.

## Keep the server up to date

**Update All** checks the OS and Xur application. These have separate lifecycles:

- **Xur application:** a signed app bundle; activation briefly reconnects the web
  manager and preserves independently running workloads.
- **Operating system:** Bazzite kernel, drivers and desktop components; download
  and staging happen before reboot. A pending deployment banner means reboot is
  required. Reboot interrupts workloads.

An available version is not necessarily staged. Check the operation result and
pending deployment on **Updates**. OS rollback queues the previous deployment;
application rollback selects the previous app bundle. Neither rolls back your
personal files. Download a configuration backup from **Settings** before major
changes; it is not a backup of models, games or home directories.

For custom builds, enable **Settings → Update channel → Local build testing** and configure the
local repository. Signed GitHub releases remain the normal distribution path.
See [application updates](application-updates.md) and [OS updates](updates.md).

For static addresses or a bootstrap code supplied on USB, see
[answer YAML and network settings](answer-file.md). Disk approval remains required.
