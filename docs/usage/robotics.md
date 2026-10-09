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

The xur-255 owner confirmed the three-omniwheel base and requires fully automatic
calibration using camera markers and a physical emergency stop. Assess camera
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
`captures` endpoints. The report marks duplicate IDs as ambiguous and does not
claim metric pose, synchronized stereo capture or movement-range coverage.
The controller selection can stay empty during camera setup. Controller motion
and demonstration recording still require a selected, connected controller.

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
   The installed upstream hand-guided capability is separate and does not meet
   the selected fully automatic workflow. No assessment result grants motion
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

Expected motor IDs must already be programmed. A missing or misidentified motor
is a setup error, not permission to reassign IDs or search for hard stops.
Readable decals provide observations; they do not establish hidden mechanical
limits or approve a picking policy.

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
workflow does not establish CV1 compatibility. Verify the actual CV1 runtime and
tracking on Windows first, then separately on Linux. Reuse the existing VR app's
native compositor and headset runtime first. If a custom headset app is needed,
Rust is an option for rendering, frame timing and the native XR loop. Add a
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
and cleanup must not delete them. The planned backup workflow creates a separate
durable copy on xur-epyc, with a dataset manifest, file checksums, verified-copy
status and visible errors. A backup is complete only after destination checksums
match. Remote backup is not implemented yet; retain originals and verify a
separate copy before starting destructive maintenance.

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

For an emote, record a single-arm demonstration without objects and use its
dataset path, episode number and bounded duration. Evaluate it under supervision,
then save the reviewed emote. **Trigger emote** and the emote API invoke LeRobot
replay. No untested pose presets are supplied.

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
the selected calibration approach remains fully automatic using markers;
the upstream hand-guided path does not satisfy that requirement.

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

## Remote GR00T N1.7 preparation

The [GR00T preparation kit](../../containers/gr00t/README.md) describes the
remote GPU workflow for xur-epyc, pinned source/model versions, a private
connection plan and the first demonstration-only inference test. Its .NET
console writes plans and checks saved GPU inventory without accessing motors.
Host access, GPU runtime and gated model access remain to be checked when the
server is powered on. XLeRobot needs its own calibrated demonstrations, dataset
conversion, custom embodiment and fine-tuned checkpoint before remote policy
predictions can enter a reviewed local rollout adapter.
