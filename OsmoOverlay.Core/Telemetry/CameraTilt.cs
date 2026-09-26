namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The camera's roll (left/right lean, positive = right) and pitch (nose up/down, positive = up) from the
///     accelerometer, with the vehicle's own acceleration taken out using GPS.
///     An accelerometer can't tell tilt from acceleration (it reads their sum), so on its own braking reads as
///     nose-down and a turn as lean - and a bike leaning into a steady turn reads level, since the cornering
///     force cancels the lean. Where the GPS says the camera is moving, the forward acceleration (dv/dt) and
///     the cornering one (v * heading rate) are known, so both are removed: with k = acceleration / g and m the
///     reading as a fraction of the total, roll = atan(k) - asin(m_y) and pitch = asin(m_x) - atan(k)
///     (a car in a turn reads m_y = k / sqrt(1 + k^2) and gets roll 0, a bike leaning into it reads m_y = 0 and
///     gets its lean). Without GPS, or slower than CompensationFullKmh, k fades to 0 - the plain tilt.
///     Axes and signs: AccelY is roll and AccelX pitch (the controlled tilt-test recording, see
///     TelemetryProcessor), roll negated (verified in the GUI). Measured on 6 real Osmo Action 6 rides
///     (1 s windows above 4 km/h): AccelX rises with GPS dv/dt (slope 0.45-0.78 g/g in every ride), which by
///     the same equivalence fixes pitch's sign (nose-up reads like speeding up); AccelY rises with v * heading
///     rate (0.11-0.29 g/g - riders leaning into turns, a car would be ~1). Compensated pitch varied less than
///     the raw one in all 6 rides.
/// </summary>
public static class CameraTilt
{
	internal const double TimeConstantSeconds = 0.3;

	private const double G = 9.80665;
	// GPS speed and heading are differenced over this window, centered on the frame.
	private const double DerivativeWindowSeconds = 1.0;
	// Below CompensationStartKmh the GPS heading is mostly noise (walking, standing), so nothing is taken out;
	// it ramps in up to CompensationFullKmh rather than switching on with a jump.
	private const double CompensationStartKmh = 5;
	private const double CompensationFullKmh = 10;
	// A GPS glitch can put a spike into a derivative - no vehicle this films corners or brakes past ~1 g.
	private const double MaxAccelerationG = 1.0;

	/// <summary>Smoothed roll/pitch in degrees for every frame. speedKmh/headingDegrees are TelemetryProcessor's, one per frame.</summary>
	public static (double[] Roll, double[] Pitch) Compute(IReadOnlyList<TelemetryFrame> frames, IReadOnlyList<double> speedKmh,
		IReadOnlyList<double> headingDegrees)
	{
		var roll = new double[frames.Count];
		var pitch = new double[frames.Count];
		var segmentStart = 0;
		var segmentEnd = SegmentEnd(frames, 0);
		int before = 0, after = 0;
		double smoothedRoll = 0, smoothedPitch = 0;

		for (var i = 0; i < frames.Count; i++)
		{
			TelemetryFrame frame = frames[i];
			if (i > 0 && frame.StartsAfterGap)
			{
				segmentStart = i;
				segmentEnd = SegmentEnd(frames, i);
				before = after = i;
			}

			var t = frame.SampleTimeSeconds;
			while (before < i && t - frames[before].SampleTimeSeconds > DerivativeWindowSeconds / 2) before++;
			if (after < i) after = i;
			while (after + 1 < segmentEnd && frames[after + 1].SampleTimeSeconds - t <= DerivativeWindowSeconds / 2) after++;
			before = Math.Max(before, segmentStart);

			var (forwardG, lateralG) = VehicleAcceleration(frames, speedKmh, headingDegrees, i, before, after);
			var (rawRoll, rawPitch) = Angles(frame, forwardG, lateralG);

			if (i == segmentStart)
			{
				smoothedRoll = rawRoll;
				smoothedPitch = rawPitch;
			}
			else
			{
				var alpha = 1.0 - Math.Exp(-Math.Max(t - frames[i - 1].SampleTimeSeconds, 0) / TimeConstantSeconds);
				smoothedRoll += alpha * (rawRoll - smoothedRoll);
				smoothedPitch += alpha * (rawPitch - smoothedPitch);
			}

			roll[i] = smoothedRoll;
			pitch[i] = smoothedPitch;
		}

		return (roll, pitch);
	}

	/// <summary>One sample's unsmoothed roll/pitch with the given vehicle acceleration (in g) taken out.</summary>
	internal static (double Roll, double Pitch) Angles(TelemetryFrame frame, double forwardG, double lateralG)
	{
		var magnitude = frame.GForce;
		if (magnitude <= 1e-6) return (0, 0);

		var roll = Math.Atan(lateralG) - Math.Asin(Math.Clamp(frame.AccelY / magnitude, -1, 1));
		var pitch = Math.Asin(Math.Clamp(frame.AccelX / magnitude, -1, 1)) - Math.Atan(forwardG);
		return (AngleMath.RadToDeg(roll), AngleMath.RadToDeg(pitch));
	}

	private static (double ForwardG, double LateralG) VehicleAcceleration(IReadOnlyList<TelemetryFrame> frames,
		IReadOnlyList<double> speedKmh, IReadOnlyList<double> headingDegrees, int i, int before, int after)
	{
		var weight = Math.Clamp((speedKmh[i] - CompensationStartKmh) / (CompensationFullKmh - CompensationStartKmh), 0, 1);
		var dt = frames[after].SampleTimeSeconds - frames[before].SampleTimeSeconds;
		if (weight <= 0 || !frames[i].HasGpsFix || dt < DerivativeWindowSeconds / 2) return (0, 0);

		var forward = (speedKmh[after] - speedKmh[before]) / 3.6 / dt / G;
		var turn = AngleMath.DegToRad(AngleMath.NormalizeDegrees(headingDegrees[after] - headingDegrees[before] + 180) - 180);
		var lateral = speedKmh[i] / 3.6 * turn / dt / G;
		return (weight * Math.Clamp(forward, -MaxAccelerationG, MaxAccelerationG),
			weight * Math.Clamp(lateral, -MaxAccelerationG, MaxAccelerationG));
	}

	/// <summary>One past the last frame of the file `start` is in - derivatives never reach across a gap between files.</summary>
	private static int SegmentEnd(IReadOnlyList<TelemetryFrame> frames, int start)
	{
		var end = start + 1;
		while (end < frames.Count && !frames[end].StartsAfterGap) end++;
		return end;
	}
}
