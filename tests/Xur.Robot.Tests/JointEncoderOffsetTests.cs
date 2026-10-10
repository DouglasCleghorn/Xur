using Xur.Robot;

public static class JointEncoderOffsetTests
{
    public static void Run(Action<bool,string> check)
    {
        var reference = new JointEncoderReference("synthetic SO101 model revision fixture-1", "elbow", 4096, 1,
            "Degrees about model +joint axis; zero is a supplied anchored model pose, not a discovered physical zero.",
            "Synthetic modulo encoder values; unchanged declared EEPROM reading context, not EEPROM writes.",
            "Independent synthetic angle/count pairs", true);
        var quality = new EncoderOffsetQuality(3, 15, .15, .1);
        var heldOutQuality = new EncoderOffsetQuality(2, 15, .15, .1);
        static double Mod(double value, double period) => (value % period + period) % period;
        static bool Near(double value, double expected, double tolerance = 1e-8) => Math.Abs(value - expected) < tolerance;
        JointEncoderObservation[] Samples(JointEncoderReference model, double offset, string prefix, params double[] angles)
            => angles.Select((angle, i) => new JointEncoderObservation(prefix + i,
                (int)Mod(Math.Round(offset + model.Direction * model.EncoderTicksPerTurn * angle / 360), model.EncoderTicksPerTurn), angle)).ToArray();
        void Throws(Action action, string name)
        {
            var threw = false; try { action(); } catch (ArgumentException) { threw = true; }
            check(threw, name);
        }
        var fit = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 4080, "fit", -45, 0, 45), quality);
        check(fit.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(fit.EncoderCountAtReferenceZero!.Value, 4080), "encoder fit crosses 4096-count wrap at a supplied reference zero");
        check(Near(fit.CircularSpanDegrees, 90) && Near(fit.RmsDegrees!.Value, 0), "encoder fit uses circular phase span and exact synthetic residuals");
        check(!fit.ApprovedForMotorUse && !fit.MechanicalTurnBranchEstablished, "encoder fit never approves motors or a mechanical turn branch");
        var negative = reference with { Direction = -1 };
        var reversed = JointEncoderOffsetFitter.Fit(negative, Samples(negative, 16, "reverse", -45, 0, 45), quality);
        check(reversed.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(reversed.EncoderCountAtReferenceZero!.Value, 16), "encoder fit honors explicitly supplied negative direction");
        var wrongDirection = JointEncoderOffsetFitter.Fit(reference, Samples(negative, 16, "wrong", -45, 0, 45), quality);
        check(wrongDirection.Status == EncoderOffsetFitStatus.Rejected, "encoder fit rejects inconsistent supplied direction without inferring another");
        var angleWrap = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 100, "angle", 170, 180, -170), quality);
        check(angleWrap.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(angleWrap.CircularSpanDegrees, 20), "encoder fit handles signed joint-angle wrap without claiming unwrapping");
        var multipleTurns = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 4080, "turns", -405, 360, 765), quality);
        check(multipleTurns.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(multipleTurns.EncoderCountAtReferenceZero!.Value, 4080), "encoder fit reduces equivalent whole turns to the same provisional modulo mapping");
        var repeated = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 100, "repeat", 0, 360, 720), quality);
        check(repeated.Status == EncoderOffsetFitStatus.InsufficientData && repeated.EncoderCountAtReferenceZero == null, "whole-turn repeated poses provide no circular variation");
        var weak = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 100, "weak", 179, -180, -179), quality);
        check(weak.Status == EncoderOffsetFitStatus.InsufficientData && Near(weak.CircularSpanDegrees, 2), "encoder fit rejects weak variation across signed angle boundary");
        var tooFew = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 100, "few", -45, 45), quality);
        check(tooFew.Status == EncoderOffsetFitStatus.InsufficientData, "encoder fit requires the supplied observation count");
        var halfTurn = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 0, "half", 0, 180, 360), quality);
        check(halfTurn.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(halfTurn.EncoderCountAtReferenceZero!.Value, 0), "half-turn poses fit only the explicitly supplied sign; sign inference is absent");
        var antipodal = new[] { new JointEncoderObservation("a",0,0), new JointEncoderObservation("b",2560,45), new JointEncoderObservation("c",1024,90), new JointEncoderObservation("d",3584,135) };
        var ambiguous = JointEncoderOffsetFitter.Fit(reference, antipodal, quality);
        check(ambiguous.Status == EncoderOffsetFitStatus.Ambiguous && ambiguous.EncoderCountAtReferenceZero == null, "balanced antipodal offsets have no selected circular mean");
        var outliers = Samples(reference, 100, "bad", -45, 0, 45, 90);
        outliers[2] = outliers[2] with { EncoderCount = outliers[2].EncoderCount + 100 };
        var rejected = JointEncoderOffsetFitter.Fit(reference, outliers, quality);
        check(rejected.Status == EncoderOffsetFitStatus.Rejected && rejected.Residuals.Count == 4 && rejected.EncoderCountAtReferenceZero == null, "outlier observations reject the entire fit and are retained in diagnostics");
        var quantized = JointEncoderOffsetFitter.Fit(reference, Samples(reference, 123, "quant", -31, 9, 53, 87), quality);
        check(quantized.Status == EncoderOffsetFitStatus.ProvisionalFit && quantized.RmsDegrees > 0 && quantized.RmsDegrees < .05, "encoder fit tolerates explicit bounded count quantization");
        var period1024 = reference with { EncoderTicksPerTurn = 1024 };
        var custom = JointEncoderOffsetFitter.Fit(period1024, Samples(period1024, 1018, "custom", -45,0,45), quality);
        check(custom.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(custom.EncoderCountAtReferenceZero!.Value, 1018), "encoder cycle is explicit rather than hardcoded to STS motors");
        var validation = JointEncoderOffsetFitter.ValidateHeldOut(fit, reference, Samples(reference,4080,"held",-90,90), heldOutQuality);
        check(validation.Passed && Near(validation.RmsDegrees!.Value,0) && !validation.ApprovedForMotorUse, "separate held-out samples verify the mapping without approving motor use");
        var nearZero = JointEncoderOffsetFitter.Fit(reference, Samples(reference,0,"zero",-30.1,0,30.1), quality);
        var nearZeroValidation = JointEncoderOffsetFitter.ValidateHeldOut(nearZero,reference,Samples(reference,0,"zeroheld",-90,90),heldOutQuality);
        check(nearZero.Status == EncoderOffsetFitStatus.ProvisionalFit && Near(nearZero.EncoderCountAtReferenceZero!.Value,0) && nearZeroValidation.Passed,
            "near-zero offset with rounded counts across zero remains canonical and validates");
        var failedValidation = JointEncoderOffsetFitter.ValidateHeldOut(fit, reference, Samples(reference,4084,"drift",-90,90), heldOutQuality);
        check(!failedValidation.Passed && Near(fit.EncoderCountAtReferenceZero!.Value,4080) && Near(failedValidation.Residuals[0].CountResidual,4), "held-out failure preserves the original fit rather than refitting");
        check(!JointEncoderOffsetFitter.ValidateHeldOut(fit, reference, Samples(reference,4080,"short",0), heldOutQuality).Passed, "held-out validation rejects insufficient observations");
        check(!JointEncoderOffsetFitter.ValidateHeldOut(ambiguous, reference, Samples(reference,4080,"ambheld",-90,90), heldOutQuality).Passed, "held-out validation cannot select an ambiguous fit");
        check(!JointEncoderOffsetFitter.ValidateHeldOut(fit,reference,Samples(reference,2032,"antipodalheld",-90,90),heldOutQuality).Passed,
            "half-turn held-out residuals are rejected without choosing a mechanical branch");
        var negativeValidation = JointEncoderOffsetFitter.ValidateHeldOut(reversed, negative, Samples(negative,20,"negheld",-90,90), heldOutQuality);
        check(!negativeValidation.Passed && Near(negativeValidation.Residuals[0].JointDegreesResidual,-4d*360/4096), "residual joint-angle sign follows supplied negative direction");
        var unaltered = Samples(reference, 100, "snapshot", -45,0,45);
        var snapshotFit = JointEncoderOffsetFitter.Fit(reference,unaltered,quality);
        unaltered[0] = new("altered",0,0);
        check(snapshotFit.Observations[0].SampleId == "snapshot0", "fit retains an independent read-only observation snapshot");
        Throws(() => JointEncoderOffsetFitter.ValidateHeldOut(fit, reference, Samples(reference,4080,"fit",-90,90),heldOutQuality), "held-out IDs cannot overlap fitting IDs");
        Throws(() => JointEncoderOffsetFitter.ValidateHeldOut(fit, reference with {EncoderReadingConvention="different existing EEPROM homing context"},Samples(reference,4080,"context",-90,90),heldOutQuality), "held-out encoder context must match the fitting declaration");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {Direction=0},[],quality), "encoder direction zero is invalid");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {Direction=2},[],quality), "encoder direction must be exactly plus or minus one");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {EncoderTicksPerTurn=1},[],quality), "encoder full cycle must be valid");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {JointAnglesHaveAnchoredReference=false},[],quality), "unanchored relative angles cannot claim a reference-zero fit");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {JointAngleConvention=""},[],quality), "joint model angle convention is required");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {ObservationProvenance=""},[],quality), "observation provenance is required");
        Throws(() => JointEncoderOffsetFitter.Fit(reference with {EncoderReadingConvention=""},[],quality), "encoder reading convention is required");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[new("n",-1,0)],quality), "negative modulo encoder code is rejected");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[new("n",4096,0)],quality), "4096 is the period, not a valid STS modulo encoder code");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[new("n",0,double.NaN)],quality), "nonfinite joint angles are rejected");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[new("n",0,double.MaxValue)],quality), "joint angles lacking count-level floating-point precision are rejected");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[new("same",0,0),new("same",512,45)],quality), "duplicate sample IDs are rejected");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[],quality with {MaximumResidualDegrees=180}), "half-turn residual limits cannot approve ambiguous shortest differences");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[],quality with {MinimumCircularSpanDegrees=double.NaN}), "nonfinite quality thresholds are rejected");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[],quality with {MinimumObservations=2}), "fit quality requires at least three observations");
        Throws(() => JointEncoderOffsetFitter.Fit(reference,[],quality with {MaximumRmsDegrees=1}), "RMS threshold cannot exceed the individual residual threshold");
    }
}
