# Marker-only automatic calibration

Status: stationary marker observations assessed and live read-only inspection
implemented/tested, October 8, 2026. The owner
confirmed a three-omniwheel XLeRobot and selected camera markers plus a physical
motor-power emergency stop. Per-joint reference sensors are outside the selected
approach. No automatic powered calibration has been enabled or validated. The
container now supports optional read-only metric pose candidates from supplied
measured intrinsics and tag sizes; this prototype does not establish those
measurements on xur-255. See [camera/tag metrology](robot-camera-metrology.md).

## Reusable setup, with robot-specific evidence

Camera roles are operator-selected inventory paths, not camera-brand checks or
assumed `/dev/videoN` numbers. The xur-255 owner identifies its Lenovo camera as
the head camera; this is local setup information, not a default for other robots.
Exclude virtual cameras and metadata-only video nodes from coverage capture.
Store intrinsics, marker mounts, robot geometry and coverage results per setup.
Replacing a camera, changing its mounting or changing the arm model invalidates
the corresponding assessment and calibration evidence.

The first motion adapter targets the confirmed XLeRobot with three omniwheels.
Other robot models need an upstream adapter with its own inventory, geometry and
verified limits. Keep the public task/coverage/calibration workflow independent
of those adapter details; never apply XLeRobot motor IDs or limits to another
model merely because it has two arms and webcams.

## First: stabilize USB and assess visibility

Initial xur-255 observations on October 8, 2026: diagnostic SSH was enabled
through the authenticated manager. The Xbox receiver (`045e:02e6`) bound to the
existing `xone-dongle` driver; pairing was skipped at the owner's request. A hub
then exposed EMEET C950 (`328f:0073`) and Lenovo 500 (`17ef:482f`) cameras. Both
returned stationary frames, including captures after a two-second exposure
settling period. The host boot ID and Xur service restart counts remained
unchanged during these observations. Two `1a86:55d3` motor USB adapters then
enumerated as `ttyACM0`/`ttyACM1`, with distinct persistent serial identities
ending in `7920`/`7688`; arm assignment was initially unknown. Neither
motor bus was opened or commanded during these USB-only checks, which did not reproduce the
reported failure. At approximately 14:50:08 MDT the log streams stopped; the
owner confirmed that the computer turned off when the motors were connected
to the Anker Solix. This establishes whole-host power loss during the power
connection, without establishing its electrical cause. The owner identified a C200 with all loads on USB-C and the computer on C2.
Anker documents C2 falling from 100 W to 60 W when all three port groups are
active ([C200 power table](https://support.ankersolix.com/s/article/Anker-SOLIX-C200-DC-Portable-Power-Station-Guide-de-l-utilisateur-A1727)).
A USB-PD allocation change is a plausible explanation, not a verified cause.
Motor-port assignments, adapter voltage ratings and any station fault indication
are pending. Use a separate, correctly rated computer supply for diagnosis when
available, so motor-power changes do not take down monitoring.
Keep motion disabled until that layout is understood.

Power notes reported by the owner later on October 8: C1 does not boot the
computer, C2 starts it but then shuts down, and C3 keeps it online. These are
observations of this computer/cable/load combination, not defaults for every
C200 or robot. Use C3 for this host during further observation, verify its
stability with the actual connected loads, and retain the independent-supply
option for isolating motor-power faults. C3 working does not establish that the
motor adapters provide the correct voltage or that hot-plugging is safe.

The owner also asked whether the C200 could restart motor power through USB-C
port control. Source review on October 8 found per-port power/status telemetry
in the community API's `A1727` model mapping, but no independent USB-C switch
commands for that model. The charger-specific USB switch commands belong to
other device models and must not be applied to the C200. See the
[reviewed C200 mapping](https://github.com/thomluther/anker-solix-api/blob/643cd5b16f6bc82055c74f15966b3ffab5679bf6/src/anker_solix_api/mqttmap.py#L4713).
This is evidence of what that implementation supports, not proof that no
firmware could ever provide the feature. No Anker account was accessed and no
station power command was sent.

Use a model-independent motor-power provider for a future restart tool, with an
explicit unavailable state until a verified provider exists. The provider must
switch only the motor supply, keep the host supply live, stop/disarm before a
restart and leave motion disarmed afterward. Verify loss/restoration of motor
power and unchanged host uptime during acceptance. A remote restart relay must
not replace or bypass the physical motor-power emergency stop.

The owner requires automatic bus-role detection. The current implementation runs the installed read-only LeRobot discovery
tool inside the robotics application container and uses a .NET matcher for the
selected upstream motor inventory, persisting candidate roles by serial
identity. After the owner restored the computer on C3, physical discovery
identified `7688` as the eight-motor left/head bus and `7920` as the nine-motor
right/wheel bus; every responding motor reported model 777 (STS3215). This used
upstream broadcast discovery and explicitly disconnected without disabling
torque; it wrote no motor registers. The serial node numbers changed after the
reboot, confirming why roles must use persistent identities. A complete inventory signature
cannot prove that a modified robot's physical arms follow upstream wiring;
marker geometry must verify that association before motion is approved.

A subsequent upstream read-only health capture at 21:13:49 UTC found every
motor stationary with torque disabled, zero reported load/current, and
temperatures from 26 to 35 degrees Celsius. Voltage registers were 49–50 on
the right/wheel bus and 120–121 on the left/head bus. Feetech documents voltage
feedback in 0.1 V units, implying approximately 4.9–5.0 V and 12.0–12.1 V
respectively ([feedback units](https://www.feetechrc.com/20210430-56680.html)).
The manufacturer offers both 7.4 V and 12 V STS3215 variants
([product inventory](https://www.feetechrc.com/products.html?keyword=STS3215));
model 777 does not prove a particular voltage rating. Supply/trigger ratings
and connections remain unverified. These idle readings are not validated force
limits and do not approve either bus for motion. No torque settings were changed.

For the standard three-omniwheel build, upstream explicitly specifies seventeen
12 V STS3215 motors and two 12 V USB-C-to-DC power cables
([pinned parts list](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/docs/en/source/hardware/getting_started/material.md)).
Thus the design expectation is 12 V for both buses, covering arms, head and
wheels; the installed hardware variant still needs independent confirmation.
Anker's C200 DC specification identifies C1 as a 5 V-only output
([C200 specification](https://support.ankersolix.com/s/article/Anker-SOLIX-C200-DC-Portable-Power-Station-Guide-de-l-utilisateur-A1727)).
If the right/wheel supply is on C1, that would explain its approximately 5 V
reading, but that port association has not been confirmed. Neither motor labels
nor configurable protection registers should be inferred from current supply
voltage. Do not increase a supply voltage solely from the shared model ID.

Monitoring later stopped around 15:17 MDT, with the last service-health sample
showing the same boot ID, all three Xur services active and zero restarts. SSH
and HTTPS then timed out and Tailscale reported the host offline. The owner
subsequently confirmed unplugging the computer to inspect labels; this was not
an unexplained crash. A planned read-only voltage-limit capture did not execute because its
SSH connection failed. The cables have no voltage markings according to the
owner; motor/adapter labels, purchase records and motor-port connections remain
pending.

After the owner reconnected the computer, diagnostic SSH and monitoring were
restored on boot `f81e6b2c-9518-42ae-926d-5dd039225989`. At 21:26:45–46 UTC,
direct upstream reads of every motor's `Model_Number` register (address 3,
two bytes) returned 777; firmware registers returned 3.10 for all seventeen.
Every motor's configured minimum/maximum voltage-protection registers were
40/140, corresponding to 4/14 V. These configurable protection settings are
consistent with the standard 12 V build but do not certify the hardware variant
or define its safe operating range. All torque-enable registers remained zero.
The new present-voltage readings were 120–121 on right/wheels and 50 on
left/head, the inverse of the previous supply association. This points toward
the power connections rather than different motor families, but actual port
assignments and adapters remain unconfirmed. No motor register was written.
Evidence: `.build/evidence/robotics-usb/boot-f81e6b2c/motor-identity.json`.

The owner later moved the motor feed previously on C1 to an external power
supply. Read-only captures at 23:14:35 and 23:15:26 UTC confirmed approximately
12 V on both buses. The repeat showed right/wheels at 12.0–12.1 V and left/head
at 12.1–12.2 V, all torque disabled, zero reported load/current and no moving
flags. Right motor 9 initially reported 11.1 V and a moving flag; its repeat was
12.1 V with that flag cleared and only one raw encoder count difference. No
motor command or setting change was issued. The host retained its boot ID and
all three Xur services reported zero restarts. The external supply's output
rating and current capacity remain pending; these are idle observations, not a
powered load test. Fresh host/USB monitoring was started under the ignored
`boot-f81e6b2c/external-supply/` evidence directory.

The Lenovo initially returned dark frames; opening its physical shutter
restored the image. Its stationary front view did not show the arm joints. The
EMEET view contains parts of the chassis/arm and floor, without enough visible
references to assess every joint. The current onboard views do **not** establish
full joint coverage. A stationary external view angled down from the front or
side was requested, and the owner deferred adding this camera until later.
Assess the existing head and hand cameras first, including whether a downward
head-camera aim and known marker mounts provide enough joint references. A head
adjustment before full calibration must use the bounded setup procedure below;
the normal task and teleoperation paths remain blocked. Request an additional
view only after identifying the references that the onboard cameras cannot
resolve. No automatic calibration feasibility claim follows from these unmarked
images.
Private frame and log evidence lives in `.build/evidence/robotics-usb/`; camera
captures are registered in `docs/screenshots.md` and are not publication assets.

Later on October 8, after the owner mounted printed tag 00 on the tray, a fresh
1920×1080 head frame showed a downward view of the tray and parts of both arms.
The native upstream `tagStandard41h12` detector decoded ID 0 with zero bit
errors; its reference square spans approximately 44×40 image pixels. The hand
camera faced tray mesh and yielded no detections. This demonstrates physical
label readability in that head-camera view, superseding the earlier forward-view
coverage observation. It does not establish every joint's visibility, camera
intrinsics or a known marker-to-chassis transform. The printed small label has
a nominal 15.24 mm reference edge, still requiring physical measurement. Evidence
is under `.build/evidence/robotics-markers-camera/`. No motor command was issued.

After the owner attached arm tags, the head view decoded 00 on the tray,
01 near the image-left wrist/end bracket and 02 near the image-right
wrist/forearm. The hand view also decoded 01, providing a shared visible tag.
Two five-frame stationary captures repeated those detections in every frame
with zero corrected bits and less than 0.2 pixels of center spread. Fresh
upstream reads found all seventeen motors with torque disabled, zero reported
load/current, 31–36°C and approximately 12.0–12.2 V. These measurements involved
no motor register writes; they do not establish powered effort limits.

The new [standalone .NET/C survey tool](../../tools/Xur.Robotics/marker-survey.md)
records projected corners, detection presence, duplicate-ID ambiguities and
shared camera references. It has no motor or camera APIs and does not issue
calibration approval. Evidence is under `.build/evidence/robotics-marker-survey/`.
Candidate visual link associations have not been accepted as bus-role or
marker-mount transforms. Tag 02's paper visibly bows: decoding it successfully
does not certify the planar geometry needed for pose estimation. The observed
end tags support further kinematic assessment, but these stationary frames do
not identify camera intrinsics, joint zero offsets or travel limits.

Bring up the robot with the suspect USB devices disconnected. Establish
diagnostic SSH, collect boot/service/USB inventory and watch kernel, udev,
Xur-service and network logs before reconnecting one item at a time. Preserve
the evidence on both the host and the diagnostic client, so a service or network
failure does not discard the preceding events. Distinguish a process crash from
a kernel fault, USB reset/power problem, whole-host restart or network loss.
Keep motor motion disabled throughout this diagnosis.

After USB is stable, collect stationary head- and hand-camera frames. Establish
which camera belongs to which arm and record camera resolution, lens distortion
and the view of the base, shoulder, elbow, wrist and gripper on both arms. An
external stationary webcam can supply missing coverage if the onboard cameras
cannot see all required references. Placement and lens choice await these views.

A moving head camera is a valid candidate, but its pose relative to the robot
must be established from visible fixed-chassis references with known geometry
or an independently verified camera-to-base transform. Do not assume its pose
is fixed or use uncalibrated head-joint readings as ground truth. Coverage should
identify unresolved joints and ambiguous poses rather than require an external
camera by default.

Use markers on the fixed base and moving links, with mounts whose position and
orientation relative to the corresponding CAD link are known. A marker on only
the gripper does not independently establish every intermediate joint angle.
Some links may need markers on more than one face to remain visible. Assess
occlusion, blur, tag pixel size, viewing angle and pose ambiguity at the actual
working distance before selecting tag sizes and placement. A missing or
ambiguous joint reference blocks automatic calibration rather than guessing its
pose. Printed placement templates depend on the exact arm revision and mount
geometry; none have been prescribed yet.

AprilTag provides marker detection and pose estimation; its upstream pose
estimator requires measured tag size and camera intrinsics. These are inputs to
the visibility assessment, not a motor-safety certification. See the
[AprilTag pose-estimation documentation](https://github.com/AprilRobotics/apriltag#pose-estimation).

## Proposed marker kit

Use black-and-white AprilTags from the `tagStandard41h12` family, recommended by
[AprilTag upstream](https://github.com/AprilRobotics/apriltag#choosing-a-tag-family).
Each mount has a unique tag ID and a recorded, measured transform to its robot
link or workspace reference. Treat this as a proposed kit until camera coverage
and mounting geometry have been measured. Detection works in the saved-frame
survey and the live read-only workload task; automatic calibration remains
unimplemented.

The workload now also implements the read-only `inspect-markers` task using
that pinned detector in its application container. It captures three observations
from each selected camera, validates and persists pixel geometry in .NET, and
exposes a job report and camera evidence. This adds live observation to the
workload. Optional supplied measured metrology adds competing visual tag-to-camera
poses with ambiguity and reprojection checks; unconfigured surveys remain
pixel-only. It does not implement the calibration or bounded setup-motion phases
below, create calibration receipts, or grant motion authorization.

At 20:03 MDT the updated diagnostic tools image decoded 00/01/02 in all three
head frames and 01 in all three hand frames at 1920×1080. No serial or controller
devices were passed to that container. The .NET task API accepted those real
observations, persisted the report and served both JPEGs in a local integration
host; this is not a deployed update to the robot's Xur manager. A fresh read-only
motor check at 20:08 MDT found all seventeen motors with torque disabled, no
reported motion/load/current, 31–36°C and approximately 12.0–12.2 V. Neither
observation path issued motor register writes.

Print with crisp edges on matte white material, retaining the complete official
pattern and its surrounding margin. Mount tags flat on rigid surfaces or small
brackets; do not wrap them around cylindrical joints, cover joint clearance or
put them on motor shafts. Multiple visible faces on a link can use distinct IDs
with known transforms when a single face would become occluded.

Provide references on the fixed chassis and the moving links needed to resolve
each arm/head joint, including the gripper's moving parts where mechanically
feasible. A tag on only an arm's tip cannot identify every intermediate angle.
Separate workspace tags can identify the table and bins. A camera-calibration
target supplies intrinsics; object/bin tags do not replace that calibration.

Choose physical tag dimensions after the camera-coverage assessment
at the actual working distance. The pose estimator's tag size is the edge
between its detection corners, not the full paper/sticker width. Print at actual
scale and measure that edge after printing. Store both the family and measured
size per marker, allowing different sizes and robot models without fixed serial
numbers or camera brands. The .NET
[print-kit generator](../../tools/Xur.Robotics/marker-printing.md) now prepares
official sheet templates and individual QL-800 labels with separate recorded
reference sizes. Rendered sheets and label rasters passed native AprilTag checks.
Exact placement, physical dimensions and camera coverage remain pending.

## Bounded movement before full calibration

Full robot calibration is not a prerequisite for every motor movement. The
pinned LeRobot bus API can read raw encoder position and command a small relative
change without normalized joint coordinates. Its STS3215 table exposes a runtime
`Torque_Limit` register, distinct from the persistent `Max_Torque_Limit`.
See the pinned upstream [raw motor API](https://github.com/huggingface/lerobot/blob/30da8e687a6dfc617fcd94afc367ac7071c376ce/src/lerobot/motors/motors_bus.py)
and [Feetech control table](https://github.com/huggingface/lerobot/blob/30da8e687a6dfc617fcd94afc367ac7071c376ce/src/lerobot/motors/feetech/tables.py).
These capabilities do not establish a safe torque value for the installed robot;
no limiting-register writes or powered tests have been performed. The current
Xur adapter monitors effort but does not yet apply this runtime torque cap.

Add an internal coverage-setup operation through the upstream motor API, with
the .NET workflow enforcing the following gates before any powered movement:

- Verify the selected head joint's physical identity, position operating mode,
  mechanically justified local encoder interval and clear travel. Full arm
  offsets are unnecessary for this local interval; motor identity and clearance
  remain necessary. Keep the base and arms disabled and arrange passive support
  for any assembly that can fall when torque is limited or removed.
- Check the tested motor-power emergency stop. Read and preserve the selected
  motor's initial state. With torque disabled, seed its goal from the current raw
  position, apply a verified low motor-side torque cap and nonzero conservative
  speed/acceleration limits, and verify their readback before enabling that
  motor. Do not run upstream whole-robot configuration or enable other joints.
- Use one joint at a time, tiny relative steps, a bounded cumulative excursion
  within the verified local interval and a short absolute timeout. Refuse encoder
  wrap ambiguity, invalid protection settings and stale or incomplete telemetry.
- Monitor position, following error, load, current, temperature and relevant
  camera observations. Abort on unexpected motion, resistance, loss of required
  observations or a stop event. An insufficient cap must stop the attempt rather
  than trigger an automatic increase in force.
- End with torque disabled and retain the observations. Restore temporary
  limiting settings only with torque off; never replay an old goal or enable
  torque as part of cleanup. A coverage adjustment does not approve calibration
  or authorize normal robot tasks.

Torque limiting reduces motor effort; it does not bound contact force for every
link geometry or prevent gravity-driven motion. The bootstrap operation remains
planned, not implemented or validated on xur-255. Public clients should request
camera-coverage assessment, not supply raw motor targets or torque values.

## Intended automatic sequence

1. Check the physical emergency-stop state, power interlock and camera/device
   identities. Keep the base disabled and refuse unexpected motor IDs.
2. Capture initial marker observations and raw encoder readings without motion.
   If a head adjustment is needed, use only the bounded setup operation above
   after its independent gates pass. Validate camera intrinsics, marker-to-link
   mounts and joint reference coverage against the confirmed robot model.
3. Derive candidate encoder offsets from measured parent/child link poses and
   known joint axes. Check consistency across independent views/frames, marker
   reprojection errors and alternative pose solutions. Persist the evidence and
   candidate calibration separately from the approved LeRobot calibration.
4. Obtain conservative joint limits from verified mechanical/model references.
   Markers establish observed pose; they do not reveal a hidden mechanical stop
   or certify an unseen collision. Do not discover limits by blindly driving
   until a motor stalls. If limits cannot be justified, retain the blocked state.
5. Only after the interlock, limits and collision-free verification envelope are
   established, perform bounded low-speed verification motions using the
   upstream robot tools. Verify observed motion, encoder direction, offset
   consistency and marker tracking. Loss of required references, stale frames,
   excessive disagreement or a stop event aborts verification.
6. Produce the upstream calibration files and identity/hash receipt after all
   required joints pass. Invalidate existing skill reviews and leave the robot
   disarmed. Automatic calibration success does not approve a picking policy.

The calibration state machine, settings and public API belong in the Native
AOT .NET robotics app at `/robot`. Marker detection and upstream interactions
run as Python subprocesses inside that same container. The main Xur package
only loads/unloads the workload, maps devices/state and authenticates its proxy. Public commands should request coverage assessment, candidate
calibration and bounded verification; they should not expose joint targets.
The physical emergency stop must remove motor power independently of Xur,
USB, camera tracking or the host's ability to process an API stop.

## Evidence needed before implementation can drive motors

- Exact arm/head construction and marker-to-link geometry.
- Stationary camera views showing all joint references, with calibrated camera
  intrinsics and measured marker dimensions.
- Verified motor/encoder identities and mechanically justified safe limits.
- A tested motor-power emergency stop and a way for the calibration workflow
  to observe its state; feedback wiring remains to be determined.
- A supported initial pose and a clear verification envelope that does not
  depend on an operator moving joints by hand during calibration.

This design may support automatic offset calibration without an initial powered
sweep when sufficient references are visible. Feasibility for every joint and
the gripper remains an open question until camera and mechanical evidence exist.
