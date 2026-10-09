# Read-only camera and tag metrology

The robotics container can add metric AprilTag pose candidates to its stationary
marker surveys when measured camera parameters and tag reference sizes are
supplied. It never infers these values from a camera model, advertised field of
view, requested capture resolution or paper width. Without settings, the
existing pixel-only survey and overlay remain available.

This is a foundation for camera coverage assessment, not joint calibration.
Neither supplying measurements nor obtaining a low reprojection error creates a
calibration receipt, arms the robot, identifies a link or establishes travel
limits. Powered automatic calibration still needs the solver and acceptance
gates in the [marker calibration design](robot-marker-calibration.md).

## Supply measured values

Use **Camera and tag measurements** on `/robot/setup`, then **Check markers** on
`/robot/tags`. The blank template contains no guessed values and is not saved
until all required fields have been replaced. The app owns
`GET/POST /robot/api/metrology` and `/state/metrology.json`; main Xur only proxies
the request. Changing these settings requires an idle, disarmed workload.

The version-1 JSON document has:

| Field | Meaning |
| --- | --- |
| `cameras[].name` | Selected logical role, `head` or `hand`. |
| `cameras[].device` | Exact selected `/dev/v4l/by-id/…-video-index0` identity. |
| `cameras[].width`, `height` | Actual decoded frame resolution used when measuring intrinsics. |
| `cameras[].fx`, `fy`, `cx`, `cy` | Measured pinhole intrinsics in pixels at that exact resolution. |
| `cameras[].distortionModel` | Explicit `none` with an empty coefficient array, or `brown-conrady-5`. |
| `cameras[].distortion` | Exactly five coefficients `[k1,k2,p1,p2,k3]` for `brown-conrady-5`. |
| `cameras[].measurementSource` | Required informational provenance, such as calibration report/date and focus settings. |
| `tags[].id` | Unique `tagStandard41h12` ID. |
| `tags[].referenceEdgeMeters` | Measured flat square edge between the detector's corner references, in meters. |
| `tags[].measurementSource` | Required informational source of the actual printed measurement. |
| `maximumReprojectionRmsPixels` | Candidate acceptance threshold, default 2 pixels. |
| `minimumCandidateSeparationPixels` | Minimum RMS separation to select between two otherwise valid candidates, default 0.5 pixels. |

Quality thresholds are image-fit filters, not torque, motion or uncertainty
limits. The API rejects unknown fields, unsupported lens models, nonfinite
values, invalid dimensions, duplicate roles/IDs and absent provenance. It
supports at most two cameras and 128 tag measurements; requests share the app's
16 KiB body limit. Fisheye, rational, thin-prism and tilted-sensor models require
an explicitly reviewed adapter and are currently unsupported.

Intrinsics must describe the same camera, resolution and unchanged focus/zoom.
The task uses decoded dimensions, not the requested video mode: a camera that
returns 640×480 after a 1920×1080 request needs 640×480 measurements. A selected
device or resolution mismatch produces `unavailable`; the app never rescales
intrinsics. Clear the camera list to restore pixel-only scans. Saved reports
retain the supplied measurement snapshot and its hash, so later changes do not
discard the parameters and provenance used for those observations.

Measure the reference square after printing. The complete paper/sticker width,
white margin and number caption are not the estimator's reference edge.
Keep the pattern flat on a rigid mount; a readable bowed sticker does not
establish correct metric geometry. AprilTag documents the required
[reference size and intrinsics](https://github.com/AprilRobotics/apriltag/tree/b7c0ebe9aa20f82ec7a828579004f9e706bfecd9#pose-estimation).
The app validates the supplied numbers and provenance format; it cannot verify
that the measurements were performed correctly.

## Estimation and reported errors

.NET inverts the supplied Brown-Conrady distortion at each detected corner,
requiring finite values, convergence within 30 iterations and a positive local
Jacobian before accepting even a zero-residual root. The native helper receives
only undistorted pixel coordinates, intrinsics and the measured edge. It calls
the pinned AprilTag `estimate_tag_pose_orthogonal_iteration` implementation for
50 iterations and retains both returned local minima.

These local inversion checks do not prove global bijectivity of the supplied
lens model over the image domain. Camera calibration must independently validate
that property before the results can support a future joint solver.
[OpenCV's camera-model documentation](https://docs.opencv.org/4.13.0/d9/d0c/group__calib3d.html#details)
describes the Brown-Conrady coefficients and warns that calibration optimization
does not enforce monotonic/bijective distortion.

The report defines `p_camera = R * p_tag + t`: `rotation` is a row-major 3×3
matrix; `translationMeters` is in meters; optical camera axes are x right,
y down, z forward. Tag corners use AprilTag's native order
`[-s,+s,0], [+s,+s,0], [+s,-s,0], [-s,-s,0]`, where `s` is half the reference
edge. It reports tag-to-camera poses only. No camera-to-base, marker-to-link or
head-joint transform is inferred, and sequential head/hand frames are not
synchronized stereo.

.NET rejects malformed/nonfinite poses, improper rotations, any nonpositive
corner depth and excessive reprojection error. `reprojectionRmsPixels` is the
RMS **2D corner distance** in the original distorted image:
`sqrt(sum((predictedU-observedU)^2 + (predictedV-observedV)^2) / 4)`.
The native `objectSpaceError` is AprilTag's sum of squared object-space
residuals (square meters for meter inputs), not pixel error or a distance RMS.
See the [pinned upstream objective](https://github.com/AprilRobotics/apriltag/blob/b7c0ebe9aa20f82ec7a828579004f9e706bfecd9/apriltag_pose.c#L117).

Both candidates remain in copyable/downloadable raw JSON. A tag is `estimated`
only when a valid candidate can be selected; close valid fits are `ambiguous`
with no `preferredCandidate`. Missing tag measurements, duplicate IDs,
reprojection/depth failures and helper errors are explicit. An optional helper
timeout preserves the pixel report; real cancellation still cancels the job.
`metricPoseAvailable` means at least one valid candidate exists, including
ambiguous candidates. Read the per-tag state before treating a pose as resolved.
`jointCalibrationApproved` and `motorCommandsIssued` remain false throughout.

## Offline proof

`MetrologyTests.cs` checks .NET validation, distortion inversion, resolution/
device matching, planar ambiguity, positive depth, rejection paths, timeout
handling and absence of arming or calibration receipts.
`robot-metrology-native.py` exercises the exact C helper against known synthetic
oblique and weak-perspective geometry, retaining competing fits.
`robot-metrology-web.py` sends distorted synthetic corners through the actual
Native AOT API, .NET inversion, pinned native estimator and raw-pixel
reprojection, then checks persistence and pixel-only fallback. Neither suite
opens physical devices or submits motor commands.

Generated test receipts remain under ignored `.build/evidence/metrology/`.
Synthetic numerical accuracy is a regression check, not evidence of camera
calibration or xur-255 accuracy. Valid measured inputs and suitable camera/tag
coverage have not yet been established for that robot.
