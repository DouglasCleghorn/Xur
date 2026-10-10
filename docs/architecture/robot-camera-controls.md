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
head angle or safe mechanical endpoints. No head motor was moved. A different
robot must supply or establish its own camera-mount joint binding; bus IDs 7/8
are not a universal camera-control interface.

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
controls does not approve motorized re-aiming or automatic calibration.

## App implementation scope

The read-only capability panel is being prepared in the application container;
it is not present in the deployed `bd2f18c` image. It should preserve each
device's failures, unsupported payloads and raw driver output for copying, and
report selected roles from saved configuration. Queries use fixed local tool
arguments and inventory-approved device aliases. No control-setting endpoint
or public motor-target API is part of this phase.
