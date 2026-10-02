namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The one number that makes up for a GPS reading low: a percentage added to every speed (OverlaySettings.SpeedCorrectionPercent,
///     TelemetryProcessor.Process). A scale rather than a fixed amount - a receiver's error grows with speed, and standing still must stay 0.
/// </summary>
public static class SpeedCalibration
{
	/// <summary>The most a speed can be raised by - a calibration, not a way to invent speed.</summary>
	public const double MaxPercent = 50;

	private const double Step = 0.5;

	public static double Clamp(double percent)
	{
		return double.IsFinite(percent) ? Math.Clamp(percent, 0, MaxPercent) : 0;
	}

	/// <summary>The factor a speed is multiplied by.</summary>
	public static double Factor(double percent)
	{
		return 1 + Clamp(percent) / 100;
	}

	/// <summary>Frames that were derived without a correction (a cached summary's), with their speeds raised like TelemetryProcessor.Process does.</summary>
	public static IReadOnlyList<DerivedFrame> Apply(IReadOnlyList<DerivedFrame> frames, double percent)
	{
		double factor = Factor(percent);
		return factor == 1 ? frames : [.. frames.Select(f => f with { SpeedKmh = f.SpeedKmh * factor })];
	}

	/// <summary>
	///     The percentage that turns what the overlay `shown` into `actual` (the same unit). `shown` already carries
	///     `currentPercent`, so the receiver's own reading is worked out from it first. Rounded to half a percent, within
	///     0..MaxPercent; 0 for a speed that makes no sense.
	/// </summary>
	public static double PercentFor(double actual, double shown, double currentPercent)
	{
		if (!(actual > 0) || !(shown > 0)) return 0;

		double measured = shown / Factor(currentPercent);
		return Clamp(Math.Round((actual / measured - 1) * 100 / Step) * Step);
	}
}
