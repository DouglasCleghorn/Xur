# Saved-frame camera motion evidence

`tools/Xur.Robotics/image_motion.py` compares saved frames using classical image
processing. It has no robot, serial, camera capture, network or model-weight
interfaces. It does not receive motor IDs or produce movement instructions.
The original implementation is MIT licensed under Xur's root `LICENSE`.

Run it with the robot container's existing Python/OpenCV environment, or an
isolated environment under ignored `.build/`. Local acceptance used Python
3.12.15, `opencv-python-headless==4.13.0.92` and `numpy==2.2.6`.
[OpenCV is Apache-2.0](https://github.com/opencv/opencv/blob/4.13.0/LICENSE);
[NumPy is BSD-3-Clause](https://github.com/numpy/numpy/blob/v2.2.6/LICENSE.txt).
Their own notices remain applicable.

```sh
python tools/Xur.Robotics/image_motion.py \
  --before .build/evidence/experiment/head-before.jpg \
  --displaced .build/evidence/experiment/head-displaced.jpg \
  --after .build/evidence/experiment/head-after.jpg \
  --output .build/evidence/experiment/head-motion.json
```

Images must be local files with identical decoded dimensions. `--after` is
optional; `--max-dimension` bounds analysis resolution while all reported motion
and residual distances remain in original decoded-image pixels. JSON includes
input hashes, decoded dimensions, software/source versions, raw phase shift and
response, mutual ORB matches, RANSAC inliers and reprojection residuals, spatial
coverage, a projected motion grid, illumination fit and residual image changes.
No images or generated evidence belong in source archives.

Phase correlation supplies a translation screening measurement. ORB/Hamming
matches and RANSAC supply a planar image homography; inliers must cover both
views rather than just a small object. An affine intensity correction reduces
false motion from simple gain/offset exposure changes. Unknown, dark, clipped,
weakly textured or conflicting evidence stays ambiguous. The phase response and
inlier ratio are measurements, not probabilities of a motor identity. The
defaults are screening thresholds and are explicitly unvalidated for a physical
installation.

Input file hashes expose identical content, and the CLI rejects JSON output that
would overwrite an input image. A ready/before pair with identical content is a
duplicate-frame check, not an independent jitter or exposure baseline. Image
files alone do not verify capture timestamps; a separate capture manifest must
establish temporal independence.

Pair classifications are `global-motion-candidate`, `local-motion-candidate`,
`static-or-jitter` and `ambiguous`. Local changes require concentration in a few
image tiles while the broad background transform remains within jitter. Shadows
and reflections can still resemble a moving local object. Three-frame reports
compare both the return motion and before/after closure; a partial return can
correctly produce `ambiguous-repeat-control`. Stable views produce
`stable-through-sequence`.

For attended motor identification, the operator's separate bounded upstream
operation must record which one motor was exercised and its actual encoder
readings. Compare both cameras' before/displaced/after frames and the
ready/before nuisance pair. A reproducible broad shift in one camera alongside
a stable other camera supports a camera-mount hypothesis. Validate against
repeated opposite perturbations, settled exposure, scene motion and measured
baseline jitter before accepting that hypothesis. Neither the script nor its
tests establish physical clearance, drive calibration or authority to move.

A stationary camera viewing a moving screen or scene can produce the same
global image transformation. This observability limit survives an exact return
control. Parallax, rolling shutter and occlusion can also invalidate a single
homography. Image registration needs no guessed intrinsics, but does not turn
pixel shifts into pan/tilt angles or a metric camera pose. Every result retains
`cameraMotorIdentified: false` and `motorCommandsIssued: false`.

Run hardware-free acceptance in the same OpenCV environment:

```sh
python tests/Xur.Integration.Tests/robot-image-motion.py
```

The deterministic tests cover translated pixels at reduced analysis resolution,
exposure-only changes, subpixel jitter, low texture, spatially confined features,
local object changes, distributed noise, clipping, unrelated scenes, invalid
projective transforms, return drift and the moving-scene counterexample. They
write receipts only under ignored `.build/evidence/image-motion/`.

Primary API references: [OpenCV ORB feature matching](https://docs.opencv.org/4.13.0/dc/dc3/tutorial_py_matcher.html),
[homography and camera geometry](https://docs.opencv.org/4.13.0/d9/d0c/group__calib3d.html),
and [phase-correlation implementation](https://github.com/opencv/opencv/blob/4.13.0/modules/imgproc/src/phasecorr.cpp).
