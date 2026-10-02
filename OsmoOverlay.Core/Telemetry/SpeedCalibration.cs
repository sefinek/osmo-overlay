namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The one number that makes up for a GPS reading low: a percentage added to every speed (OverlaySettings.SpeedCorrectionPercent,
///     TelemetryProcessor.Process). A scale rather than a fixed amount - a receiver's error grows with speed, and standing still must stay 0.
/// </summary>
public static class SpeedCalibration
{
	/// <summary>The most a speed can be raised by - a calibration, not a way to invent speed.</summary>
	public const double MaxPercent = 50;

	/// <summary>The precision of a worked-out correction: at 25 km/h, 0.1% is 0.025 km/h - well below what the widget shows.</summary>
	public const double Step = 0.1;

	public static double Clamp(double percent)
	{
		return double.IsFinite(percent) ? Math.Clamp(percent, 0, MaxPercent) : 0;
	}

	/// <summary>The factor a speed is multiplied by.</summary>
	public static double Factor(double percent)
	{
		return 1 + Clamp(percent) / 100;
	}

	/// <summary>A measured speed as the overlay shows it with `percent`.</summary>
	public static double Corrected(double speed, double percent)
	{
		return speed * Factor(percent);
	}

	/// <summary>Frames that were derived without a correction (a cached summary's), with their speeds raised like TelemetryProcessor.Process does.</summary>
	public static IReadOnlyList<DerivedFrame> Apply(IReadOnlyList<DerivedFrame> frames, double percent)
	{
		double factor = Factor(percent);
		return factor == 1 ? frames : [.. frames.Select(f => f with { SpeedKmh = f.SpeedKmh * factor })];
	}

	/// <summary>The stretch the shown speed is averaged over before CruisingSpeedKmh looks at it - takes out the receiver's jitter.</summary>
	public const double CruisingWindowSeconds = 3;

	private const double CruisingBinKmh = 0.5;

	/// <summary>
	///     The speed a recording was ridden at most of the time when going fast - what to hold against the speedometer or a
	///     limiter. Not a top speed: over a long ride even a few seconds' maximum is the moment the GPS read highest (the
	///     shown speed is the receiver's own per sample, jittering by about half a km/h), so it sets the correction off.
	///     The speed is averaged over CruisingWindowSeconds (never across a gap); of the moving time (TripStats'
	///     threshold), the faster half's most common speed is taken, as the mean of the samples around it. A vehicle at its
	///     limiter rides one plateau, and this finds it. 0 without moving frames.
	/// </summary>
	public static double CruisingSpeedKmh(IReadOnlyList<DerivedFrame> frames)
	{
		List<double> moving = [];
		double sum = 0;
		int start = 0;
		for (int end = 0; end < frames.Count; end++)
		{
			if (frames[end].Raw.StartsAfterGap)
			{
				start = end;
				sum = 0;
			}

			sum += frames[end].SpeedKmh;
			while (start < end && frames[end].Raw.SampleTimeSeconds - frames[start].Raw.SampleTimeSeconds > CruisingWindowSeconds)
				sum -= frames[start++].SpeedKmh;

			double average = sum / (end - start + 1);
			if (average >= TripStats.MovingThresholdKmh) moving.Add(average);
		}

		if (moving.Count == 0) return 0;

		moving.Sort();
		List<double> faster = moving[(moving.Count / 2)..];
		// The fullest bin of the faster half; a tie goes to the faster bin.
		int mode = faster.GroupBy(v => (int)Math.Floor(v / CruisingBinKmh)).MaxBy(g => (g.Count(), g.Key))!.Key;
		double center = (mode + 0.5) * CruisingBinKmh;
		return faster.Where(v => Math.Abs(v - center) <= 1.5 * CruisingBinKmh).Average();
	}

	/// <summary>
	///     The percentage that makes a speed of `shown` (as the overlay showed it, carrying `currentPercent`) come out as
	///     `actual` (the same unit). Rounded down to Step, so the corrected speed never ends up above the real one; within
	///     0..MaxPercent, 0 for a speed that makes no sense.
	/// </summary>
	public static double PercentFor(double actual, double shown, double currentPercent)
	{
		if (!(actual > 0) || !(shown > 0)) return 0;

		double measured = shown / Factor(currentPercent);
		double exact = (actual / measured - 1) * 100;
		// The epsilon keeps an exact result (e.g. 10.0 from 1.1 * x) from flooring a step lower on a floating-point hair.
		return Clamp(Math.Round(Math.Floor(exact / Step + 1e-9) * Step, 1));
	}
}
