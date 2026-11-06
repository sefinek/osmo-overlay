namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The direction of travel for a widget that turns with it (the map): the measured heading steadied against GPS noise. Computed
///     once over the whole recording (TelemetryProcessor.Process), so a frame's value never depends on which frames were drawn before.
///     Below MinSpeedKmh, or without a fix, the last trustworthy direction is held; a sudden turn of more than JumpDegrees counts only
///     once it has lasted JumpConfirmSeconds. Nothing is carried across a gap between files, and the stretch before the first
///     trustworthy direction takes that one.
/// </summary>
internal static class HeadingStabilizer
{
	internal const double MinSpeedKmh = 4;
	// A flight log's positions are the aircraft's own fused ones, far steadier than a camera's GPS: its direction is trustworthy at a walking pace.
	private const double FlightLogMinSpeedKmh = 1.5;
	// A direction that comes back after a hold (or a sharp turn of the track) is turned to at this pace at most, so it is seen turning, not jumping.
	private const double MaxTurnDegreesPerSecond = 180;
	private const double FastKmh = 15;
	private const double FastTimeConstantSeconds = 0.25;
	private const double SlowTimeConstantSeconds = 1.2;
	private const double JumpDegrees = 100;
	private const double JumpConfirmSeconds = 1;
	private const double JumpMatchDegrees = 40;
	// After this long without a trustworthy direction (standing, no fix) the next one is a new start, not a jump to doubt.
	private const double HoldSeconds = 2;
	private const double MaxStepSeconds = 0.1;

	/// <param name="speedKmh">The measured speed per frame, before any calibration.</param>
	/// <param name="headingDegrees">The measured heading per frame, 0..360.</param>
	public static double[] Compute(IReadOnlyList<TelemetryFrame> frames, IReadOnlyList<double> speedKmh, IReadOnlyList<double> headingDegrees)
	{
		double[] result = new double[frames.Count];
		double minSpeed = frames.Any(f => f.DroneAttitude is not null) ? FlightLogMinSpeedKmh : MinSpeedKmh;
		int segmentStart = 0;
		for (int i = 1; i <= frames.Count; i++)
		{
			if (i < frames.Count && !frames[i].StartsAfterGap) continue;

			Segment(frames, speedKmh, headingDegrees, segmentStart, i, minSpeed, result);
			segmentStart = i;
		}

		return result;
	}

	private static void Segment(IReadOnlyList<TelemetryFrame> frames, IReadOnlyList<double> speedKmh, IReadOnlyList<double> headingDegrees, int start, int end, double minSpeed, double[] result)
	{
		double? held = null;
		double lastUpdate = 0;
		double? pendingTarget = null;
		double pendingSince = 0;
		int firstTrusted = -1;

		for (int i = start; i < end; i++)
		{
			double t = frames[i].SampleTimeSeconds;
			bool trusted = frames[i].HasGpsFix && speedKmh[i] >= minSpeed;
			if (trusted)
			{
				double target = headingDegrees[i];
				if (held is not { } current)
				{
					held = target;
					lastUpdate = t;
					firstTrusted = i;
				}
				else
				{
					double delta = Signed(target - current);
					bool restart = t - lastUpdate > HoldSeconds;
					if (!restart && Math.Abs(delta) > JumpDegrees)
					{
						if (pendingTarget is not { } pending || Math.Abs(Signed(target - pending)) > JumpMatchDegrees)
						{
							pendingTarget = target;
							pendingSince = t;
						}

						trusted = t - pendingSince >= JumpConfirmSeconds;
					}
					else
					{
						pendingTarget = null;
					}

					if (trusted)
					{
						double constant = TimeConstant(speedKmh[i], minSpeed);
						// A long wait since the last update (standing, a doubted jump) is not time the heading spent turning:
						// the way back to the current direction stays smooth.
						double elapsed = Math.Clamp(t - lastUpdate, 0, MaxStepSeconds);
						double step = 1 - Math.Exp(-elapsed / constant);
						double turn = Math.Clamp(Signed(target - current) * step, -MaxTurnDegreesPerSecond * elapsed, MaxTurnDegreesPerSecond * elapsed);
						held = AngleMath.NormalizeDegrees(current + turn);
						lastUpdate = t;
						pendingTarget = null;
					}
				}
			}

			result[i] = held ?? 0;
		}

		if (firstTrusted > start)
		{
			double first = result[firstTrusted];
			for (int i = start; i < firstTrusted; i++) result[i] = first;
		}
		else if (firstTrusted < 0)
		{
			for (int i = start; i < end; i++) result[i] = headingDegrees[i];
		}
	}

	/// <summary>Slower the slower the vehicle goes: its direction is less certain then.</summary>
	private static double TimeConstant(double speedKmh, double minSpeed)
	{
		double share = Math.Clamp((speedKmh - minSpeed) / (FastKmh - minSpeed), 0, 1);
		return SlowTimeConstantSeconds + (FastTimeConstantSeconds - SlowTimeConstantSeconds) * share;
	}

	private static double Signed(double degrees)
	{
		double normalized = AngleMath.NormalizeDegrees(degrees);
		return normalized > 180 ? normalized - 360 : normalized;
	}
}
