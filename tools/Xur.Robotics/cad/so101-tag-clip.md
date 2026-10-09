# SO-101 upper-arm AprilTag clip — prototype v1

One face-down printable PETG clip supports a **35 × 43 mm flat label face**.
Its two flexible fingers clip over the outside rail of the SO-101 printed upper
arm. It is a fit-and-visibility prototype, not a validated calibration fixture.
No print job or robot movement has been performed.

## Geometry and intended location

The reference is TheRobotStudio's
[Upper_arm_SO101.stl at commit a758567](https://github.com/TheRobotStudio/SO-ARM100/blob/a758567c3978dfeefe282ede0500085a48fe8f78/STL/SO101/Individual/Upper_arm_SO101.stl),
inspected as actual mesh sections, not estimated from a photograph. The local
reference is `.build/robotics/upstream/SO-ARM100/`. Its Apache-2.0 license remains
there; the upstream mesh is neither embedded in the clip nor redistributed as
MIT. Original Xur CAD source and these notes use the root MIT license.

“Positive-Z rail” below means the individual upstream STL coordinate frame,
**not gravity, the robot base frame, or a URDF link frame**. The collar occupies
upstream X=43..49 mm on that rail, centered at X=46, Y=12. Its nominal cross-section
is 24 mm wide (Y=0..24), 15 mm deep (Z=16.7..31.7), with rounded corners. This is
one rigid printed member of a moving link, away from the gripper. Only the
printed link itself was modeled in the clearance check.

The internal collar is 24.5 mm wide, with 0.25 mm nominal side clearance; two
45° lead-in/retention ramps narrow the mouth to 23 mm. Fingers are 1.8 mm thick
and 6 mm wide along the link. Their roots have 45° transitions into the plate.
The overall print envelope is **35 × 43 × 17.2 mm**. The front plate is 2.4 mm
thick; its center has a 0.6 mm back pad that seats on the rail.

This prototype has **no positive longitudinal stop**. It can slide along the
rail or rock if the print fits loosely. A tight-looking clip is not proof of a
stable marker transform. Verify retention and repeatability by hand; if it can
move under light finger pressure, do not use it for robot calibration. Tune
`SIDE_CLEARANCE` after measuring the actual printed rail. A thin removable
friction shim can be evaluated during fit testing, but its thickness must be
included in the measured transform. Do not glue it permanently to establish fit.

## Label and measurable transform

Use white PETG and a flat white adhesive label. The actual label family is
`tagStandard41h12`: **15.24 mm detector reference square**, 27.432 mm full
9-cell artwork, and **33.528 mm including the mandatory one-module white quiet
margin on every side**. These are different dimensions. The ID footer adds
7.62 mm below that square: about **33.528 × 41.148 mm** total. Trim only unused
roll margins to fit the face; do not crop artwork or the white halo. No guessed
pattern, engraved code, or raised border is part of this design.

Center that full 33.528 × 41.148 mm label footprint on the 35 × 43 mm face,
with the footer toward **negative print Y**. The detector reference center is
then nominally `(0, 3.81, 0)` in print coordinates. Print Z=0 is the outward
label face, with outward normal -Z; print +Y points toward the top of the tag.
Measure actual label placement and thickness instead of assuming perfect trim.

The intended installed transform, in millimetres, is:

```text
upstream_X = 46 + print_Y
upstream_Y = 12 + print_X
upstream_Z = 34.7 - print_Z
```

Thus the nominal marker center is upstream `(49.81, 12, 34.7)` plus label
thickness in +Z. This is a CAD placement, **not a calibrated robot transform**.
The frame mapping, actual axial position, play and label offset must all be
measured. As one accessible placement reference, the mesh's rail tip reaches
upstream X=77.0613: the collar edge at X=49 is **28.0613 mm back from that tip**,
measured parallel to upstream X. This is a nominal positioning aid; caliper
access, printed tip shape and fit must be checked on the actual arm. Mark the
accepted position and record the final marker-to-link transform only after the
mount passes a repeatable seat/remove/reseat check.

## Print and fit

- Keep the exported orientation: the large, unmarked **label face flat on the
  bed**, with the open clip fingers pointing up. Do not auto-orient it onto the
  finger tips. The inward retention ramps rise at 45°; no support is intended.
- Starting settings: white PETG, 0.4 mm nozzle, 0.2 mm layers, four perimeters,
  five top/bottom layers, 25–35% infill. Use the filament supplier's validated
  temperature profile. A smooth release-compatible build surface gives the
  adhesive label a flatter seat. PETG's attachment to the bed needs the surface
  manufacturer's release guidance. A brim is optional if your printer lifts
  plate corners; remove it without rounding the label face.
- Inspect the slicer at the finger roots and hooks: retain the solid fingers,
  45° slopes and full flat plate. Supports on the label face are unnecessary.
  This orientation puts finger bending across layer bonds; inspect for cracks
  or whitening after each insertion. Retention strength and fatigue have not
  been tested. Do not force a brittle or oversized print onto the arm.
- First fit with robot power disconnected and the arm supported. Measure the
  real rail width/depth and compare with 24 × 15 mm; revisions, print shrinkage,
  coating and alternate arm parts can invalidate this fit. Keep the fingers on
  the plastic rail only. They must not capture a cable or bear on a motor,
  ventilation opening, fastener head, shaft or bearing.
- The 43 mm paddle extends toward the adjacent pivot. Motors, fasteners, cables,
  the opposite arm and the assembled XLeRobot were not included in the link-only
  intersection test. Reject or reposition the mount if any can touch it. Check
  the intended range by hand while unpowered before considering any powered
  use; respect the arm manufacturer's manual-movement guidance. Verify camera
  visibility at the intended poses. Neither motion nor visibility is validated.

## Rebuild and evidence

Editable original CAD: `so101_tag_clip.py`. Dimensions and placement parameters
are near its top. It generates a standalone editable `.scad` companion using
exactly the same polygon as the STL. Blender and OpenSCAD are not required for
generation. Python packages are pinned in `requirements.txt`; use an ignored
virtual environment under `.build/`.

The environment used here is `.build/robotics/cad-tools/venv/bin/python`.
From the repository root:

```sh
.build/robotics/cad-tools/venv/bin/python tools/Xur.Robotics/cad/so101_tag_clip.py \
  --upstream-stl .build/robotics/upstream/SO-ARM100/STL/SO101/Individual/Upper_arm_SO101.stl
```

Outputs (all ignored):

- `.build/robotics/so101-tag-mount/so101-upper-arm-tag-clip.stl`
- `.build/robotics/so101-tag-mount/so101-upper-arm-tag-clip.scad`
- `.build/evidence/so101-tag-mount/mesh-validation.json`
- `.build/evidence/so101-tag-mount/clip-review.png`
- `.build/evidence/so101-tag-mount/clip-on-link.png`

The serialized STL was reopened and checked: one connected solid, watertight,
consistent winding, positive volume (~4037 mm³), 68 triangles, expected bounds,
and a maximum downward overhang of 45° from vertical above the bed.
The installed mesh has zero volumetric intersection with the pinned printed
upper-arm STL. Nominal surface contact at the top rail is intentional. That
result does not cover the assembled robot, manufacturing variation, spring
force, vibration, label detection or motion. CAD renders and source inspection
captures are registered in `docs/screenshots.md`.
