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

	/// <summary>How long a speed has to be held to count as a recording's top speed for calibration (SustainedTopSpeedKmh).</summary>
	public const double SustainedSeconds = 3;

	/// <summary>
	///     The highest speed held for SustainedSeconds: the best average over any such stretch, never across a gap. The shown
	///     speed is the receiver's own per sample, which jitters by around half a km/h - its single highest sample is a spike
	///     more than a top speed, and calibrating on it sets the correction off by that much. A vehicle at its limiter holds
	///     its top speed, so the sustained one is what to compare with the real figure. 0 without frames; a recording
	///     shorter than the stretch gives its overall average.
	/// </summary>
	public static double SustainedTopSpeedKmh(IReadOnlyList<DerivedFrame> frames, double seconds = SustainedSeconds)
	{
		double best = 0, sum = 0;
		int start = 0;
		bool full = false;
		for (int end = 0; end < frames.Count; end++)
		{
			if (frames[end].Raw.StartsAfterGap)
			{
				start = end;
				sum = 0;
			}

			sum += frames[end].SpeedKmh;
			while (start < end && frames[end].Raw.SampleTimeSeconds - frames[start + 1].Raw.SampleTimeSeconds >= seconds)
				sum -= frames[start++].SpeedKmh;

			double average = sum / (end - start + 1);
			if (frames[end].Raw.SampleTimeSeconds - frames[start].Raw.SampleTimeSeconds >= seconds)
			{
				best = full ? Math.Max(best, average) : average;
				full = true;
			}
			else if (!full) best = Math.Max(best, average);
		}

		return best;
	}

	/// <summary>
	///     The percentage that makes a top speed of `shown` (as the overlay showed it, carrying `currentPercent`) come out as
	///     `actual` (the same unit). Rounded down to Step, so the corrected top speed never ends up above the real one; within
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
