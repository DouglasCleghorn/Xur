# Read-only marker survey

The .NET console tool `marker_survey.cs` reports marker IDs, their projected
corner sizes, detection presence and shared camera references from saved frames.
It makes no camera, serial, motor or power calls. Logical camera names and frame
paths are operator-selected; there are no camera-brand or robot-model checks.
The native C adapter calls AprilTag's existing detector with zero allowed bit
corrections and full-resolution grayscale input. Its output is image geometry,
not metric poses or joint calibration.

Build the detector from the revision in `upstream-lock.json`. The example uses
the existing ignored checkout; first verify its revision is
`b7c0ebe9aa20f82ec7a828579004f9e706bfecd9`:

```sh
git -C .build/robotics/markers/apriltag rev-parse HEAD
cc -O2 -std=gnu99 -I .build/robotics/markers/apriltag \
  -o .build/robotics/markers/detect-markers \
  tools/Xur.Robotics/detect_markers.c \
  .build/robotics/markers/apriltag/apriltag.c \
  .build/robotics/markers/apriltag/apriltag_quad_thresh.c \
  .build/robotics/markers/apriltag/apriltag_pose.c \
  .build/robotics/markers/apriltag/tagStandard41h12.c \
  .build/robotics/markers/apriltag/common/*.c -lm -lpthread
```

Keep the upstream BSD-2-Clause notice with any distributed detector. Original
Xur wrappers are MIT licensed. The tools container builds this same adapter
from the pinned source and retains `/opt/licenses/AprilTag-LICENSE.md`.

The live workload offers **Check markers**, or
`POST /api/robotics/tasks` with `{"kind":"inspect-markers"}`. This task uses only
the selected head/hand cameras, requests 1920×1080 MJPEG when supported, drains
initial exposure frames, and detects three observations per camera with no
allowed bit corrections. Actual image dimensions and capture times are saved;
camera roles come from setup rather than vendor names. It does not open a motor
bus or controller and does not require motion enablement or calibration.

Read the completed report at `/api/robotics/jobs/{id}/markers` and JPEG evidence
through `/api/robotics/jobs/{id}/captures`. The .NET runtime validates detector
identity, IDs, timestamps, pixel geometry and image limits before persisting the
report under `.build/captures/{id}/`. Duplicate copies of an ID in one frame
are ambiguous. A shared camera reference requires unambiguous observations in
all three frames of both cameras; the sequential camera captures are not
synchronized stereo pairs. Reports explicitly leave metric pose and joint
calibration unapproved. The task shares the workload's exclusive operation gate
with manipulation and is available to the existing Robotics API-key scope.

The saved-frame console below remains available for offline analysis without
the workload or robot container.

The live integration was checked on xur-255 on October 8, 2026: the prepared
tools image captured both cameras at 1920×1080 and found 00/01/02 in every head
frame and 01 in every hand frame. This diagnostic container was given only the
two cameras, with no serial/controller devices and no network. The .NET task
API validated that real adapter response, persisted the report, and served both
JPEGs in a local integration host. Evidence is under the private ignored
`.build/evidence/robotics-marker-survey/live-api/` family. The robot's installed
Xur manager has not been replaced with this working-tree application build.

Supply one grayscale 8-bit P5 PGM image or a concatenation of complete P5 frames
for each named camera. FFmpeg's `image2pipe` with `-vcodec pgm` produces that
stream directly. Keep capture commands separate from analysis so surveys can
run offline against saved evidence. Inputs are limited to 128 MiB and 100 frames
per camera; detector processes have a 15-second timeout per frame.

```sh
dotnet run --file tools/Xur.Robotics/marker_survey.cs \
  --artifacts-path .build/robotics/marker-survey -- \
  .build/robotics/markers/detect-markers \
  .build/evidence/robotics-marker-survey/analysis \
  head=.build/evidence/robotics-marker-survey/head-repeat.pgmstream \
  hand=.build/evidence/robotics-marker-survey/hand-repeat.pgmstream
```

The ignored output directory contains extracted PGM frames and `survey.json`.
The report includes the detector executable's SHA-256, zero-error detections,
duplicate-ID ambiguities, minimum/maximum projected reference edges, center
spread and IDs observed by more than one camera. Duplicate copies in a frame
are excluded from the unambiguous camera summary and shared-reference list.
Frame sets are not asserted to be synchronized stereo pairs. Decision margin
is upstream's decoding statistic; it is not pose accuracy or force confidence.

On October 8, 2026, two stationary five-frame captures from xur-255 found tags
00, 01 and 02 in every head frame, and 01 in every hand frame, all with zero
corrected bits. Head-reference edges were approximately 40–44, 52–58 and 78–87
pixels respectively; tag 01 in the hand view was approximately 29–57 pixels.
Center spread was under 0.2 pixels in each camera during those captures. These
observations establish readability in those poses only. They do not establish
visibility through a movement range or validated camera-to-link transforms.

Visual inspection places 00 on the tray, 01 near the image-left wrist/end
bracket and 02 near the image-right wrist/forearm, beside the hand-camera
assembly. Those are candidate link associations, not approved calibration
bindings. The paper at 02 visibly bows; readability does not establish a flat,
rigid marker plane suitable for metric pose estimation. Actual tag dimensions,
camera intrinsics, mounting transforms, joint offsets and travel limits remain
unverified. End markers can support further kinematic observations; a single
stationary pose does not identify those unknowns.
