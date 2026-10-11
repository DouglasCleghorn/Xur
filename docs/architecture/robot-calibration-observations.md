# Marker-independent calibration observations

The robotics container can preserve a small, read-only observation of its
selected cameras and motor buses. This is the acquisition phase of the
[visual pose-learning plan](robot-visual-pose-model.md), not automatic joint
calibration. The robot stays disarmed: capturing does not command positions,
enable torque, change EEPROM limits or create calibration approval.

Markers and CAD are optional evidence. Images paired with raw encoders can
support segmentation, tracking and joint correspondence, but labels from an
unverified encoder cannot independently establish that encoder's physical zero.
The [offline offset fitter](robot-encoder-offset-fit.md) still requires
independently anchored physical-angle observations.

## Container API and pages

| Route | Purpose |
| --- | --- |
| `POST /robot/api/calibration-observations` | Start a bounded capture job with JSON `{"samples": 1}`; one to three samples are allowed |
| `GET /robot/api/calibration-observations` | Read the latest compact capture status, or `null` when none is saved |
| `GET /robot/api/calibration-observations/{id}/manifest` | Download the capture's file sizes and SHA-256 checksums |
| `GET /robot/api/calibration-observations/{id}/files/{name}` | Read a generated evidence file from that capture |

The setup page offers a sample-count selector, capture progress, PNG previews,
manifest download and copyable JSON. IDs are 32 lowercase hexadecimal
characters. Browser preview paths accept only generated basenames, reject
traversal and preview only PNG files. All assets are local, and report strings
are rendered as text. Existing Xur CSRF protection applies to POST requests.

The dashboard's Auto calibrate action checks hardware and captures one
read-only sample. Its assessment reports acquisition, camera mount, camera
coverage, segmentation, joint correspondence and physical references
separately. Capturing an image does not prove that the camera mount is known,
all joints are visible or sufficient varied data exist. Segmentation remains
unimplemented. The assessment stays blocked and leaves Xbox motion subject to
the existing approved-calibration and motor-limit gates.

## Evidence and timing

Each sample retains motor reads before and after acquiring the head and hand
camera frames. Camera captures are sequential, not synchronized stereo. Retain
the individual capture and motor-read timestamps; neither a shared sample
number nor the job completion time proves image/state alignment.

Generated files include session metadata, per-sample JSON, decoded camera arrays
in NPY format, PNG previews, and the before/after motor observations. PNGs are
viewing artifacts; keep the decoded arrays and their metadata for later data
quality checks. A few stationary samples are not a training corpus or evidence
of joint correspondence. Future varied observations require independently
validated timing, coverage and joint-channel conventions.

Stationarity requires torque disabled, measured velocity zero and no more than
two raw encoder counts of drift across the read brackets. Position-mode motors
also require their `Moving` flag to be clear. A continuous-rotation motor in
velocity mode may retain that flag while disabled; its measured velocity and
encoder brackets still have to pass. Missing register values and unsupported
operating modes are rejected, and all original register values are retained.
When torque is verified off but stationarity is unresolved, camera images and
both motor brackets are still retained as incomplete evidence. Unreadable motor
positions, unreadable buses or unverified torque stop collection before images.

The compact status records completeness, manifest checksum, problems, backup
state and the explicit flags `motorCommandsIssued: false`,
`jointCalibrationApproved: false` and `synchronizedStereoFrames: false`.
An incomplete capture can still contain useful retained files. Do not present
it as a completed calibration or silently replace missing data with defaults.

## Preservation and backup scope

Original capture files and checksum manifests are preserved outside the recent
job list, so removing an old job from that list does not remove its evidence.
The UI can continue retrieving a capture by its stable ID. A failed new capture
does not replace the previous report displayed in the browser with a claimed
success.

`backupVerified` refers to checksum verification of an independent local copy.
This is protection against accidental edits to the original dataset, not a
verified off-device backup or protection against loss of the robot's disk.
Remote recording backups use the separate
[backup receiver](../../containers/robot-backup/README.md) and must be checked
end to end for the configured network route and actual samples.

Camera images can contain people or private room details. Runtime captures stay
in ignored evidence/state storage; no images are included in source archives.
Any screenshot used or published must follow the repository's screenshot
register and privacy-review rules.
