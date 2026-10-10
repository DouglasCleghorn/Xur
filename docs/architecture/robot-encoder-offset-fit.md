# Offline joint encoder-offset fit

`JointEncoderOffsetFitter` is pure .NET preparation for future known-pose cradle
or independently validated visual joint-angle observations. It has no API route,
hardware access, calibration receipt writer or connection to `AutoCalibrate`.
Current tests use synthetic observations; no real robot joint-angle reference has
been established by this component.

The caller supplies the robot model/revision and joint, an anchored joint-angle
convention in degrees, known direction `+1` or `-1`, full encoder ticks per turn,
encoder reading convention and observation provenance. This first component
assumes one encoder revolution per model joint revolution; gearing and grippers
that use percent-of-range need a separate explicit adapter. It never infers the
direction, mechanical turn branch, mounting frame, physical absolute zero or
joint limits. A supplied provenance/reference declaration is required input,
not proof that the physical setup has been measured correctly.

The fitted equation is:

```text
EncoderCount ≡ EncoderCountAtReferenceZero
             + Direction × EncoderTicksPerTurn × JointAngleDegrees / 360
             (modulo EncoderTicksPerTurn)
```

`EncoderCountAtReferenceZero` is a fractional, provisional modulo-turn encoder
reading at the supplied model zero. For an STS encoder the cycle is 4096 counts;
4095 is the maximum code, not the period. This output is neither LeRobot's
calibration JSON nor an EEPROM `Homing_Offset`. A motor's present-position reading
can already reflect an existing homing offset. The caller must describe that
reading context and keep it unchanged between fit and validation. No offset
register conversion or write is implemented.

The fitter takes a circular mean of count-offset samples. It rejects an undefined
balanced/antipodal mean, too few observations, too little circular joint-angle
variation, or any individual/RMS residual outside the caller's supplied quality
criteria. It keeps all observations; outliers are not silently discarded. Poses
separated by whole turns give the same phase and do not establish variation or a
mechanical branch. Residuals use the shortest signed count difference; quality
limits must stay below a half turn. Criteria are statistical thresholds, not
motor movement or safety limits.

`ValidateHeldOut` checks separate sample IDs against the unchanged fit and requires
an identical reference/reading-context declaration. It never refits on validation
data. The caller must ensure these are genuinely separate captures: distinct IDs
alone cannot prove independence. Both fit and validation permanently report
`ApprovedForMotorUse = false`; even passing residuals cannot resolve unknown
mount/base-to-first-joint or tool-to-terminal-joint reference gauges.

Run the offline synthetic checks with:

```sh
dotnet run --project tests/Xur.Robot.Tests -c Release
```

The checks cover count and angle wraparound, explicit sign, ambiguous contradictory
offsets, whole-turn repetition, insufficient variation, count quantization,
outliers, invalid inputs/context, and independent validation failures. Automatic
powered calibration remains separate work requiring real anchored observations,
coverage, physical reference validation and a reviewed motion procedure.
