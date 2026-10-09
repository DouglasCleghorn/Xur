# Marker print kit

Run the .NET 10 file-based generator from the repository root:

```bash
dotnet run --file tools/Xur.Robotics/print_markers.cs \
  --artifacts-path .build/robotics/marker-generator
```

Its optional single argument selects an output directory. The default is
`.build/robotics/markers/print-kit/`. Generated artwork, PDF sheets, labels and
manifests belong in ignored output paths. The first run downloads pinned
official `tagStandard41h12` artwork and its BSD-2-Clause license; subsequent
runs reuse the local cache.

The Letter and A4 PDFs each have five pages: small markers 0–15, medium markers
16–24, large markers 25–28, a 20 mm checkerboard, and instructions with the
artwork license. Print at Actual size / 100%, then measure the 50 mm ruler and
checkerboard squares. Preserve the complete pattern and white margin when
cutting. The checkerboard requires a sheet printer; it does not fit a QL-800.

Individual QL-800 proofs and 300 dpi PGM rasters are in
`individual-ql800-62mm/`. They use a 696-dot printable canvas for a 62 mm
continuous roll, with an identifier below each marker. Send the raster at its
native size without fitting or resampling. The label manifest records the exact
pixel dimensions, artwork hash and reference size for every ID.

| Output | IDs | Pose reference edge | Full pattern | Pattern with white margin |
| --- | --- | --- | --- | --- |
| Sheet, small | 0–15 | 15 mm | 27 mm | 33 mm |
| Sheet, medium | 16–24 | 25 mm | 45 mm | 55 mm |
| Sheet, large | 25–28 | 40 mm | 72 mm | 88 mm |
| QL-800, small | 0–15 | 15.24 mm | 27.432 mm | 33.528 mm |
| QL-800, medium | 16–28 | 25.4 mm | 45.72 mm | 55.88 mm |

For this family, pose reference size measures the five-cell square between
detected corners; the complete official pattern spans nine cells. Do not crop
the outer data cells or enter the full artwork width as the pose reference.
Measure actual printed dimensions before using any label for robot calibration.
Large sheet markers have smaller QL-800 alternatives with the same IDs, so
record which size is installed and use each ID only once in a camera view.

The QL-800 must use the print mode matching its loaded media. In particular,
DK-22251 black/red stock requires two-color mode even when the artwork contains
only black. The initial stock-driver attempt on host 7700 was rejected with a
flashing red light; a completed CUPS queue entry did not establish printing.
After a printer power cycle, tag 00 received both printing-completed and
return-to-idle acknowledgements using the upstream two-color raster stream over
direct USB. The owner confirmed physical output and installed tag 00 on the tray;
a fresh head-camera frame decoded that label with zero bit errors. Tags 01 and
02 also received completed-and-idle acknowledgements. The next transfer lost
its USB device and its outcome is unknown. Printing was then paused at the
owner's request; do not resume or resend it automatically. Actual dimensions,
remaining output quality and permanent mounting still need assessment. The
generated manifests describe prepared files and do not claim they were printed;
actual attempt receipts are stored separately under ignored evidence paths.

Rendered Letter/A4 tag pages and all 29 label rasters were checked with the
native upstream AprilTag detector at zero allowed bit errors. Expected IDs and
reference dimensions passed. This verifies digital patterns, not the physical
printer, permanent mounting transforms or camera coverage. Mounting locations
and link assignments still require assessment on the robot; printing this kit
does not enable motor motion or automatic calibration.
