# Robotics workload

The XLeRobot recipe adds a disarmed robot session to a Xur profile. Its ASP.NET
Core APIs accept named tasks and skills. A private Python adapter runs the
upstream XLeRobot calibration/teleoperation helpers and LeRobot recording,
training, replay and rollout tools inside a local Podman container. Bus discovery
uses a separate inventory container with the upstream motor APIs and their
import dependencies; it downloads no policy or model weights. Clients
cannot supply joint targets or shell commands.

This is an initial implementation, reviewed on October 8, 2026. Local tests use
fake adapters and devices. The read-only discovery container was built and
automatic bus discovery passed on xur-255; direct model/firmware and idle
load/current/voltage reads also passed without changing motor settings. The full
CPU tools image was built on xur-255 and passed offline CLI/helper imports, ACT
loss/inference and synthetic video checks without device mappings. Physical
calibration, wireless controller operation, emotes and object placement still
need acceptance on the robot. No trained picking policy ships with Xur. The initial hardware
adapter expects the XLeRobot three-omniwheel model with two SO101 arms and a
two-motor head. Confirm that configuration before using it on another revision.

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

**Check markers** runs the native upstream detector in the prepared tools image
and saves three observations from each selected camera without connecting
motors or the controller. The [.NET marker survey](../../tools/Xur.Robotics/marker-survey.md)
also supports offline analysis of saved frames. Both report readability,
projected corners and shared camera references. On xur-255, the first installed
tray and arm labels decoded in repeated stationary captures from the onboard
cameras. These pixel observations do not approve joint calibration or motion.

API clients can submit `{"kind":"inspect-markers"}` to
`POST /api/robotics/tasks`, then retrieve the completed observations through
`GET /api/robotics/jobs/{id}/markers` and camera evidence through the job's
`captures` endpoints. The report marks duplicate IDs as ambiguous and does not
claim metric pose, synchronized stereo capture or movement-range coverage.
The controller selection can stay empty during camera setup. Controller motion
and demonstration recording still require a selected, connected controller.

The separate [attended motor-identification console](../../tools/Xur.Robotics/motor-identification.md)
can check one expected arm/head motor with a fixed small movement and live
camera/effort feedback before full calibration. It records the physical
response and finishes with torque off; it does not grant calibration approval.
On STS3215 firmware 3.10, position writes auto-enable torque, so measured-pose
seeding and final cleanup use zero drive output before the last torque-off write.

## Prepare an unconfigured robot

1. Create a profile with **XLeRobot** in the workload engine list and load it.
   Loading never connects a motor or resumes a previous motion session. Robotics
   and workstation workloads cannot share a profile or run concurrently.
2. Open **Robotics** and choose **Detect motor buses** before configuring devices.
   This builds a discovery container and uses upstream read-only inventory,
   exposing only the two serial adapters. It needs exactly two stable serial
   devices and responding motors. For the selected three-omniwheel XLeRobot it
   requires a complete eight-STS3215 left/head bus and nine-STS3215 right/wheel
   bus. Missing, extra, identical or incompatible inventories fail detection.
   Successful detection fills the bus selections using their persistent USB
   identities; it never enables torque, changes motor registers or grants
   calibration approval. This signature assumes the pinned upstream wiring;
   marker geometry must still validate the physical arm assignment.
   Then select the
   Xbox input device, head camera and hand camera. Save setup with motion disabled.
   Use the inventory's stable serial and camera paths. If the Xbox driver supplies
   no stable input path, select its current event device and recheck it after
   reconnecting. The host must already expose a paired controller through its
   Linux driver; a container cannot replace missing receiver firmware or a kernel
   driver. See [controller support](controllers.md).
3. Choose **Prepare tools container**. Preparation downloads the Python image
   through `mirror.gcr.io`, installs the pinned LeRobot/XLeRobot dependencies and
   builds the container on the host. A mirror cache failure is reported without
   falling back to Docker Hub. Preparation needs internet access and sufficient
   disk space for the ML packages. The resulting tools container has no network,
   no extra capabilities and access to only the five selected device nodes.
   Changing a selected device requires preparing it again.
4. Choose **Check controller** and **Inspect table**. Neither operation enables
   motor torque. Inspection captures both onboard cameras; recent jobs link to
   their private evidence. Assess the existing head and hand camera views first.
   An external camera can provide any missing joint references or views of
   nearby obstacles; it is not a prerequisite when onboard coverage suffices.
5. Support both arms and run the calibration console from an interactive terminal
   beside the robot:

   ```bash
   sudo /var/lib/xur/app/current/host/xurutil robotics calibrate
   ```

   The .NET utility enters the prepared container's upstream hand-guided
   calibration. Follow its prompts to place joints near their midpoint and move
   each arm/head joint by hand through a clear, safe range. Motor torque stays
   disabled. The routine records ranges, derives offsets, saves separate arm
   calibration files and checks their receipt automatically. It does not drive
   motors against hard stops. Finish calibration before preparing/replacing the
   container. Disable motion before recalibrating.
6. Check the saved calibration and available clearance. Establish robot-specific
   load/current limits from verified motor documentation and measured effort,
   then enter those limits and a conservative following-error limit in setup.
   There are no default force thresholds: raw load/current readings are not
   calibrated contact forces. Motion refuses missing limits or missing feedback.
   Each controller/policy/replay update checks upstream load, current, temperature
   and the previous joint goal before sending another action; a threshold breach
   aborts through the existing stop/disconnect path. These checks need physical
   acceptance and do not replace a physical power stop.
   Enable motion in setup,
   then arm a short session while an operator is present. Start with an empty
   table and supported arms. A motor-power stop switch is independent of software
   stop; verify how it cuts power before first powered evaluation. Software stop
   tries upstream disconnect, which disables torque, so an unsupported arm can
   drop. A process forced to exit cannot guarantee motor power is removed.

The upstream routine assumes the expected motor IDs are already programmed. A
missing/misidentified motor is a setup error, not permission to reassign IDs or
search for hard stops automatically. Fully automatic calibration is not provided:
it needs verified mechanical references, clearances and a physical power stop.
Arm/table fiducials could support pose and layout checks, but decals alone do not
establish safe joint limits. No decals are required for the hand-guided path.

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
avoidance. Wheel torque remains disabled. Motion recording/teleoperation claims
the selected controller exclusively and removes it from the Xur console while
the session is active.

## Record, train and evaluate

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

Disarm, then train an ACT policy with the dataset name and a new policy name.
Training is offline inside the tools container, uses CPU initially and starts
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
emotes and stop. It cannot arm, change devices, prepare containers, record/train
or approve/evaluate skills. Those operations use the authenticated manager or
its broader Automation access. An operator arms each short motion session.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/robotics/status` | Read setup problems, arming and current job |
| `GET /api/robotics/cameras/head` or `/hand` | Capture a camera frame while idle |
| `POST /api/robotics/tasks` | Inspect the table, check markers or run reviewed sorting skills |
| `POST /api/robotics/emotes` | Replay a reviewed named emote |
| `POST /api/robotics/controller` | Start a bounded Xbox session |
| `POST /api/robotics/stop` | Cancel the active tool and disarm |
| `POST /api/robotics/estop` | Request a software stop and persist a motion latch |
| `POST /api/robotics/reset-estop` | Operator-only fresh motor checks; reset leaves motion disarmed |
| `GET /api/robotics/jobs/{id}` | Inspect a returned job |
| `GET /api/robotics/jobs/{id}/captures` | List that job's camera evidence |
| `GET /api/robotics/jobs/{id}/markers` | Read a completed marker inspection report |

For camera inspection, send `{"kind":"inspect-table"}`; for repeated marker
detection, send `{"kind":"inspect-markers"}`. Once the actual `red-left`
skill has been trained/evaluated/reviewed and the operator has armed the robot:

```bash
curl "$XUR_URL/api/robotics/tasks" \
  -H "Authorization: Bearer $XUR_API_KEY" \
  -H 'Content-Type: application/json' \
  --data '{"kind":"sort","instructions":"Put the red block in the left bin", "items":[{"object":"red block","bin":"left","skill":"red-left"}]}'

curl "$XUR_URL/api/robotics/emotes" \
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
`/var/lib/xur/robotics`. Job state, captures and container build files are private
runtime data under its `.build/` directory. They are not included in source or
application archives. Profile unload stops the tools container. Agent recovery
stops a surviving container and leaves the robot disarmed.

## Source checks and remaining acceptance

Run `dotnet run --project tests/Xur.Unit.Tests -c Release -- --robotics` and
`PYTHONPYCACHEPREFIX=.build/robotics/pycache python3 tests/Xur.Integration.Tests/robotics-bridge.py`.
Run `tests/Xur.Integration.Tests/robotics-motor-probe.py` with the same Python
cache setting for the bounded diagnostic's startup/cleanup and feedback checks.
Tests cover unknown motor fields, exclusive operation ownership, calibration
receipts, scoped access, stop/expiry, local assets, range/temperature guards and
upstream pose seeding before torque enable. Tests do not certify collision safety.

After building the tools image, set `XUR_ROBOTICS_TOOLS_IMAGE` to its local image
reference and run the dependency checks without network or robot devices:

```bash
podman run --rm -i --network=none --cap-drop=ALL \
  --security-opt=no-new-privileges --entrypoint python \
  "$XUR_ROBOTICS_TOOLS_IMAGE" - < tests/Xur.Integration.Tests/robotics-tools.py
```

These checks import the upstream CLIs and Xbox helpers, compute a CPU ACT loss
and inference result from synthetic inputs, exercise the native marker detector
on synthetic frames, and round-trip a synthetic dataset video. They download no
model weights and do not connect to hardware. The tools
image is currently validated on Linux x86-64; other architectures need a
compatible CPU codec build rather than a GPU-wheel substitution.

Physical acceptance still needs complete confirmation of the expected joint roles,
camera coverage/orientation, wireless receiver disconnects, the physical power
stop and calibration, conservative teleoperation, supervised replay and a
successful pick-and-place evaluation with the chosen objects/bins. For xur-255,
the selected calibration approach remains fully automatic using markers;
the upstream hand-guided path does not satisfy that requirement.

The separate [Native AOT robot dashboard container](../../containers/robot/README.md)
is served by Xur's authenticated proxy at `/robot`. It includes camera snapshots,
per-motor register details, controller instructions and an AprilTag overlay/JSON
page at `/robot/tags`. Its independent GitHub Actions workflow publishes only
nightly images from `main`; loading/unloading the Robotics profile owns its lifecycle.
The auto-calibrate button currently performs a read-only assessment and reports
the unimplemented solver and missing references. Camera/motor snapshots pause
explicitly during exclusive robotics operations.

Upstream references:
[XLeRobot](https://github.com/Vector-Wangel/XLeRobot),
[XLeRobot software setup](https://xlerobot.readthedocs.io/en/latest/software/index.html),
[LeRobot](https://github.com/huggingface/lerobot).
Pins and retained licenses are recorded in
`tools/Xur.Robotics/upstream-lock.json` and the tools container.

The dashboard, AprilTags page and controller guide have a sticky **E-STOP**.
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
