# Visual pose learning across robotics projects

The target is a shared robot segmentation and visual-feature model that adapts
to each installation from synchronized camera images and measured joint states.
Robot CAD and arm markers are optional. A new robot may need local adaptation;
support for an unfamiliar morphology without any examples is a separate,
unproven target. This design is not an implemented or trained pose estimator.

## Delivery phases

| Phase | Goal | Evidence needed to advance |
| --- | --- | --- |
| 1. Camera discovery and coverage | Identify capture streams, image controls and camera-mount joints; get a usable view of the arms | Capability reports plus bounded individual-joint tests, measured encoder changes and classical before/displaced/after image registration; distinguish whole-scene motion from local arm motion and report untested or inconclusive joints |
| 2. Segmentation and tracking | Extract arm instances and useful link features | Independently checked masks/tracks on varied images, with ambiguous selections and occlusions reported |
| 3. Joint correspondence | Associate tracked visual changes with measured joint channels | Synchronized varied recordings with enough independent joint variation to reject confounded associations |
| 4. Local adaptation | Fit a small per-installation visual pose adapter automatically | Held-out image-only accuracy, coverage and model-version rollback; uncertain or worse candidates are rejected |
| 5. Physical calibration and use | Independently establish references, accepted operating limits and control readiness | Validated physical references and motion acceptance; predictions trained on the same unverified encoder labels are insufficient |

Phase 1 is the immediate milestone. Segmentation and adaptation can be tested
offline using existing licensed recordings while physical calibration remains
unfinished. The Xbox input-only path can be checked independently of all phases.
See the [camera discovery evidence](robot-camera-controls.md).

### Phase 1: discover camera movement without a learned model

Read driver capabilities first, then use the upstream robot's private, attended
bounded-motion diagnostic to perturb one eligible positional joint at a time.
Keep drive wheels disabled. Capture both cameras before, during displacement
and after the return attempt, with measured encoder/effort feedback. Reject
joints that fail the existing range, temperature, supply or effort guards;
do not rewrite limits to force discovery. A command with no measured movement
is inconclusive, not evidence that the joint cannot move a camera.

Use OpenCV phase correlation for a translation score and feature matching with
RANSAC homography for distributed image movement. A rotating camera need not
produce a pure translation, so retain inlier coverage, projected image motion
and photometric residuals alongside the shift. Independent stationary frames
measure jitter/exposure variation. The return image must be checked against
the actual return encoder value, rather than assuming the starting pose was
reached. Existing duplicate pre-test frames are not independent controls.

Whole-image motion is a camera-mount candidate when paired with an isolated
joint change in a stationary room. A moving scene can produce the same image
score. Several ancestors of a hand camera can also move its entire view; this
identifies a contributing chain, not necessarily the joint physically holding
the camera. Store uncertain associations for review before using them to
improve coverage. This phase establishes local direction, not physical zero,
safe endpoints or automatic motor calibration.

## Segmentation followed by local adaptation

1. Extract robot regions and track useful features over recorded video. Keep
   separate arm instances where possible, and retain RGB appearance inside the
   masks. A whole-arm silhouette alone loses wrist rotation and other details;
   link masks or tracked features provide additional evidence.
2. Pair images with measured encoder states, their timestamps and coordinate
   conventions. Learn which visual changes correspond to which joint channels.
   Joint-state supervision does not supply segmentation labels: proposed masks
   need independent checks before they become training targets.
3. Adapt a small installation-specific pose head or adapter before considering
   full segmentation-model fine-tuning. Train on varied poses and views, then
   validate on held-out sessions. Automatically reject inadequate coverage or
   worse performance instead of replacing the installed model.
4. At inference, estimate joint states from images with per-joint visibility,
   ambiguity and evaluated error information. Encoder fusion is a separate
   output; an image-only validation path must not receive the target encoders.

A proposed automatic initialization can rank region proposals using robot
appearance and feature motion associated with observed encoder changes. This
has not been demonstrated, and clutter, moving cameras and correlated joint
motions can confound it. If selection is ambiguous, the setup should show the
candidate masks and allow a click or box to identify each arm. Automatic mask
generation does not itself identify which object is a robot or name its links.
The goal is fewer setup steps, with uncertainty visible to the user.

## Reuse across morphologies

The input contract describes joint channels, units, encoder conventions and
arm groups without assuming SO-101 names, six joints or two arms. A supplied
joint/link graph can improve constraints, but meshes and fixed link dimensions
are not required for the initial image-to-encoder estimator. A variable set of
joint channels and a per-installation adapter avoids assigning one fixed output
layout to all robots. This is an architecture proposal that needs evaluation.

Pretraining should include licensed recordings from several robot families,
camera placements and physical units. SO-100/SO-101 footage provides useful
coverage for the first family; more frames from that family do not prove
transfer to different mechanisms. Measure performance on a robot family held
out of shared-model training, both before and after local adaptation. Also hold
out installation sessions and camera views, and report unsupported joints.

## What each output means

| Output | Required evidence | What it does not establish |
| --- | --- | --- |
| Robot masks and tracked image features | Checked masks and tracking on independent frames | Joint identity, metric geometry or motor calibration |
| Visual joint state in a recorded encoder convention | Correctly timed measured states, convention metadata and held-out image-only accuracy | Physical zero or a new independent reference for those same encoders |
| Relative visual motion associated with a joint | Diverse observations that separate joint effects from camera and other-joint motion | Absolute angle, metric scale or hidden-joint observability |
| Physical joint angle or metric tool pose | Independent reference, a stated coordinate convention and validated metric geometry | Mechanical stops or a collision-free operating envelope |

An uncalibrated encoder can still label relative motion and its current raw
coordinate. Fitting images to those labels cannot independently certify the
encoder zero used to create them. A physical-angle claim needs an additional
reference, such as independently validated visible geometry, a known reference
pose or a sensor. Monocular masks alone do not establish metric scale. Hidden
joints and visually symmetric rotations may remain ambiguous even with more
training data; temporal or additional camera evidence can help but is not a
guarantee. These are output limits, not reasons to require CAD for segmentation.

The existing [offline encoder offset fitter](robot-encoder-offset-fit.md) accepts
only independently anchored physical-angle observations. Predictions learned
from the same installation's unverified encoders are not eligible references.

## Recording and data quality

Preserve original video, measured states, commands, camera capture times,
encoder sample times, clock mapping and the exact calibration context. Commands
are not achieved positions. The current head/hand snapshots are not synchronized
stereo; record each stream's timing and validate image/state alignment before
using it for pose supervision. Do not hide pairing errors by widening a loader's
timestamp tolerance.

Keep source licenses, physical-robot/session identifiers, camera mode, joint
order/units, normalization, stored offsets and gripper conventions. Public
LeRobot features require inspection rather than assuming these details exist.
For example, the [public SO-101 aggregate](https://huggingface.co/datasets/dongyoonkim/so101-pi05-base-dataset)
retains per-unit calibration offsets and documents several sources with
incorrect image/state timing. Exclude or independently repair those sources.
Deduplicate before splitting; neighboring frames or reuploaded sessions must
not appear on both sides of a validation split.

Back up originals and immutable manifests before adaptation. Keep generated
masks, corrections, training splits and checkpoints as versioned derivatives;
never overwrite the samples that produced them. Existing local backup support
and the [remote receiver](../../containers/robot-backup/README.md) are foundations,
but xur-255-to-EPYC delivery still needs a verified network route. A deployed
receiver alone is not a verified remote backup of a recording.

## Open components and deployment boundary

[SAM 2/2.1](https://github.com/facebookresearch/sam2#license) is the first
segmentation candidate: its publisher explicitly licenses the checkpoints and
training code under Apache-2.0 and provides image/video prediction and custom
fine-tuning. It is a mask generator and tracker, not a robot joint estimator.
Start with an unchanged segmentation teacher and a small pose adapter; separately
evaluate segmentation fine-tuning against independent mask labels. Do not assume
the upstream full-training configuration fits a 24 GB GPU.

If additional visual features improve measured results, standard
[DINOv2 backbones](https://github.com/facebookresearch/dinov2#license) have
explicit Apache-2.0 code and weight licensing. Cell-DINO and XRay-DINO have
separate restrictions and are excluded. Pin every selected code revision and
checkpoint checksum, preserve notices, and review data licenses separately.
No checkpoint or training dataset has been downloaded for this design.

The ASP.NET Core Native AOT robotics app owns dataset selection, masks/corrections,
adaptation jobs, model versions and results. A private Python/PyTorch worker is
appropriate for these ML libraries and can run on local GPUs or a configured
GPU system such as xur-epyc. Initial adaptation updates only the local adapter;
user recordings are not automatically pooled into a shared training corpus.
Publishing recordings or aggregating them requires a separate explicit choice.
Host Xur remains responsible only for container setup, devices and `/robot`.
No public raw motor-target API is added.

## First acceptance experiments

First test stationary segmentation on the saved RGB captures without motor
access. Independently check arm-mask boundaries and separation from the tray,
objects and cables. Evaluate both a proposed automatic initialization and the
minimal click/box fallback, retaining failures rather than using only examples
that worked.

Next, use curated existing recordings to train the smallest pose adapter and
measure image-only per-joint error, ambiguous poses, data coverage and timing
sensitivity on held-out sessions. Compare it with a constant-pose baseline and
an unadapted shared model. Segmentation confidence is not a joint error bound.
For unfamiliar robot families, measure how much verified adaptation data is
needed; no duration or accuracy promise is established yet.

Only then test local adaptation using newly recorded data. Recording movement
uses an independently accepted control/calibration path; the learning worker
cannot bootstrap training by bypassing the existing motion gates. On xur-255,
stationary masks and read-only encoder samples can be gathered now, but they
cannot supply pose variation or independently resolve its missing references.
Current partial base visibility also limits complete-arm validation.

Promoting an evaluated visual model never approves calibration, clears E-stop,
enables torque or certifies travel limits. A separate independently validated
physical calibration is still needed before controller motion. No such solver
is implemented by this design.
