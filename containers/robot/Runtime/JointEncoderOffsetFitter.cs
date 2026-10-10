namespace Xur.Robot;

// Offline math for a supplied one-encoder-turn-per-joint-turn model. This does
// not interpret LeRobot normalization or the motor's EEPROM Homing_Offset.
public sealed record JointEncoderReference(
    string RobotModel, string Joint, int EncoderTicksPerTurn, int Direction,
    string JointAngleConvention, string EncoderReadingConvention,
    string ObservationProvenance, bool JointAnglesHaveAnchoredReference);

public sealed record JointEncoderObservation(string SampleId, int EncoderCount, double JointAngleDegrees);

// Statistical quality thresholds supplied by the caller, never motion limits.
public sealed record EncoderOffsetQuality(
    int MinimumObservations, double MinimumCircularSpanDegrees,
    double MaximumResidualDegrees, double MaximumRmsDegrees);

public sealed record EncoderOffsetResidual(string SampleId, double CountResidual, double JointDegreesResidual);

public enum EncoderOffsetFitStatus { ProvisionalFit, InsufficientData, Ambiguous, Rejected }

public sealed record EncoderOffsetFit(
    JointEncoderReference Reference, EncoderOffsetQuality Quality,
    IReadOnlyList<JointEncoderObservation> Observations, EncoderOffsetFitStatus Status,
    double? EncoderCountAtReferenceZero, double CircularSpanDegrees,
    double? RmsDegrees, IReadOnlyList<EncoderOffsetResidual> Residuals, string Reason)
{
    public bool ApprovedForMotorUse => false;
    public bool MechanicalTurnBranchEstablished => false;
}

public sealed record EncoderOffsetValidation(
    EncoderOffsetQuality Quality, IReadOnlyList<JointEncoderObservation> Observations,
    bool Passed, double CircularSpanDegrees, double? RmsDegrees,
    IReadOnlyList<EncoderOffsetResidual> Residuals, string Reason)
{
    public bool ApprovedForMotorUse => false;
}

public static class JointEncoderOffsetFitter
{
    // raw = countAtReferenceZero + direction * ticksPerTurn * angleDegrees/360
    // modulo ticksPerTurn. The reference zero is supplied, not discovered here.
    public static EncoderOffsetFit Fit(JointEncoderReference reference,
        IReadOnlyList<JointEncoderObservation> observations, EncoderOffsetQuality quality)
    {
        ValidateReference(reference); ValidateQuality(quality, 3);
        var samples = ValidateObservations(reference, observations);
        var span = CircularSpan(samples);
        if (samples.Length < quality.MinimumObservations || span < quality.MinimumCircularSpanDegrees)
            return Result(EncoderOffsetFitStatus.InsufficientData, null, null, [], "Too few observations or too little distinct circular joint-angle variation.");

        var offsets = samples.Select(s => Wrap(s.EncoderCount - ExpectedCount(reference, s.JointAngleDegrees), reference.EncoderTicksPerTurn)).ToArray();
        var x = offsets.Average(v => Math.Cos(v * 2 * Math.PI / reference.EncoderTicksPerTurn));
        var y = offsets.Average(v => Math.Sin(v * 2 * Math.PI / reference.EncoderTicksPerTurn));
        // An antipodal/balanced distribution has no unique circular mean.
        if (Math.Sqrt(x * x + y * y) <= 1e-10)
            return Result(EncoderOffsetFitStatus.Ambiguous, null, null, [], "Offset samples have no unique circular mean.");
        var zero = Wrap(Math.Atan2(y, x) * reference.EncoderTicksPerTurn / (2 * Math.PI), reference.EncoderTicksPerTurn);
        var residuals = Residuals(reference, samples, zero);
        var rms = Rms(residuals);
        if (!WithinQuality(residuals, rms, quality))
            return Result(EncoderOffsetFitStatus.Rejected, null, rms, residuals, "Every supplied observation must meet the residual and RMS limits; no outliers were removed.");
        return Result(EncoderOffsetFitStatus.ProvisionalFit, zero, rms, residuals, "Provisional modulo-turn fit at the supplied joint reference zero; no motor calibration or mechanical branch established.");

        EncoderOffsetFit Result(EncoderOffsetFitStatus status, double? zeroCount, double? error,
            EncoderOffsetResidual[] errors, string reason) => new(reference, quality, Array.AsReadOnly(samples), status, zeroCount, span, error, Array.AsReadOnly(errors), reason);
    }

    // Uses the selected fit unchanged. IDs must be disjoint; the caller must
    // also ensure these are genuinely separate captures, not renamed samples.
    public static EncoderOffsetValidation ValidateHeldOut(EncoderOffsetFit fit, JointEncoderReference reference,
        IReadOnlyList<JointEncoderObservation> observations, EncoderOffsetQuality quality)
    {
        ArgumentNullException.ThrowIfNull(fit);
        ValidateReference(fit.Reference); ValidateReference(reference); ValidateQuality(quality, 2);
        if (reference != fit.Reference)
            throw new ArgumentException("Held-out observations must declare the identical model, angle reference and encoder reading context.", nameof(reference));
        var samples = ValidateObservations(fit.Reference, observations);
        var training = fit.Observations.Select(s => s.SampleId).ToHashSet(StringComparer.Ordinal);
        if (samples.Any(s => training.Contains(s.SampleId)))
            throw new ArgumentException("Held-out sample IDs must be disjoint from fitting observations.", nameof(observations));
        var span = CircularSpan(samples);
        if (fit.Status != EncoderOffsetFitStatus.ProvisionalFit || fit.EncoderCountAtReferenceZero is not double zero
            || !double.IsFinite(zero) || zero < 0 || zero >= fit.Reference.EncoderTicksPerTurn)
            return Result(false, null, [], "A valid provisional fit is required; validation does not select or refit an offset.");
        if (samples.Length < quality.MinimumObservations || span < quality.MinimumCircularSpanDegrees)
            return Result(false, null, [], "Too few held-out observations or too little distinct circular joint-angle variation.");
        var residuals = Residuals(fit.Reference, samples, zero);
        var rms = Rms(residuals); var passed = WithinQuality(residuals, rms, quality);
        return Result(passed, rms, residuals, passed
            ? "Separate samples meet the supplied quality criteria; this is not approval for motor use."
            : "Held-out samples failed the supplied quality criteria; the fitted offset was not changed.");

        EncoderOffsetValidation Result(bool passed, double? error, EncoderOffsetResidual[] errors, string reason)
            => new(quality, Array.AsReadOnly(samples), passed, span, error, Array.AsReadOnly(errors), reason);
    }

    static void ValidateReference(JointEncoderReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.EncoderTicksPerTurn < 2 || reference.Direction is not (1 or -1)
            || !reference.JointAnglesHaveAnchoredReference
            || string.IsNullOrWhiteSpace(reference.RobotModel) || string.IsNullOrWhiteSpace(reference.Joint)
            || string.IsNullOrWhiteSpace(reference.JointAngleConvention)
            || string.IsNullOrWhiteSpace(reference.EncoderReadingConvention)
            || string.IsNullOrWhiteSpace(reference.ObservationProvenance))
            throw new ArgumentException("Supply the full encoder cycle, known direction +/-1, anchored joint-angle convention, reading convention and provenance.", nameof(reference));
    }

    static void ValidateQuality(EncoderOffsetQuality quality, int minimum)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (quality.MinimumObservations < minimum
            || !double.IsFinite(quality.MinimumCircularSpanDegrees) || quality.MinimumCircularSpanDegrees <= 0 || quality.MinimumCircularSpanDegrees >= 360
            || !double.IsFinite(quality.MaximumResidualDegrees) || quality.MaximumResidualDegrees <= 0 || quality.MaximumResidualDegrees >= 180
            || !double.IsFinite(quality.MaximumRmsDegrees) || quality.MaximumRmsDegrees <= 0 || quality.MaximumRmsDegrees > quality.MaximumResidualDegrees)
            throw new ArgumentException("Supply finite positive statistical thresholds, sufficient observations, and residual limits below a half turn.", nameof(quality));
    }

    static JointEncoderObservation[] ValidateObservations(JointEncoderReference reference, IReadOnlyList<JointEncoderObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var samples = observations.ToArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            if (sample is null || string.IsNullOrWhiteSpace(sample.SampleId) || !ids.Add(sample.SampleId)
                || sample.EncoderCount < 0 || sample.EncoderCount >= reference.EncoderTicksPerTurn
                || !double.IsFinite(sample.JointAngleDegrees))
                throw new ArgumentException("Observations require unique IDs, finite joint angles in degrees and counts within the full encoder cycle.", nameof(observations));
            // Large double angles can no longer resolve even one encoder count.
            // This is a numerical precision guard, not a physical joint limit.
            var adjacent = Math.BitIncrement(sample.JointAngleDegrees);
            if (!double.IsFinite(adjacent) || Math.Abs(adjacent - sample.JointAngleDegrees) >= 360d / reference.EncoderTicksPerTurn)
                throw new ArgumentException("Joint angle precision cannot resolve an encoder count.", nameof(observations));
        }
        return samples;
    }

    static double ExpectedCount(JointEncoderReference reference, double angle)
        => reference.Direction * (angle % 360) / 360 * reference.EncoderTicksPerTurn;

    static double Wrap(double value, double period)
    {
        var wrapped = value % period;
        if (wrapped < 0) wrapped += period;
        return wrapped == 0 || wrapped >= period ? 0 : wrapped;
    }

    static double CircularSpan(JointEncoderObservation[] samples)
    {
        if (samples.Length < 2) return 0;
        var phases = samples.Select(s => Wrap(s.JointAngleDegrees, 360)).Order().ToArray();
        var largestGap = phases[0] + 360 - phases[^1];
        for (var i = 1; i < phases.Length; i++) largestGap = Math.Max(largestGap, phases[i] - phases[i - 1]);
        return 360 - largestGap;
    }

    static EncoderOffsetResidual[] Residuals(JointEncoderReference reference, JointEncoderObservation[] samples, double zero)
        => samples.Select(s =>
        {
            var error = Wrap(s.EncoderCount - zero - ExpectedCount(reference, s.JointAngleDegrees) + reference.EncoderTicksPerTurn / 2d, reference.EncoderTicksPerTurn) - reference.EncoderTicksPerTurn / 2d;
            return new EncoderOffsetResidual(s.SampleId, error, reference.Direction * error * 360 / reference.EncoderTicksPerTurn);
        }).ToArray();

    static double Rms(EncoderOffsetResidual[] residuals) => Math.Sqrt(residuals.Average(s => s.JointDegreesResidual * s.JointDegreesResidual));
    static bool WithinQuality(EncoderOffsetResidual[] residuals, double rms, EncoderOffsetQuality quality)
        => double.IsFinite(rms) && rms <= quality.MaximumRmsDegrees && residuals.All(s => Math.Abs(s.JointDegreesResidual) <= quality.MaximumResidualDegrees);
}
