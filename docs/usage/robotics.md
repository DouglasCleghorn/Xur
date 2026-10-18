# Robotics workload

The XLeRobot recipe adds a robotics application container to a Xur profile.
The main Xur package loads/unloads that container, mounts devices and persistent
state, and serves its authenticated proxy. **All robot settings are managed by
the ASP.NET Core app at `/robot/setup`**, including device selection, calibration,
effort limits, controller sessions, demonstrations, training and reviewed skills.
The app owns the task APIs and invokes the pinned Python LeRobot/XLeRobot adapter
inside the same container. No host-agent robot runtime or nested tools container
is needed. Clients cannot supply joint targets or shell commands.

The app is Native AOT compiled and serves local browser assets. The dashboard at
`/robot` shows cameras, every motor's available registers and session controls;
`/robot/tags` provides AprilTag overlays and copyable/downloadable raw data;
`/robot/controller` has the Xbox guide. Every page includes E-stop/reset.
Legacy `/robotics` redirects to the container's setup page. Legacy
`/api/robotics` requests relay to the same application for existing API clients.

The initial hardware adapter expects the XLeRobot three-omniwheel model with two
SO101 arms and a two-motor head. Confirm that model before using another revision.
Read-only bus discovery, motor identity/idle feedback and CPU upstream toolkit
checks passed on xur-255 on October 8, 2026 using the previous tools image.
The combined application image requires its own deployment checks. Physical
calibration, wireless controller operation, emotes and object placement still
need acceptance on the robot; no trained picking policy ships with Xur.

The xur-255 owner confirmed the three-omniwheel base and initially requested fully automatic
calibration using camera markers and a physical emergency stop. On 2026-10-11,
the owner authorized hand-guided setup for emotes, with an easy realtime app flow
and simulation while an operator is unavailable. Assess camera
coverage first; additional per-joint sensors have not been selected. The current
hand-guided tool is an existing upstream capability, not completion of that
automatic-calibration requirement. See the
[marker-calibration design and acceptance gates](../architecture/robot-marker-calibration.md).

The .NET [marker print-kit generator](../../tools/Xur.Robotics/marker-printing.md)
prepares official AprilTag sheets and individual QL-800 labels, including exact
reference sizes and upstream license notices. Its digital patterns passed native
detector checks; physical printing, mounting and camera coverage remain separate
acceptance steps.

**Check markers** runs the native upstream detector with the installed container tools
and saves three observations from each selected camera without connecting
motors or the controller. The [.NET marker survey](../../tools/Xur.Robotics/marker-survey.md)
also supports offline analysis of saved frames. Both report readability,
projected corners and shared camera references. On xur-255, the first installed
tray and arm labels decoded in repeated stationary captures from the onboard
cameras. These pixel observations do not approve joint calibration or motion.

API clients can submit `{"kind":"inspect-markers"}` to
`POST /robot/api/tasks`, then retrieve the completed observations through
`GET /robot/api/jobs/{id}/markers` and camera evidence through the job's
`captures` endpoints. Without supplied measurements the report stays pixel-only.
Configure measured intrinsics and actual tag reference edges in
**Camera and tag measurements** on `/robot/setup` to add competing metric
tag-to-camera candidates, positive-depth/reprojection checks and explicit planar
ambiguity. See the [measurement format](../architecture/robot-camera-metrology.md).
Neither mode approves joint calibration, synchronized stereo capture or
movement-range coverage.
The controller selection can stay empty during camera setup. Controller motion
and demonstration recording still require a selected, connected controller.

Select the correct capture interface when a USB camera provides RGB and infrared
streams. Setup lists stable `by-path` interfaces alongside cameras with only
`by-id` identities, deduplicating aliases of the same node. Generic by-id names
can collide; the app does not guess which stream is RGB. Check the image and
actual decoded dimensions. Changing camera selection removes the old calibration
approval receipt while retaining range files and recordings; existing metrology
continues to require an exact device-path match.

The [historical attended motor-identification evidence](../../tools/Xur.Robotics/motor-identification.md)
records bounded single-motor tests with camera/effort feedback before full
calibration. The former host-console command is removed; the app currently
provides read-only identity checks and has no powered identification endpoint.
The recorded tests finished with torque off and did not grant calibration approval.
On STS3215 firmware 3.10, position writes auto-enable torque, so measured-pose
seeding and final cleanup use zero drive output before the last torque-off write.

## Prepare an unconfigured robot

1. Create a profile with **XLeRobot** in the workload engine list and load it.
   Loading starts the robotics app disarmed and never resumes a previous motion
   session. Robotics and workstation workloads cannot share a profile or run
   concurrently. Container/image setup is the only robotics configuration in
   the main Xur manager.
2. Open `/robot/setup` and choose **Detect motor buses**. Discovery uses the
   installed upstream read-only inventory tools and needs exactly two stable
   serial adapters with responding motors. The three-omniwheel model requires
   an eight-STS3215 left/head bus and a nine-STS3215 right/wheel bus. Missing,
   extra, identical or incompatible inventories fail detection. Successful
   detection fills persistent USB identities without enabling torque, changing
   registers or approving calibration. This signature assumes upstream wiring;
   marker geometry must still verify the physical arm assignment.
3. Select the Xbox controller, head camera and hand camera, then save with motion
   disabled. Use stable serial/camera paths. Controller selection can remain
   empty for camera checks. A paired controller must already have a Linux input
   device; the app cannot supply missing receiver firmware or a kernel driver.
   If no stable controller path exists, select its current event device and
   recheck after reconnecting. See [controller support](controllers.md).
4. Choose **Check installed tools** to prepare device aliases, then **Check
   controller**, **Inspect table** and **Check markers** as appropriate. These
   operations do not enable motor torque. Dependencies are installed in the
   image, so setup does not build a second container or download model weights.
   If a device was absent when the workload loaded, reconnect it and reload the
   Robotics profile. Assess onboard camera coverage first; an external camera
   can fill missing joint references or obstacle views when needed.
5. Use **Auto calibrate** on the dashboard for the current read-only assessment.
   Fully automatic marker calibration remains under development. Missing
   measured marker geometry, camera intrinsics, joint coverage, justified travel
   limits or a verified physical motor-power stop retain a blocked state.
   Alternatively, open **Guided setup** at `/robot/calibrate` for the authorized
   hand-guided flow described below. No automatic assessment result grants motion
   approval or manufactures missing calibration files.
6. After approved calibration, establish robot-specific motor load/current
   limits from verified documentation and measured effort, and enter a
   conservative following-error limit in setup. There are no default force
   thresholds: raw effort readings are not calibrated contact forces. Motion
   refuses missing limits, calibration or feedback. Controller/policy/replay
   updates monitor effort, temperature and following error before each action;
   a threshold breach aborts through upstream stop/disconnect. These guards need
   physical acceptance and do not replace a motor-power stop.
7. Check clearance, enable motion in setup and arm a short session while an
   operator is present. Keep the base stationary and first verify the physical
   motor-power stop. Software stop requests torque-off cleanup, so an
   unsupported arm can settle. Process termination cannot guarantee that motor
   power is removed.

### Guided hand calibration

Open `/robot/calibrate` from the dashboard or setup page for real hand-guided
calibration. Close separate camera/controller tools first. The CV1
preview server reserves the same adapter lock even though it cannot move motors;
it must release that reservation before hardware calibration or recording.
Save hardware settings with motion disabled. Be beside
the robot, support both empty arms and keep the motor-power disconnect within
reach. The page requires all three confirmations. At each reference step, place
the listed joints midway through their clear travel and the gripper half open.
Move every listed joint by hand through clear travel, without forcing hard stops
or crossing an encoder wrap. Live cards show position, measured minimum/maximum,
span and temperature. A small span does not establish a safe operating range;
32 counts is only the minimum input-validity check. Review the actual clear
travel of every joint before **Save measured calibration**.

The private adapter uses the pinned upstream calibration primitives with torque
off, backs up the previous files/registers under `.build/evidence/hand-calibration/`,
and withholds approval while offsets are changing. It verifies torque off across
all seventeen motors, stops for missing/hot telemetry at 45°C, rejects goal and
torque-enable writes and preserves wheel configuration. Cancellation restores
the original arm/head calibration registers and verifies cleanup; the old
approval remains invalid, so inspect before starting again. Failed cleanup calls
for disconnecting motor power. Saving verifies register readback and a receipt,
then leaves motion disabled. Load/current limits and supervised motion review
are still required.

SignalR streams progress through server-sent events (long polling fallback) on
the authenticated proxy using locally bundled
assets. Each step uses a session ID and revision; stale/repeated acknowledgements
fail. The controlling page sends a heartbeat every three seconds. Losing it for
15 seconds stops setup; a page reload does not resume or take over the session.

### Simulation recordings and emote files

Use the upstream [XLeRobot MuJoCo environment](https://github.com/Vector-Wangel/XLeRobot/tree/b017b5e6354bd9f61f4247a920c72622ca0aade0/simulation/mujoco)
for authoring gestures outside the app. The generator uses the same pinned
XLeRobot commit as the robotics tools, with MuJoCo 3.3.0, NumPy 2.2.4,
Pillow 11.1.0 and imageio-ffmpeg 0.6.0. With Python 3.11 and venv support installed:

```sh
python3.11 -m venv .build/xlerobot-sim/py311
.build/xlerobot-sim/py311/bin/python -m pip install mujoco==3.3.0 numpy==2.2.4 pillow==11.1.0 imageio-ffmpeg==0.6.0
.build/xlerobot-sim/py311/bin/python eng/xlerobot-record-emotes.py --fetch --render --bundle
```

`--fetch` verifies upstream model, mesh and license blobs against the pinned Git
revision. Later runs use the verified local cache without network access.
Use `--arm right` or `--emote wave` to limit the recordings. Rendering requires a
working headless EGL renderer; omit both `--render` and `--bundle` for numeric
simulation checks alone. Outputs stay under ignored
`.build/evidence/xlerobot-emotes/`, in a separate timestamped directory per run.
To export videos and bundles from an existing completed rendered run:

```sh
.build/xlerobot-sim/py311/bin/python eng/xlerobot-record-emotes.py --export-run ".build/evidence/xlerobot-emotes/<run>"
```

The generator provides five gestures for each arm, using one empty arm at a time:

| Gesture | Movement | Duration |
| --- | --- | --- |
| Wave | Raised hand, two wrist/shoulder sweeps, return | 3.40 seconds |
| Welcome | Open gripper, extend and present outward | 3.08 seconds |
| Celebrate | Raised hand with two small victory pumps | 3.08 seconds |
| Curious | Raise the hand and examine both faces with a wrist turn | 2.88 seconds |
| Point | Hold an extended diagonal direction, then return | 2.68 seconds |

Each fast recording saves 100 Hz commands, actual simulated joint feedback and
velocities in `recording.jsonl` and `trajectory.npz`. Rendered runs also save
`preview.gif`; export adds a local H.264 MP4 and a portable `<gesture>-<arm>-fast.xuremot`
ZIP file. The MP4 preserves the 25 Hz rendered poses and real simulation pacing.
Legacy 10 Hz recordings and their original video timing remain importable. Export verifies video duration, decoded frame count and payload
checksums. Each bundle contains its versioned manifest, recording files, MP4,
metadata and license notices. It includes no encoder executable.

The run manifest records upstream provenance, dependency versions and the model
adaptation: equality constraints hold the base, wheels and head at zero while
retaining upstream arm geometry, inertias, actuators, limits and contact filters.
The generator rejects contacts, non-finite state, excessive tracking error/speed,
inactive-arm drift and failure to return to the initial pose. Target changes are
limited to one degree per sample; the denser 100 Hz recording supports
expressive timing without increasing that sample bound. These checks describe the simulated
model; physical clearances still require robot-specific review.

Open `/robot/emotes`, select one or more `.xuremot` files, then choose
**Import emotes**. Watch each video using its playback controls and inspect its
arm, duration and recording details. **Download emote file** preserves the
original bundle. Imports are limited to 16 MiB per file and stored under the
robot app's persistent `/state/emotes/imports/` directory. The importer validates
the documented archive entries, manifest, hashes and recording bounds.

Imported candidates are preview-only and labelled **Simulation recording · robot
mapping required**. Their six named upstream joint coordinates are radians,
with `hardwareApproved: false` and a null physical calibration hash. Importing
or watching a video does not calibrate, arm, approve or move the robot. Measured
joint-coordinate mapping, robot-specific limits and supervised evaluation are
still required before physical execution; no conversion from these candidates
to calibrated motor recordings is implemented. The app has no embedded simulator
or practice calibration mode.

### Existing upstream motion examples

The pinned upstream [keyboard teleoperation example](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/software/examples/4_xlerobot_teleop_keyboard.py)
contains a procedural rectangular end-effector trajectory. It is Apache-2.0
example code, not a downloadable social-emote recording. The official
[VR dataset recording example](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/software/examples/8_vr_teleop_with_dataset_recording.py)
provides demonstration capture. A search of the pinned tree and official docs on
2026-10-11 found no ready-made gesture pack.

The upstream community directory links the
[XLeRobot kinesthetic recorder](https://github.com/tianrui-li-0/xlerobot-kinesthetic-recorder),
which documents manually guided recording, JSON trajectory save/load and camera
recording during replay. Its inspected repository contains a README and script,
with no bundled gesture trajectories or visible license; Xur does not copy its
code or treat its recordings as compatible calibrated emotes. The ten generated
fast candidates above use Xur's original gesture definitions.

## Xbox control

Arm for 5–120 seconds and start a controller session. Release **Start** and
**Back** before starting. Hold **Start** as the deadman control; releasing it
holds the measured pose. **Back** stops the session. The session's end, timeout
or error disarms the robot and requires explicit rearming.

| Input while holding Start | Action |
| --- | --- |
| Left / right stick | Small Cartesian increments for the corresponding arm |
| Stick press + horizontal motion | Shoulder pan |
| Bumper + horizontal stick motion | Wrist roll |
| Bumper + vertical stick motion | Pitch |
| Corresponding trigger | Gradually close the gripper |
| Trigger + corresponding stick press | Gradually open the gripper |
| X / B; A / Y | Head pan; head tilt |
| D-pad | No base action |

The adapter seeds targets from measured positions instead of resetting to zero.
It preserves the gripper when its trigger is idle, limits each update relative
to the current observation, checks recorded joint ranges and stops at a
conservative 55°C motor temperature. Controller preparation is also limited to
30 degrees from its starting pose. These are joint guards, not collision
avoidance. Wheel torque remains disabled. Motion recording/teleoperation claims the selected controller exclusively inside
the app. The host keeps robotics and workstation profiles separate.

## Record, train and evaluate

The requested Oculus Rift CV1 headset and Touch controllers will connect to a
separate teleoperation computer. Prefer existing upstream applications: assess
[XLeVR](https://xlerobot.readthedocs.io/en/latest/simulation/getting_started/vr_sim.html)
and [LeRobot Isaac Teleop](https://huggingface.co/docs/lerobot/isaac_teleop) before
adding new client code. The documented XLeVR browser flow targets Quest 3;
Isaac Teleop provides SO101/OpenXR/recording integration, but its default CloudXR
workflow is excluded from planned client dependencies. Select open-source app
code and openly licensed model weights, reviewing weight licenses separately.
Reuse the open clutch/IK/recording components only with a verified local reader.
Monado/OpenHMD CV1 tracking is unverified; proprietary Windows Meta/Oculus or
SteamVR runtimes are optional existing compatibility routes, not selected or
installed dependencies. Verify the actual headset host/runtime before choosing a
path. Reuse the existing VR app's native compositor and headset runtime first.
If a custom headset app is needed, Rust is an option for rendering, frame timing
and the native XR loop. Add a
transport adapter only for a connection the upstream tools cannot supply. The
ASP.NET Core container app owns settings, mapping, arming and recording. CV1
integration is not implemented yet; the current recorder uses Xbox input.
See the [Rift CV1 reuse plan](../../tools/Xur.Robotics/vr/README.md) for runtime
checks, source-reviewed adaptation blockers and recording metadata.

Start with one easy-to-grasp object category and one reachable bin, in fixed
positions under good lighting. Keep people and fragile objects outside the arm
workspace. The operator chooses the actual objects and bin layout; there is no
universal pre-trained sorting policy in this workload.

Use **Record with Xbox controller** for a 5–60 second single-arm demonstration.
Record the approach, grasp, lift, placement and a safe ending pose. Both cameras,
the selected arm's state and the accepted upstream actions are stored as a local
LeRobot dataset. Reuse the same dataset name to append representative episodes;
arm, task, camera selection and calibration must match. Include examples across
the object positions you intend to use. Recording requires an armed session long
enough for the demonstration and stops on Back or expiry.

Keep original recordings in persistent `/state/datasets`; training, conversion
and cleanup must not delete them. Each recording creates an immutable local
snapshot. Configure the separate xur-epyc receiver in `/robot/setup` for automatic
remote backups with manifests, file checksums and visible retry/error status.
A remote backup is `verified` only after every destination file matches its
checksum. Unconfigured receivers remain `unconfigured`. Interrupted recordings
retain raw camera arrays and numeric frame journals; review them before training.

Disarm, then train an ACT policy with the dataset name and a new policy name.
Training is offline inside the robotics app container, uses CPU initially and starts
without downloading a pre-trained vision backbone. A small step count is a
pipeline smoke test, not evidence that the policy can pick safely. Training can
take substantial time; practical demonstration counts and training steps depend
on the actual task. Checkpoints are retained locally. Typical asset paths are:

```text
/state/policies/red-left/checkpoints/last/pretrained_model
/state/datasets/wave-right
```

Fill in a sorting skill with the trained checkpoint, demonstrated task, arm,
fixed bin ID and maximum duration. Arm briefly and use **Evaluate with operator
present** before checking its review box and saving it. Evaluation grants no
persistent skill approval. Verify picking/placement and clearances in person and
using camera evidence. Review binds the skill to the current calibration;
recalibration invalidates it. Sorting camera selections must match its training
demonstrations. Re-evaluate after changing objects, lighting or the bin layout.

Setup's **Emotes** section offers **Wave**, **Welcome**, **Celebrate**, **Curious**
and **Point** recording ideas. Choose the left or right arm, then prepare a
recording to fill in the demonstration name, task and suggested duration. These
buttons only fill the form. Record the gesture with Xbox control using one empty
arm, keeping its movement within the robot's accepted clearance and returning to
its starting pose. You can also record your own gesture.

After recording, choose **Use recording details for an emote** to fill the skill
review form with its actual dataset name and arm. Select the episode you recorded
and its bounded duration; recordings added to an existing dataset create another
episode, so episode zero may refer to an older recording. Arm for a supervised
evaluation, then disarm and save the reviewed skill. Your recorded emotes appear
as named cards with **Play** and **Review** controls. Playback requires a current
calibration review and an armed operator session, and ends disarmed. Changing a
skill's form details clears the review checkbox. The emote API invokes the same
LeRobot replay workflow. Gesture ideas contain no motor targets or prerecorded
motion assets. Imported `.xuremot` candidates on `/robot` have their own
video library and cannot be played through this physical replay workflow.

## Task API

Create a **Robotics** API key for the assistant after setup. It can inspect status,
jobs, camera evidence and invoke controller sessions, reviewed sorting skills,
emotes and stop. It cannot arm, change devices, prepare hardware, record/train
or approve/evaluate skills. Those operations use the authenticated robotics app through Xur's proxy, or
its broader Automation access. An operator arms each short motion session.

| Endpoint | Purpose |
| --- | --- |
| `GET /robot/api/status` | Read setup problems, arming and current job |
| `GET /robot/api/cameras/head` or `/hand` | Capture a camera frame while idle |
| `POST /robot/api/tasks` | Inspect the table, check markers or run reviewed sorting skills |
| `POST /robot/api/emotes` | Replay a reviewed named emote |
| `POST /robot/api/controller` | Start a bounded Xbox session |
| `POST /robot/api/stop` | Cancel the active tool and disarm |
| `POST /robot/api/estop` | Request a software stop and persist a motion latch |
| `POST /robot/api/reset-estop` | Operator-only fresh motor checks; reset leaves motion disarmed |
| `GET /robot/api/jobs/{id}` | Inspect a returned job |
| `GET /robot/api/jobs/{id}/captures` | List that job's camera evidence |
| `GET /robot/api/jobs/{id}/markers` | Read a completed marker inspection report |

For camera inspection, send `{"kind":"inspect-table"}`; for repeated marker
detection, send `{"kind":"inspect-markers"}`. Once the actual `red-left`
skill has been trained/evaluated/reviewed and the operator has armed the robot:

```bash
curl "$XUR_URL/robot/api/tasks" \
  -H "Authorization: Bearer $XUR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data '{"kind":"sort","instructions":"Put the red block in the left bin", "items":[{"object":"red block","bin":"left","skill":"red-left"}]}'

curl "$XUR_URL/robot/api/emotes" \
  -H "Authorization: Bearer $XUR_API_KEY" \
  -H 'Content-Type: application/json' --data '{"skill":"wave"}'
```

Motion requests return `202` and a job ID. A sorting job runs 1–8 specified skills
sequentially and captures head-camera evidence before/after each. Each fixed ACT
policy receives its demonstrated task; arbitrary text does not teach a new pick
or choose a new placement. Object descriptions and bin/skill choices are retained
for operator/assistant review. A finished policy yields `awaiting-verification`,
not a claim that the object landed correctly. Inspect evidence before retrying;
never automatically retry a failed physical action.

Persistent setup, calibration, datasets and policies live under
`/var/lib/xur/robotics`, bound at `/state` inside the app container. Job state and
captures are private runtime data under its `.build/` directory and are excluded
from source/application archives. Profile unload requests stop/disarm before
removing the container. Container startup/recovery leaves the robot disarmed.

## Source checks and remaining acceptance

Run `dotnet run --project tests/Xur.Unit.Tests -c Release -- --robotics` and
`PYTHONPYCACHEPREFIX=.build/robotics/pycache python3 tests/Xur.Integration.Tests/robotics-bridge.py`.
Run `tests/Xur.Integration.Tests/robotics-motor-probe.py` with the same Python
cache setting for the bounded diagnostic's startup/cleanup and feedback checks.
Tests cover unknown motor fields, exclusive operation ownership, calibration
receipts, scoped access, stop/expiry, local assets, range/temperature guards and
upstream pose seeding before torque enable. Tests do not certify collision safety.

After building the combined app image, set `XUR_ROBOT_IMAGE` to its local image
reference and run the dependency checks without network or robot devices:

```bash
podman run --rm -i --network=none --cap-drop=ALL \
  --security-opt=no-new-privileges --entrypoint python \
  "$XUR_ROBOT_IMAGE" - < tests/Xur.Integration.Tests/robotics-tools.py
```

These checks import the upstream CLIs and Xbox helpers, compute a CPU ACT loss
and inference result from synthetic inputs, exercise the native marker detector
on synthetic frames, and round-trip a synthetic dataset video. They download no
model weights and do not connect to hardware. The upstream toolkit was validated on Linux x86-64; other architectures need a
compatible CPU codec build rather than a GPU-wheel substitution.

Physical acceptance still needs complete confirmation of the expected joint roles,
camera coverage/orientation, wireless receiver disconnects, the physical power
stop and calibration, conservative teleoperation, supervised replay and a
successful pick-and-place evaluation with the chosen objects/bins. For xur-255,
hand-guided setup is now authorized for emotes. It does not complete the
separate automatic marker-calibration work.

The [Native AOT robotics application container](../../containers/robot/README.md)
is served by Xur's authenticated proxy at `/robot`. It includes camera snapshots,
per-motor register details, controller instructions and an AprilTag overlay/JSON
page at `/robot/tags`, with all setup/training/skill settings at `/robot/setup`. Its independent GitHub Actions workflow publishes only
nightly images from `main`; loading/unloading the Robotics profile owns its lifecycle.
The auto-calibrate button currently performs a read-only assessment and reports
the unimplemented solver and missing references. Camera/motor snapshots pause
explicitly during exclusive robotics operations.

Upstream references:
[XLeRobot](https://github.com/Vector-Wangel/XLeRobot),
[XLeRobot software setup](https://xlerobot.readthedocs.io/en/latest/software/index.html),
[LeRobot](https://github.com/huggingface/lerobot).
Pins and retained licenses are recorded in
`tools/Xur.Robotics/upstream-lock.json` and the robotics app container.

The dashboard, setup page, AprilTags page and controller guide have a sticky **E-STOP**.
This software stop persists across restarts and cancels the active tool; it does
not switch off the power supply. Reset checks fresh feedback from both buses
and requires all seventeen motors to report torque off, stationary and no status
fault. A reset leaves motion disarmed. Robotics API keys may latch E-stop, while
reset is reserved for an authenticated operator.
## Recording preservation and remote backups

The container app preserves byte-for-byte local snapshots after completed or
interrupted recording sessions and retains the original datasets. Configure the
HTTPS receiver and its dedicated token in `/robot/setup`. Verification, retry
status and interrupted-episode labels appear per recording. See the
[receiver setup and recovery guide](../../containers/robot-cluster/README.md).
