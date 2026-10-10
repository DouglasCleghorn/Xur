# Camera discovery and arm coverage

Camera discovery is the first phase of the
[visual pose-learning plan](robot-visual-pose-model.md). The container app should
report what the selected devices actually expose, without assuming a webcam
brand, robot model or identical interfaces on other installations. Image
controls and a motorized camera mount are separate capabilities.

## Read-only observations on xur-255

Queries on 2026-10-10 at approximately 16:54 UTC used Linux `v4l2-ctl` to list
device properties, controls and capture formats. They did not change image
controls or send motor commands. The appliance was running Xur 26.10.032 and
the robotics image from commit `bd2f18c`.

| Stream | Installed role | Driver-reported controls and modes |
| --- | --- | --- |
| `/dev/video3`, Lenovo 500 colour stream | Head RGB | Exposure mode/time, dynamic frame rate, brightness, contrast, saturation, hue, automatic/manual white balance, gamma, sharpness, backlight compensation, mains-frequency selection and region-of-interest payload controls; MJPG includes 1920×1080 and 640×480 at 30 fps |
| `/dev/video5`, separate Lenovo grayscale stream | Unselected IR | GREY 640×480 at 30 fps and region-of-interest payload controls; not a depth stream |
| `/dev/video1`, EMEET C950 | Hand | Exposure mode/time, dynamic frame rate, brightness, contrast, saturation, hue, automatic/manual white balance, gamma, gain, sharpness, backlight compensation and mains-frequency selection; available formats are retained in the private report |

None of these capture nodes advertised USB pan, tilt, zoom or focus controls.
An absent control is not a writable setting with a guessed default. The head
driver's region-of-interest rectangle is retained as an opaque payload; its
presence alone does not establish a supported crop or field-of-view adjustment.
Manual exposure and white-balance temperature were flagged inactive while their
automatic modes were enabled.

The head colour stream's uncompressed YUYV 1920×1080 mode reports only 5 fps;
MJPG reports 30 fps at that size. Camera format, resolution and frame rate must
be negotiated and checked together rather than inferred from the camera name.
Stable `/dev/v4l/by-path/` aliases identify its separate interfaces; transient
`/dev/videoN` numbers are only the observed node mapping.

## Separate head motors

A fresh read-only app observation at 16:56 UTC found STS3215 model-register 777
motors on the selected left/head bus, whose adapter serial ends `7688`:

| Upstream role | Bus ID | Reported raw position | Stored min/max | Torque/current/load |
| --- | --- | --- | --- | --- |
| `head_pan` | 7 | 2083 | 745 / 3455 | All zero |
| `head_tilt` | 8 | 2814 | 818 / 2975 | All zero |

The role names come from the pinned upstream motor map. This observation does
not experimentally establish positive/negative camera movement, an absolute
head angle or safe mechanical endpoints. That read-only observation did not
move a head motor. A different
robot must supply or establish its own camera-mount joint binding; bus IDs 7/8
are not a universal camera-control interface.

Earlier attended identification evidence from 2026-10-09 does support a local
image-direction association. The pan test changed reported encoder position
2078→2082 while tray tag 00 moved left by about 5 pixels. The tilt test briefly
reached 2830 from 2809 while that fixed tag moved upward by about 50 pixels;
afterward it settled at 2814. These short observations identify the installed
axes' local effect, not a calibrated angle, full travel range or a guaranteed
view-improvement trajectory. Their private capture family is already registered.

## Attended motor/image comparison on 2026-10-10

An operator confirmed they were beside the robot with motor power within reach.
The installed private diagnostic used the pinned LeRobot motor interface to test
individual eligible positional joints; wheel commands were excluded. Its goals
were limited to 24 raw counts (about 2.1 degrees), with a 32-count observed
envelope, speed/output caps, continuous camera freshness and measured
load/current/temperature/supply checks. Stored limits were additional guards,
not approved mechanical calibration. No EEPROM limits or offsets were changed.

Both head axes moved by up to 22 measured counts. Classical OpenCV registration
of the before/displaced images gave the following local evidence at 1920×1080:

| Joint | Head phase-correlation translation | Distributed feature evidence | Hand view |
| --- | --- | --- | --- |
| Left/head bus ID 7, upstream `head_pan` | About 26 pixels left, 1 pixel down | 90% homography inliers over 42% of the image and 9/12 grid cells | About 0.14-pixel projected movement |
| Left/head bus ID 8, upstream `head_tilt` | About 52 pixels up, 1 pixel right | 97% homography inliers over 70% of the image and 11/12 grid cells | About 0.06-pixel projected movement |

The image shift supports the installed pan/tilt bindings and positive encoder
direction in the tested neighborhood. Rotation is not a pure image translation;
phase correlation and projected feature motion therefore need not have equal
magnitudes. These values are relative image observations, not degrees per pixel
or a universal motor-to-camera map. The scoring tool uses no tags or model
weights. A scene moving across the whole view can produce similar scores.

Both completed head tests verified torque disabled. The low-output return
attempts did not reach their exact starting positions: pan ended at 2088 after
starting at 2083; tilt ended at 2834 after starting at 2814. Their return images
must not be described as a repeated starting-pose control. The diagnostic's
`ready` and `before` frames were also identical copies. A separate stationary
sequence was therefore captured with distinct timestamps/content and no motor
writes. Five pairs per camera were all classified static-or-jitter; the largest
median projected movement was 0.057 pixels for the head and 0.120 for the hand.
That short baseline supports this comparison but does not validate thresholds
for every lighting or capture condition.

Arm tests retain measured encoder displacement alongside image scores. A joint
that does not move under the output cap is inconclusive; a joint blocked by the
preflight range guard is untested. A hand camera's entire view may move with
multiple upstream joints, so these tests can identify contributing joints
without uniquely identifying its physical mounting link. Private captures and
operator-attended test provenance are recorded in the screenshot register.

All 14 positional selections were considered: 11 completed bounded tests, and
left wrist roll, right shoulder lift and right elbow were blocked before any
writes by the existing range guard. One earlier tilt attempt was also blocked
by the shared adapter lease and was retried after ownership cleared. Four arm
tests had no measured displacement and left wrist flex changed by one count,
so those five tests provide no useful exclusion evidence. Both grippers moved
19–20 counts while broad image features stayed steady. Right shoulder pan and
wrist flex changed the hand view by about 37–38 projected pixels, but the
classifier retained ambiguity because of phase/feature disagreement or limited
spatial coverage. No physical hand-camera mount was automatically assigned.

A fresh final dashboard observation around 17:24 UTC read all 17 motors with
torque off, no motor-command writes and a maximum reported temperature of 36°C.
The low-output tests did not force motion against resistance or widen limits.

## Capture-mode comparison

Stationary head RGB frames were captured at 640×480 and 1920×1080 around 16:55
UTC, allowing exposure to settle. Capture mode was negotiated for each frame;
no image control or motor setting was written. Both views still omit the arm
bases/proximal references and crop portions of the arms. The 4:3 mode did not
solve coverage; changing resolution is not sufficient evidence of a wider view.
The 1920×1080 capture retains more visible detail for segmentation experiments.
Private image locations and privacy review are in
[the screenshot register](../screenshots.md).

The next view-improvement step needs to distinguish a head-angle change from a
mounting/field-of-view limitation. Image controls can improve exposure and
contrast, but cannot reveal geometry outside the frame. Discovering these
controls and the bounded tests above do not establish a calibrated operating
range or automatic calibration.

The reusable [saved-frame comparison tool](robot-camera-motion-evidence.md)
ships with the container's existing OpenCV environment. Its JSON retains raw
measurements and ambiguity for copying; the scorer itself has no hardware
access or motor-command interface.

## App implementation scope

The read-only capability panel is implemented in the application container;
it is not present in the older deployed `bd2f18c` image. It preserves each
device's failures, unsupported payloads and raw driver output for copying, and
report selected roles from saved configuration. Queries use fixed local tool
arguments and inventory-approved device aliases. No control-setting endpoint
or public motor-target API is part of this phase.

Container startup and explicit preparation rebuild the selected serial/camera
aliases under the adapter's exclusive ownership lock. All required targets must
exist before aliases change, and a failed preparation cannot leave a ready
manifest. Hardware operations also verify alias targets rather than trusting
the persistent manifest after container recreation. Startup first requests a
bounded software stop of any previous adapter owner. Alias preparation changes
filesystem aliases only; it does not enable torque, write motor registers or
approve calibration. A disconnected selected controller remains saved while
its alias is absent; controller motion and recording fail closed until it is
connected and prepared. Local dataset training remains independent of connected
hardware.
