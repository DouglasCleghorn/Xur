# Persisted Compute design

Status: product decisions settled; interactive design preview, **not implemented**.
Updated on 2026-10-11 against source `d025ad9`. Engineering proposals and future
acceptance requirements below are not claims of working runtime behavior.

## Brief

Add **Persisted Compute** for long-running services, principally web servers.
Each item can be turned on or off. It can use no GPU or share selected GPUs,
including with active profile models and workstations. Show its RAM and VRAM
consumption prominently. Items come from Xur-managed system services or the
same prepared containers available to profiles. Robotics is the first concrete
use case and must coexist with workstations using specific device conflict checks.

On services survive profile changes and return after reboot; Off services stay
off. Robotics restarts its web app disarmed, never resuming motion. Scheduling
is excluded from the initial version. Future automation may use a queue or cron
job model; no weekly running-window model has been accepted.

Profiles assign resources to persisted services without controlling their On/Off
intent. Each service has defaults; the active profile can override its selected
GPUs and resource limits. Missing overrides and profile unload restore defaults.
Apply performs required restarts after listing affected services in its preview,
preserving settings, data and the service's address. A profile switch that does
not change effective resources leaves the service running. Existing model and
workstation profile lifecycles are unchanged by this design.

Audience: the person managing the Xur host and using its hosted applications.
The page's primary job is to answer what should be running, what actually is
running, what resources it uses, and how to open or change it.

## What exists today

| Evidence | Implication |
| --- | --- |
| [Profile policy](../../src/Xur.Domain/Profiles.cs) plans Stop for observed workloads absent from the target profile. | Simply adding persistent items to the current observation would let profile changes stop them. Lifecycle ownership must be explicit. |
| [Profile load](../../src/Xur.Control/ParallelLoad.cs) publishes target-profile routes; [unload](../../src/Xur.Control/ParallelUnload.cs) publishes an empty route set. | Persistent web routes must survive both operations. Separate start logic alone is insufficient. |
| [Profile validation](../../src/Xur.Domain/Profiles.cs), [runtime allocation](../../src/Xur.Agent/WorkloadRuntime.cs), and [GPU release checks](../../src/Xur.Agent/GpuInventory.cs) require exclusive GPUs. | Sharing needs coordinated allocation and release rules across both lifecycles. Removing one validation check would leave other operations failing. |
| [Container preparation](../usage/container-workloads.md) already supports images, Dockerfiles, immutable recipes, environment, health checks, and named volumes. | Reuse preparation and volume management. A recipe need not imply profile ownership. |
| [Robotics container](../../src/Xur.Agent/RoboticsWebContainer.cs) maps discovered serial, camera, and controller devices and holds a host gamepad reservation. [Runtime policy](../../src/Xur.Agent/WorkloadRuntime.cs) blocks concurrent workstations. | Keeping the robot dashboard running alongside desktops requires a deliberate peripheral ownership design. |
| [Robotics recipe validation](../../src/Xur.Domain/Profiles.cs) requires CPU-only execution; its image and resource limits are set in the robotics launcher. | A GPU picker alone would not make Robotics GPU-capable. Image capability, device mapping, and application training settings need validation. |
| [Robotics startup](../../src/Xur.Agent/RoboticsWebContainer.cs) uses a fixed singleton container and `--restart=no`; [agent initialization](../../src/Xur.Agent/WorkloadRuntime.cs) reconciles its reservation without starting it. | Reboot restoration and a migration to one durable service are new behavior. Preserve the existing robot data and emergency-stop latch. |
| [GPU monitoring](../../src/Xur.Agent/GpuMonitor.cs) records NVIDIA per-process VRAM and device-level readings; [system monitoring](../../src/Xur.Agent/SystemMonitor.cs) records host RAM. | Per-item RAM and VRAM attribution still need implementation. Existing host/device totals are not item usage. |
| [HTTP forwarding](../../src/Xur.Domain/StreamProxy.cs) strips cookies, authorization, and upgrade headers. | The current inference/robot proxy is not a general web-app proxy. Cookies, WebSockets, redirects, base paths, and browser origin isolation need design. |
| [Robot startup](../../containers/robot/Runtime/RoboticsRuntime.cs) resets arming and retains persisted emergency-stop state. | Service restoration must preserve this behavior. Running the web server does not authorize motion. |

The [container proposal](container-workloads-proposal.md) explicitly calls for
reusing resource coordination rather than introducing a competing allocator.
Independent service lifecycles must still share the host's allocation authority.

## Visual direction

Follow the existing [Xur design system](../architecture/design-system.md):

- Graphite `#15181d`, Surface `#20252d`, Rule `#373f4b`, Text `#edf1f7`,
  Secondary `#b0bac8`, Action `#99c5ff`.
- Locally bundled IBM Plex Sans: 28 px page heading, 18 px service names,
  15 px body/controls, 14 px supporting text. Use tabular figures for readings.
- Left-aligned content with the existing navigation rail. Put Persisted Compute
  beside Profiles; preserve access on mobile.
- Use aligned service rows. Keep mode, observed state, resource readings, and
  Open visible. Green means running, amber means waiting or changing, and red
  means an error. Always accompany color with text.
- Make the relationship between selected GPU and this item's memory use the
  distinctive element. Keep surrounding navigation and actions quiet.

Conceptual desktop layout; names and readings below are illustrative, not
measurements or additional promised catalog entries:

```text
Persisted Compute                                      Add service
Services that run independently of profiles.

Service             Mode          State       RAM      GPU / VRAM
Robotics            On            Running     0.8 GiB  No GPU
  Open  Settings  Logs

Vision server       On            Running     2.1 GiB  GPU 2, shared
                                                      Item: 1.6 GiB
  Open  Settings  Logs                                GPU: 9 / 24 GiB

Local web server    Off           Stopped      —        No GPU
  Settings  Logs
```

Keep mode separate from status: an item can be **On / Starting** or **On / Failed**.
A single green toggle cannot explain those states. Show a labeled On/Off control.
Applying a mode change reports its pending and final state, retaining errors and
the saved mode if startup fails.

RAM remains visible for CPU-only services too. Show item VRAM per selected GPU,
with a separate whole-GPU usage reading for context. Do not imply that the full
GPU capacity is reserved for the item. For several GPUs, expand into one line
per device without adding their capacities into a fictional shared memory pool.

Mobile stacks the same fields beneath each service name. Preserve mode,
RAM, selected GPUs, and Open; put secondary actions in a disclosure if
necessary. Target at least 44 px for touch controls, with no horizontal scrolling
at 320 px. Keep keyboard focus visible and announce operation results without
moving focus on every telemetry refresh.

### Add and edit

```text
Add service

Source           Xur system service | Prepared container
Service / image  [selection or existing preparation flow]
Name             [suggested human name]

Lifecycle        [x] Keep running outside profiles
Operation        Off | On (when checked)

GPU access       No GPU | Shared GPUs
GPUs             [available devices, names and current usage]

Advanced         Storage, command, environment, health, web access
Save service     Cancel
```

Custom-app web access offers **Easy HTTPS with Tailscale**, the app's HTTP
port, and a suggested service name. Once ready, show the separate HTTPS address
and **Open**. If tailnet setup is incomplete, show **Finish Tailscale setup**
without stopping the container. This is an optional convenience over ordinary
container networking; port bindings do not depend on enabling it. Xur's trusted
Robotics UI can continue opening at `/robot`.

Use the existing container preparation flow rather than creating another build
system. Offer only capabilities supported by the selected service/image. A
service collection entry can have a specialized setup page; Robotics keeps
its calibration and controller configuration inside the robot app.

The container keeps the same identity, settings, and data in either lifecycle.
Suggested control: **Keep running outside profiles**. Unchecked, profiles govern
its lifecycle; checked, its own On/Off control governs it. This does not create a
copy or fresh writable volumes. The checkbox represents lifecycle only; a
profile's resource assignment is separate. Display the service's saved defaults,
effective resources and the profile supplying an override. Editing a default
under an active override must not falsely claim that the effective assignment
changed. Recovery delay values and
shutdown grace periods are engineering defaults, not additional lifecycle modes.

The profile preview lists each persisted service as **Keep running**, **Restart
with new resources**, or **Update resources; remains Off**, showing before/after
GPUs and limits. Applying a profile cannot turn an Off service On. Unloading a
profile previews any restarts needed to restore defaults. Preserve each service's
stable identity, data and HTTPS name through resource changes; an intentional
restart interrupts connections and must be called out before Apply.

Removing a container from Xur always asks what to do with its stored data:
**Keep stored data**, **Delete stored data**, or **Cancel**. Require an explicit
choice on every removal rather than silently choosing retention or deletion.
List the affected volumes; do not delete storage still referenced by another
container. Turning Off is not removal and always retains data. Removing a
profile resource assignment is not deleting the underlying container.

### States to design and prove

| State | Page behavior |
| --- | --- |
| Empty | Explain what belongs here and provide Add service. |
| Preparing / starting / stopping | Show the active step and preserve unrelated controls. |
| Running | Open, logs, memory readings, and the saved operating mode are visible. |
| GPU or peripheral unavailable | Name the missing or conflicting resource and offer settings/retry. Do not substitute another device silently. |
| Failed / retrying | Show the cause, last attempt, and any next retry. |
| Telemetry unavailable | Show Not reported or a timestamped stale reading. Do not display a fabricated zero. |
| Stopped | Show Stopped and a dash for current usage. Retained data is separate from resource use. |
| Manager unavailable | Preserve the last observation as stale; do not claim a stop occurred. |

Review against the brief: service rows make mixed CPU/GPU applications easier
to compare than a gallery of equally large cards. Separate mode and status avoids
hiding failed starts behind an On control. Keep both RAM and VRAM in the list;
requiring the GPU monitoring page would defeat the requested quick visibility.

### Interactive review

An offline interactive prototype is available at
`.build/evidence/persisted-compute/design/index.html`. It uses the existing Xur
stylesheet, local IBM Plex Sans font and local icon, with their license notices.
All service names, memory readings, states and HTTPS addresses are synthetic;
controls make no host, container, robot or tailnet changes.

The 2026-10-11 revision includes effective GPU/RAM-limit sources, editing service
defaults under an active override, a profile-change preview and simulated Apply,
and explicit keep/delete/cancel choices on removal. The profile resource controls
are a focused demonstration, not a replacement design for the full Profiles page.

The T3 collaborative browser review covered 1440 px desktop and 390/320 px phone
layouts, with no horizontal overflow. Interaction checks covered adding an
unlisted prepared container, the lifecycle checkbox, selected-GPU validation,
Escape dismissal, simulated On/Off, failed-start Retry, and Off cancelling a
pending retry without a stale timer restarting the item. Empty, unreported-metric
and unfinished-Tailscale-setup states were checked. A pending row update
did not steal focus from an open settings input. Native select values were
changed through DOM events during automation; this is not a native mobile
browser acceptance test. HTTPS fields were stacked after narrow-layout review.

The follow-up review covered 1440/1024/390/320 px without horizontal overflow.
Desktop overrides changed the On example to GPU 1 / 4 GiB RAM limit and the Off
example to a 1 GiB RAM limit without starting it. Unloading restored GPU 2 /
8 GiB and the Off example's 2 GiB default. A profile with unchanged effective
resources correctly reported no restart. Off during a simulated resource restart
cancelled completion and retained the HTTPS address. Removal required a fresh
data choice each time; Cancel retained the item, and both Keep and Delete paths
reported the selected outcome. Some T3 native clicks failed; DOM-triggered
events were used to complete those state checks, so native interaction coverage
is partial rather than a claim of full device-browser acceptance.

Private captures live under `.build/evidence/persisted-compute/design-review/`
and its `2026-10-11/` subdirectory and are recorded in
[the screenshot register](../screenshots.md). The dated `captures.json` maps
the updated screens to native T3 captures. This review does not validate service
persistence, GPU isolation, memory attribution,
actual HTTPS certificates, bounded crash recovery or physical robot operations.

## Decision round 1

The user answered this round on 2026-10-10. Q1–Q6 are settled. Q6 establishes
the browser trust boundary; Q11 later selects Tailscale for Xur-managed easy HTTPS URLs
for custom container frontends. Authentication and provisioning details remain
engineering work.

| ID | Decision | Outcome |
| --- | --- | --- |
| Q1 | Persistence across profiles/reboots | Accepted. On returns after reboot; Off stays off. Robot motion never resumes automatically. Scheduling is removed by Q5. |
| Q2 | GPU sharing with profile workloads | Accepted, including models and workstations. Keep persistent services running and expose shared memory pressure without reserving VRAM. |
| Q3 | Robot/workstation coexistence | Accepted. Enforce conflicts on the specific devices needed instead of a blanket exclusion. |
| Q4 | Service collection | Xur-managed system services plus the same containers users can select in profiles. |
| Q5 | Scheduling | Deferred entirely. Future automation may be a queue or cron job; no weekly schedule UI or runtime in this version. |
| Q6 | Web access trust boundary | Accepted. Manager-origin paths are reserved for first-party Xur-maintained applications explicitly trusted as part of the manager. Arbitrary or user-selected container frontends use separate hostnames, host-only credentials, and strict origin/antiforgery enforcement. Q11 later selects Tailscale for Xur-managed easy HTTPS URLs for custom frontends; ordinary container port publishing remains available. Existing local manager access and trusted first-party `/robot` access remain available. |

### Tailscale naming investigation

Verified against current official documentation on 2026-10-10. Tailscale Services
can give applications separate stable MagicDNS names and HTTPS endpoints while
one host advertises multiple services. The host must have a tagged identity;
service definitions, host approval (or auto-approval), and access policies are
required. HTTPS Serve provisions certificates. See
[Tailscale Services](https://tailscale.com/docs/features/tailscale-services) and
[the multi-service configuration format](https://tailscale.com/docs/reference/tailscale-services-configuration-file).

Illustrative addresses on the same Xur host could be
`https://robot.<tailnet>.ts.net` and `https://vision.<tailnet>.ts.net`.
These are private tailnet endpoints, not public hosting. Ordinary node Serve
uses the device's tailnet name; arbitrary customer-owned domains are a separate
DNS/certificate configuration. See
[Serve limitations](https://tailscale.com/docs/features/tailscale-serve).

Q11 accepts Tailscale HTTPS service names for Xur-managed easy HTTPS custom-app
URLs in this version. Ordinary container port publishing and direct application
access remain available. Existing local manager access and the trusted
first-party `/robot` path remain available. Service-name collisions across Xur
hosts, authentication, provisioning, and integration with Xur's existing Tailscale
enrollment still need engineering design. No tailnet changes have been made.

### Path proxy trust boundary

The user asked whether path-based proxying is safe. A URL path does not isolate
browser applications: scheme, hostname, and port determine the origin. Thus
`https://xur.example/apps/vision/` and `https://xur.example/settings` share one
origin. See [browser same-origin policy](https://developer.mozilla.org/en-US/docs/Web/Security/Defenses/Same-origin_policy).

Code review of [BrowserSecurity](../../src/Xur.Control/BrowserSecurity.cs),
[authentication middleware](../../src/Xur.Control/Program.cs), and
[RobotProxy](../../src/Xur.Control/RobotProxy.cs) shows the consequence for Xur:
script served by a same-origin app can request protected manager pages and
antiforgery tokens, then submit authenticated manager operations using the
browser's session or the trusted Tailscale transport. This assumes an authorized
user opens compromised or malicious app content; it is a trust-boundary analysis,
not a demonstrated exploit or a claim that the current robot app is compromised.

The existing proxy strips credentials before forwarding to a container, but
that does not stop the container's browser script making separate requests to
the manager. HttpOnly prevents reading a session cookie, not using it in a
request. Cookie paths, CORS, and the existing CSP with `'self'` do not establish
isolation between these paths.

Accepted Q6: reserve manager-origin paths for first-party Xur-maintained UIs
explicitly trusted as part of the manager. Give arbitrary or user-selected
container web UIs separate hostnames with host-only credentials and strict
origin/antiforgery enforcement. Trusted-frontend classification must come from
Xur's reviewed service definition, never arbitrary container image labels.
Xur management of a container's lifecycle alone does not make its web content
trusted. A proxy may still route internally by path; the address visible to the
browser is what sets this boundary. Putting all apps on paths of one separate
apps hostname isolates the manager but still shares an origin between those apps.

Different ports create different origins, but cookies are not port-scoped, so a
port-only design needs additional credential isolation and cannot be treated as
equivalent to separate hostnames. See [HTTP cookie security boundaries](https://httpwg.org/specs/rfc6265.html#section-8.5).
An enforced opaque-origin sandbox is another possible approach, but compatibility
with arbitrary application scripts, storage, authentication, and navigation would
need explicit validation. No generic sandbox solution is assumed here.

## Decision round 2

The user settled Q7–Q11 on 2026-10-10. The container lifecycle checkbox,
bounded recovery, operation-scoped peripheral ownership, and Tailscale-managed
easy HTTPS custom-app URLs are accepted scope; implementation details remain.

| ID | Decision | Outcome |
| --- | --- | --- |
| Q7 / Q10 | Container identity and lifecycle | Accepted: keep the same container identity, settings, and data, with a checkbox selecting profile-governed or persisted lifecycle. Suggested label: Keep running outside profiles. No separate copy, fresh-volume default, or Move product workflow. Q12/Q15/Q16 refine profiles into resource overrides for persisted services, with independent intent and disclosed resource-change restarts. |
| Q8 | Recovery while On | Accepted. Restart automatically with increasing delay; after five failed starts, show Failed until Retry. Off cancels pending recovery. Robotics always restarts disarmed. |
| Q9 | Robotics peripheral ownership | Accepted. Reserve devices only during robot operations, independently of the dashboard lifecycle. Camera operations must also claim the devices they actually use and release them after verified completion. Keep the dashboard available when a device is missing or busy; block the affected operation without taking a device from its current owner. Exact acquisition mechanics remain engineering work. |
| Q11 | Easy HTTPS custom-app URLs | Accepted: Xur-managed easy HTTPS uses a distinct Tailscale origin per custom app and an Open button, targeting the app's configured HTTP port. Ordinary container port publishing and direct access remain available. LAN DNS/mDNS and local CA/custom TLS provisioning are outside this convenience feature. Existing local manager access and the trusted first-party `/robot` path remain available. |

The earlier Q7 independent-instance/fresh-storage recommendation and Q10 explicit
conversion workflow are superseded by the lifecycle checkbox direction. An
independent duplicate-instance feature would require a separate request. No
initial On/Off state or automatic removal of profile references is inferred from
this clarification; existing data and emergency-stop state must be preserved.

### Q11 scope

The user chose “Only tailscale” for Xur-managed easy HTTPS, then clarified that
containers can still bind and publish ports for ordinary direct access. The
HTTPS convenience provides a distinct Tailscale app origin and an Open button,
forwarding to the app's configured HTTP port. This supersedes LAN discovery and
local DNS/certificate provisioning proposals for that convenience feature;
Tailscale service naming, authentication, and tagged-host integration remain
engineering work. Existing LAN management and trusted first-party application
access remain available.

### Implementation evidence

[Profile selection](../../src/Xur.Control/ProfileManager.cs) currently reuses a
runtime identity when its fingerprint matches another profile's workload;
[container preparation](../../src/Xur.Agent/ContainerLibrary.cs) stores concrete
volume IDs in recipes. The accepted direction preserves that container identity
and storage while changing lifecycle governance; it needs explicit ownership
handoffs rather than duplicate runtime or volume creation.

[Automatic model recovery](../../src/Xur.Control/AutomaticModels.cs) currently
retries failed starts indefinitely at one-minute intervals. The accepted Q8
bounded recovery policy for Persisted Compute needs new behavior.

[Tailscale enrollment](../../src/Xur.Control/Bootstrap.cs) currently derives the
administrator from the enrolling user's identity and invokes `tailscale up`
without tags. The tagged-host requirement for Tailscale Services needs a deliberate
enrollment and administrator-binding design.
[Local TLS](../../src/Xur.Control/LocalTls.cs) currently includes localhost, the
machine name, and local IP addresses in its certificate, with no per-app names.
Q11 does not add per-app local TLS provisioning; the Tailscale integration needs
extending within the accepted scope.

Proposed backup direction: persist service intent in the existing
[database](../../src/Xur.Control/ProfileStore.cs) and extend the readable
[profile export](../../src/Xur.Control/ProfileManager.cs) to include it. Actual
volume data needs separate backup and restore handling; exporting intent does
not back up application data. This remains part of the open delivery decisions.

## Decision round 3

The user answered on 2026-10-11. Q13 and Q14 are settled; Q12's revised direction
is completed by the accepted Q15–Q16 decisions below.

| ID | Decision | Outcome |
| --- | --- | --- |
| Q12 | Role of profiles | Revised direction: profiles assign resources to persisted compute. Separate service On/Off intent from resource assignment; Q15–Q16 settle precedence and disruptive changes. Supersedes the earlier membership-only handoff framing. |
| Q13 | Shared memory pressure | Accepted: keep running workloads. Report failed starts and memory readings without automatically stopping another workload to make room. |
| Q14 | Removal and stored data | Ask about stored data on every removal. Offer keep, delete and cancel explicitly. Off always retains data. Supersedes the proposed silent keep-by-default flow. |

## Decision round 4

The user accepted both recommendations on 2026-10-11. No product questions
remain open from the interview.

| ID | Decision | Outcome |
| --- | --- | --- |
| Q15 | Resource assignment precedence | Accepted: defaults plus active-profile overrides for selected GPUs and resource limits. On/Off remains independent. Use defaults when the profile has no override or is unloaded. |
| Q16 | Resource changes requiring restart | Accepted: show affected services and required restarts in the profile preview, then perform them as part of Apply. Preserve settings/data and restart Robotics disarmed. |

GPU selection is part of the current container's launch configuration and
[workload fingerprint](../../src/Xur.Domain/Profiles.cs); it cannot be presented
as a seamless live reassignment. Applying a resource-changing profile performs
the intentional service restart disclosed in its preview. Merely switching a
profile without changing a persisted service's resources must keep it running.
The UI should distinguish the effective resource assignment from the service's
own On/Off intent and identify which profile supplied an override.

### Engineering work after the product choices

Implement in this order using the settled scope. These are engineering tasks,
not another round of access questions.

1. Store one canonical container definition and durable lifecycle intent in the
   existing database. Retain runtime IDs and concrete volumes. Profile resource
   assignments refer to that definition, rather than making independent copies
   of its command, environment or storage. Changing lifecycle alone must not change its runtime
   fingerprint. Journal ownership handoffs with the same coordination used by
   profile transitions, preventing two reconcilers from starting one container.
2. Reconcile On/Off independently of profiles. Persist retry counts, failure state
   and intent revisions across manager restarts. Reject stale start/retry results
   after Off or a settings change. Use bounded backoff and a sustained healthy
   interval before resetting failures so a brief successful start cannot create
   an endless crash loop. Profile absence/unload must not turn a persisted service
   Off or delete its HTTPS identity. Coordinate intentional resource changes
   separately under Q15–Q16; automatic-model repair must not own these instances.
   Persist requested versus observed assignment during a transition so a failed
   restart cannot appear as successfully applied resources. Revalidate the preview
   against intent and profile revisions before applying it; Off during a profile
   transition cancels that service's start without changing its saved resources.
3. Extend the shared resource authority and process accounting. Recognize exact
   co-owner identities when starting or verifying release; stopping one item must
   not require the whole GPU to be idle. Keep profile-to-profile sharing outside
   this feature's expansion unless requested separately. Report memory pressure;
   shared GPU access cannot guarantee immunity from driver or host OOM behavior.
4. Implement operation-scoped robot device leases and verified release before
   removing the blanket workstation exclusion. Preserve calibration, recordings
   and the emergency-stop latch. The prototype reflects today's CPU-only Robotics
   image; enabling its GPU picker requires a compatible image and tested runtime
   device/training support, not merely changing the form. This remains part of
   delivering the requested GPU capability where supported.
5. Add per-item RAM/VRAM telemetry and the Tailscale integration described below.
   Retain the selected app address across lifecycle changes and restarts; withdraw
   only its own advertisement while it is off. Setup failures affect easy HTTPS
   status independently of container operation and ordinary port access.
6. Integrate the reviewed UI into the manager, extend configuration exports and
   storage-use references, then exercise the acceptance cases below. Restore of
   application volume contents remains separate from restoring service intent.

Scheduling questions are removed from this design's active decision tree.

## Engineering direction to test

Use durable service intent and explicit runtime ownership so profile planning
cannot turn a persisted item Off merely because it is absent from the target
profile. Resource-change restarts are intentional actions shown before Apply;
they preserve On/Off intent and address identity. Coordinate starts, stops,
GPU changes, and device leases through one host resource authority. Preserve
instance identity checks during stop/recovery; do not exempt arbitrary processes
because their names resemble a managed service.

### Tailscale integration proposal

Source-supported engineering proposal, not live-validated: use one Tailscale
daemon for the existing manager node Serve endpoint and separate application
service entries. The upstream
[Serve configuration](https://github.com/tailscale/tailscale/blob/v1.102.4/ipn/serve.go#L44)
supports both in one configuration, so a second enrollment is not inherently
required. The [toolchain lock](../../eng/toolchain-lock.json) pins 1.102.4,
above the Services minimum 1.86.0; see
[Tailscale Services prerequisites](https://tailscale.com/docs/features/tailscale-services).

[Tags replace a server's user ownership](https://tailscale.com/docs/features/tags),
but incoming [Serve identity headers](https://tailscale.com/docs/features/tailscale-serve)
still identify a connecting user client through `Tailscale-User-Login`; that user
header is absent when the connecting client is tagged. Preserve the confirmed
`administrator.json` binding before tagging the host. Update
[Bootstrap](../../src/Xur.Control/Bootstrap.cs) to clear stale `TailIdentity` in
`RefreshTailscale` and prevent owner-derived `ConfirmAdmin` on a tagged host;
server ownership must not replace the separately confirmed administrator.

Tailnet administrator service definitions, host approval, and grants remain
prerequisites; Xur does not automatically configure them today. A **Finish
Tailscale setup** action can represent unmet prerequisites. Restrict app changes
to service-scoped mutations, preserve the manager's `unix:serve.sock` target, and
serialize configuration changes. Prove that reboot and repair preserve both the
manager endpoint and app services before treating this integration as working.

For RAM, assess the service/container cgroup and its descendants rather than
summing only the main process. Linux's `memory.current` includes descendants and
accounts for more than anonymous application memory; label the metric
consistently. [Kernel cgroup v2 documentation](https://docs.kernel.org/admin-guide/cgroup-v2.html)
and [Podman statistics](https://docs.podman.io/en/latest/markdown/podman-stats.1.html)
describe the available accounting. This is a proposed source of per-item usage,
not an implemented Xur metric.

For VRAM, associate observed GPU processes with the actual item's process group,
including children, and aggregate by device. Treat missing or partial readings
explicitly. The current NVIDIA adapter supplies per-process values, while its
AMD/Intel fallback lacks that attribution. Driver reporting is conditional;
see [NVIDIA SMI documentation](https://docs.nvidia.com/deploy/nvidia-smi/index.html).
Do not attribute another service's memory or whole-device usage to this item.

Persistent routes need lifecycle-aware publication. Enforce the accepted Q6
browser trust boundary: arbitrary or user-selected application content uses
separate hostnames, host-only credentials, and strict origin/antiforgery checks.
Manager-origin paths require explicit trust in a first-party Xur-maintained UI.
Use Tailscale for Xur-managed easy HTTPS custom-app URLs; design service provisioning
and authentication within that accepted scope.

The GPU selector must enforce no GPU access or exactly the chosen devices,
including environment/image defaults. Selecting a GPU does not install userspace
drivers, make CPU-only training GPU-capable, or provide memory isolation.
The existing [vLLM launch settings](../../src/Xur.Domain/ModelLaunchSettings.cs)
default to 85% GPU memory utilization; shared access needs admission and tuning
decisions rather than assuming every current model configuration will fit.
Workstation shutdown also terminates that workstation user's processes, so
persistent services must have independent process identities.

Retain the repository's registry mirror rules, local browser assets and notices,
and named persistent-volume handling. Turning an item off must not erase stored data. Physical robot testing remains separate from source/browser
validation and requires its own attended acceptance.

## Planned acceptance evidence

1. With an active web request, switch and unload a profile without changing the
   service's effective resources; it keeps the same instance and address. Test
   resource overrides and any required restart separately under Q15–Q16, while
   preserving On/Off intent, stored data and address identity.
2. Restart the manager and reboot a test host; reconcile the agreed modes without
   duplicate instances, stale routes, or resumed robot motion.
3. Exercise crash recovery and explicit Off during a pending retry according
   to the agreed recovery policy. Verify that no scheduling UI or timer starts
   services in this version.
4. Run two services on selected GPUs and a CPU-only service. Verify visibility,
   stopping one without disturbing the other, and accurate attribution of child
   processes. Include profile/workstation coexistence.
5. Lose a GPU or peripheral and simulate startup/retry failure. Show actionable
   status and retain identities; never attach an unselected replacement.
6. Open a representative custom web app with assets, redirects, cookies, and a
   live connection through its Tailscale HTTPS address. Verify separation from
   privileged manager access and preservation through profile publication.
7. Test keyboard and touch flows, empty/error/stale states, 1440 px desktop,
   390 px mobile, and 320 px overflow. Use synthetic values for visual fixtures.
8. Change Robotics lifecycle using the same identity, saved settings, datasets,
   and emergency-stop state; ensure one owner and no automated arming. Prove
   operation-scoped controller/camera acquisition and release separately on
   physical hardware.

These are future validation requirements, not claims of completed tests.

## Skill provenance

Verified the upstream files on 2026-10-10:

- Anthropic [frontend-design](https://github.com/anthropics/skills/blob/41bbe19d1a1a7eaab5e7bb9050a417e5c6cffc8f/skills/frontend-design/SKILL.md), latest file change 2026-09-03.
- Matt Pocock [grill-with-docs](https://github.com/mattpocock/skills/blob/447ca70872026d5b79d6073a546dac082117fed7/skills/engineering/grill-with-docs/SKILL.md), latest file change 2026-08-15; invokes grilling and domain-modeling.
- [grilling](https://github.com/mattpocock/skills/blob/95249b0b49782349740fd9b8c6ce32b4e59e497a/skills/productivity/grilling/SKILL.md), latest file change 2026-10-07; asks each available decision round before proceeding to dependent choices.
- [domain-modeling](https://github.com/mattpocock/skills/blob/d80fa0f4ebe0c5714af0adf8670336065233ecc6/skills/engineering/domain-modeling/SKILL.md), latest file change 2026-09-17; records settled vocabulary in [GLOSSARY.md](../../GLOSSARY.md).

All four product decision rounds are recorded above. The accepted browser trust
boundary is recorded in [ADR 0001](../adr/0001-persisted-compute-web-origins.md),
and separation of service intent from profile resources in
[ADR 0002](../adr/0002-persisted-compute-resource-profiles.md). The requested
grilling interview has reached shared understanding; the remaining work is
engineering implementation and the acceptance evidence listed above.
