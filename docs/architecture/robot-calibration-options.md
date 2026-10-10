# Calibration with fewer user steps

The robotics workload should support different reference methods inside the
container. Robot profiles still own only deployment, devices and the `/robot`
proxy. There is no working automatic joint-calibration solver yet; the current
button remains a read-only readiness assessment.

## What the cameras can currently see

Stationary captures from xur-255 on 2026-10-10 showed a clear, well-lit 1920×1080
head colour image of both arms and grippers, although some lower links extend
outside the frame. The separate 640×480 infrared interface also showed the
arms, but the printed tags had little contrast. IR is a grayscale image source,
not a depth measurement. These captures establish visibility in one pose only.
Private evidence is registered in [the screenshot register](../screenshots.md).

## Alternatives to markers on the arms

| Method | User setup | What it can establish | Remaining limitations |
| --- | --- | --- | --- |
| Match known robot CAD to colour images | Select the robot model and give the camera a clear view; an uncalibrated webcam still needs optical calibration | Candidate visible joint angles and camera-to-robot alignment | Hidden links, similar silhouettes, symmetric joints and incorrect CAD can leave multiple answers; accuracy on this robot is unverified |
| Match robot CAD to a calibrated depth camera | Connect a supported camera with usable metric depth and the correct model | Candidate joint geometry and alignment using 3D surfaces | Additional hardware, minimum working distance, occlusion and depth errors; it still needs installed-robot validation |
| Reusable calibration cradle | Place each torque-disabled arm into a known supported pose using a model-specific removable fixture | A physical reference for the joints constrained by the fixture, without arm labels | Requires human placement and does not independently establish full travel ranges; does not meet a completely unattended calibration goal |
| Joint index references | Install reference sensors and model-specific brackets | Independent joint reference events for a future homing procedure | More wiring/hardware; installation and powered approach must be validated |

The [RoboPose research](https://www.di.ens.fr/willow/research/robopose/)
demonstrates estimating joint angles and camera-to-robot pose from an image of
a known articulated robot. This is evidence that the approach is possible,
not evidence of SO-101 support or installed XLeRobot accuracy. Its published
models are for other arms, and a repository license must not be assumed to
license model weights. The first experiment should use geometric fitting with
no downloaded pretrained weights. Any later model must have its code and weight
licenses reviewed separately.

A single reusable camera-calibration board is another way to reduce printed
materials: it can establish webcam intrinsics without attaching anything to
the arms. It does not establish motor zero angles or mechanical endpoints.

## First experiment with existing hardware

Start with colour-image geometry fitting, entirely offline and without motor
access. Use the pinned upstream arm model, retaining mesh licenses. Fit the
visible model to saved images and compare its projected outlines with the
actual links. Preserve competing poses and report which joints cannot be
observed. Do not label a visually plausible overlay as a verified joint pose.

The pinned [XLeRobot model source](https://github.com/Vector-Wangel/XLeRobot/tree/b017b5e6354bd9f61f4247a920c72622ca0aade0/simulation/xlerobot)
contains separate link meshes and an arm URDF under the upstream Apache-2.0
license. Preserve that provenance rather than relabeling the meshes as MIT.
This CAD is a nominal model: its whole chassis has a wheel
configuration different from the installed three-omniwheel base, and compared
upstream jaw models disagree about their zero orientation. Validate link
geometry before relying on it. The head camera can be useful even though it is
mounted on moving head joints, provided the visible chassis geometry constrains
the camera-to-base transform. A cropped or occluded base may defeat that fit.

Only after independent pose validation should candidate robot-space angles be
compared with read-only raw encoder observations. A modular offset fitter can
serve camera estimates, a measured cradle, or future joint references. It must
handle encoder wrap, direction, inadequate variation and held-out failures;
it must not silently turn unknown mount or pose conventions into physical zero.

## Calibration is more than pose estimation

Motor encoder coordinates, joint physical angles, camera calibration and safe
operating ranges are separate quantities. [LeRobot's current SO-101 workflow](https://huggingface.co/docs/lerobot/en/so101)
asks the operator to position the
arm and move its joints through their ranges; replacing those steps requires
new validated references and range acceptance, not just a new vision detector.

A conservative collision-free operating envelope can be smaller than the full
mechanical travel, but it still needs independent verification. Neither CAD
limits nor stored motor min/max values prove installed clearance. The earlier
read-only assessment flagged a suspicious stored left wrist-roll range.

No experiment described here may approve a calibration receipt, enable torque,
start controller motion or issue motor commands. The app's existing calibration
and E-stop gates remain in force. Xbox pairing and input-only probing can be
done independently when the operator is ready.
