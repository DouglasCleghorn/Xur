# Motor identity and historical attended diagnostics

Current motor identity is available through **Detect motor buses** at
`/robot/setup` and the read-only motor details on `/robot`. These run the upstream
tools inside the robotics app container without changing motor settings.

The former `xurutil robotics identify` host-console entry point has been removed
as robot settings and execution moved into that container. The app intentionally
does not expose a powered motor-identification or raw motor-write endpoint.
No replacement host command is required for current read-only identity checks.

The remaining sections document the internal bounded diagnostic and October 8
evidence. It tested one expected arm/head motor, recorded encoder/effort feedback
and camera views, and finished with torque off. Those tests did not approve
calibration. Any future operator-facing diagnostic must be added to the app's
reviewed workflow before it can be used through the API.

Selections are `left-` or `right-` followed by `shoulder-pan`, `shoulder-lift`,
`elbow-flex`, `wrist-flex`, `wrist-roll` or `gripper`, plus `head-pan` and
`head-tilt`. These names select the **expected upstream wiring**; compare the
recorded physical response before treating that assignment as verified.
Wheel selections are excluded.
Ctrl+C or the workload's software stop interrupts the diagnostic and runs its
torque-off cleanup. Keep the physical power-cut option available if feedback or
cleanup fails.

Use an interactive terminal beside the robot, with clear space and immediate
access to cut motor power. Leave the arms and tags untouched during each test.
The current diagnostic requires the three-omniwheel XLeRobot's complete nine-
and eight-STS3215 inventories, firmware 3.10, and both selected onboard cameras.
Other robots/firmware need a separately reviewed adapter. The prepared container
must map `/dev/arm_right`, `/dev/arm_left`, `/dev/camera_head` and
`/dev/camera_hand`; serial enumeration is not used to assign sides.

## Fixed diagnostic guards

- Maximum goal change: 24 encoder counts, approximately 2.1 degrees.
- Maximum observed displacement: 32 counts, approximately 2.8 degrees including
  overshoot. The target ramps in two-count steps, then returns toward its start.
- Runtime drive-output cap: 40/1000 (4%). This is a PWM/output limit, not a
  calibrated contact-force limit; some joints will not move at this cap.
- Stop at absolute raw load 50, raw current 30, reported temperature 45°C,
  nonzero status, changed output cap, or supply outside 11–13 V.
- Abort on missing feedback, unexpected torque state or camera frame age above
  250 ms. Each powered test lasts at most 2.4 seconds in the normal loop.
- Require room for the measured envelope within the motor's existing stored
  software range. Never rewrite offsets, modes, limits or other EEPROM settings
  to make a test pass. Stored ranges are not measured mechanical stops.

STS3215 firmware 3.10 was observed to **auto-enable torque when a position goal
is written**. The diagnostic and task adapter seed measured goals with runtime
output set to zero. Cleanup also seeds its final measured goal at zero output,
then disables torque before restoring the previous cap, and makes torque-off the
final write. It never restores a stale zero-position goal. A cleanup/readback
failure reports that motor power must be cut; an unsupported arm can drop when
torque is disabled.

The adapter also checks the upstream SDK's read/write response lengths. This
rejects malformed or delayed replies with the wrong payload size rather than
interpreting them as temperature/current. It does not suppress high temperature
readings in correctly sized packets.

Receipts, samples and JPEGs are private runtime evidence under
`/state/.build/motor-identification/`. `completed` means the bounded diagnostic
finished; it does not mean the joint moved, its role was visually verified or
its mechanical range was calibrated. Compare `maximumObservedDeltaCounts`,
camera observations and any abort/rejected-reply details.

## xur-255 observations — October 8, 2026

Direct reads identified all 17 motors as model **777 / STS3215**, firmware
**3.10**. That common model register does not distinguish the installed 7.4 V
and 12 V hardware variants. Idle supply readings were about 12 V on both buses;
these readings do not establish the motor's voltage rating.

The pinned
[XLeRobot motor declarations](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/software/plugins/lerobot_robot_xlerobot/lerobot_robot_xlerobot/xlerobot.py)
provide this expected map:

| ID | Adapter ending 7688: left/head | Adapter ending 7920: right/wheels |
| --- | --- | --- |
| 1 | Shoulder pan | Shoulder pan |
| 2 | Shoulder lift | Shoulder lift |
| 3 | Elbow flex | Elbow flex |
| 4 | Wrist flex | Wrist flex |
| 5 | Wrist roll | Wrist roll |
| 6 | Gripper | Gripper |
| 7 | Head pan | Left wheel |
| 8 | Head tilt | Back wheel |
| 9 | — | Right wheel |

Powered checks established additional evidence:

- 7920 ID 1 moved the arm carrying tag 02 while the tray/tag 01 stayed still.
  Its encoder traversed eight counts in the initial 4% test. The hand-camera
  view shifted with that arm. This supports the right-arm bus assignment.
- 7688 ID 7 shifted the head view horizontally; ID 8 shifted the head view
  vertically while the hand view stayed steady. These support pan/tilt roles.
  The 2.1-degree tilt target produced 21 counts of observed movement.
- ID 6 on each bus responded to a 2.1-degree target: 22 observed counts on
  7920 and 20 on 7688. Tag 01 moved with the left gripper, so it is a moving
  finger reference rather than a fixed wrist reference.
- A 7920 ID 4 check produced encoder/camera movement, but an operator adjusted
  tag 02 during that test. Its images cannot independently verify the axis.
- Some other tests produced little or no motion at the small output cap.
  The right IDs 2/3 were not tested because they sit near stored software range
  endpoints. The left ID 5 has a suspicious ten-count stored range and was not
  tested. No wheel received a motion or torque-enable command.

An experimental 6% cap produced isolated high temperature reports on left IDs
1/2/3 (48–90°C), immediately followed by normal 31–34°C readings. Each test
aborted and verified torque off. Eighty idle reads of ID 1 all reported 34°C.
Checking reply lengths did not explain the anomaly: the guarded retest still
received a high reading in a correctly sized packet. Its cause remains
unresolved, and the current diagnostic retains the smaller 4% cap and first-
sample temperature cutoff. These conflicting readings do not establish the
motor's physical temperature during those tests.

Only distal arm assemblies were covered well in these head-camera views.
Tag 02 was later repositioned partly outside the frame, and the labels were
not consistently flat. Further rigid link references and movement coverage are
needed for automatic camera-based calibration; these small tests do not finish
that requirement. Raw evidence remains under ignored
`.build/evidence/robotics-motor-identification/`; see the screenshot register.
