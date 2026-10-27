using System.Globalization;
using System.Text;

namespace OsmoOverlay.Core.Telemetry;

/// <summary>Which way the camera was held for a recording made to check the tilt's signs: roll is positive to the right, pitch positive nose up.</summary>
public enum Lean
{
	Right,
	Left,
	NoseUp,
	NoseDown
}

public readonly record struct ValueRange(double Min, double Median, double Max);

/// <summary>
///     What a recording's telemetry says about itself, for checking a camera or a firmware the app hasn't been verified on:
///     how much of it has a GPS fix, whether the accelerometer reads about 1 g (so its scale is right), and the range the tilt
///     moves in. A recording made leaning the camera one known way (CheckLean) shows whether roll and pitch have the right sign.
/// </summary>
public sealed record TelemetryReport(
	int Frames,
	double DurationSeconds,
	double SampleRateHz,
	double GpsFixShare,
	int GpsLossStretches,
	double LongestGpsLossSeconds,
	bool HasGpsTime,
	ValueRange Gravity,
	bool TiltKnown,
	ValueRange Roll,
	ValueRange Pitch,
	double MaxSpeedKmh)
{
	/// <summary>The accelerometer's magnitude, in g, a camera at rest reads within this much of 1.</summary>
	public const double GravityTolerance = 0.15;

	/// <summary>How far the median tilt has to be from level for CheckLean to call it a lean.</summary>
	public const double LeanThresholdDegrees = 5;

	public static TelemetryReport Create(IReadOnlyList<DerivedFrame> frames, bool tiltKnown)
	{
		if (frames.Count == 0) throw new ArgumentException("No frames.", nameof(frames));

		double duration = frames[^1].Raw.RecordingTimeSeconds - frames[0].Raw.RecordingTimeSeconds;
		List<(double Start, double End)> losses = TelemetryProcessor.FindGpsLossRanges([.. frames.Select(f => f.Raw)]);
		return new TelemetryReport(
			frames.Count,
			duration,
			duration > 0 ? (frames.Count - 1) / duration : 0,
			frames.Count(f => f.Raw.HasGpsFix) / (double)frames.Count,
			losses.Count,
			losses.Count == 0 ? 0 : losses.Max(l => l.End - l.Start),
			frames.Any(f => f.Raw.GpsTimestamp is not null),
			RangeOf(frames.Select(f => f.Raw.GForce)),
			tiltKnown,
			RangeOf(frames.Select(f => f.RollDegrees)),
			RangeOf(frames.Select(f => f.PitchDegrees)),
			frames.Max(f => f.SpeedKmh));
	}

	/// <summary>Whether the accelerometer reads about 1 g at its median - a camera held still can't read otherwise unless its scale is off.</summary>
	public bool GravityLooksRight => Math.Abs(Gravity.Median - 1) <= GravityTolerance;

	/// <summary>True when the median tilt is a clear lean the way `expected` says; false when it's level or the other way (the sign is wrong, or the camera wasn't held so).</summary>
	public bool CheckLean(Lean expected)
	{
		if (!TiltKnown) return false;

		return expected switch
		{
			Lean.Right => Roll.Median >= LeanThresholdDegrees,
			Lean.Left => Roll.Median <= -LeanThresholdDegrees,
			Lean.NoseUp => Pitch.Median >= LeanThresholdDegrees,
			_ => Pitch.Median <= -LeanThresholdDegrees
		};
	}

	public static Lean? ParseLean(string text)
	{
		return text.Replace("-", "").Replace("_", "").ToLowerInvariant() switch
		{
			"right" => Lean.Right,
			"left" => Lean.Left,
			"noseup" or "up" => Lean.NoseUp,
			"nosedown" or "down" => Lean.NoseDown,
			_ => null
		};
	}

	/// <summary>The report as the command line prints it (English, like the rest of the CLI).</summary>
	public string ToText()
	{
		var text = new StringBuilder();
		text.AppendLine(Line("Frames", $"{Frames} over {Num(DurationSeconds, "0.0")} s ({Num(SampleRateHz, "0.0")} per second)"));
		text.AppendLine(Line("GPS fix", $"{Num(GpsFixShare * 100, "0.0")}% of the frames; {GpsLossStretches} stretch(es) without one" +
		                                (GpsLossStretches > 0 ? $", the longest {Num(LongestGpsLossSeconds, "0.0")} s" : "")));
		text.AppendLine(Line("GPS time", HasGpsTime ? "present" : "none"));
		text.AppendLine(Line("Max speed", $"{Num(MaxSpeedKmh, "0.0")} km/h"));
		text.AppendLine(Line("Accelerometer", $"{Num(Gravity.Median, "0.000")} g median ({Num(Gravity.Min, "0.00")} to {Num(Gravity.Max, "0.00")})" +
		                                      (GravityLooksRight ? "" : "  <- far from 1 g: the scale may be wrong")));
		text.AppendLine(Line("Roll", TiltKnown ? Describe(Roll) : "unknown (the camera's axes aren't known)"));
		text.AppendLine(Line("Pitch", TiltKnown ? Describe(Pitch) : "unknown (the camera's axes aren't known)"));
		return text.ToString().TrimEnd();
	}

	private static string Describe(ValueRange range)
	{
		return $"{Num(range.Median, "0.0")} deg median ({Num(range.Min, "0.0")} to {Num(range.Max, "0.0")})";
	}

	private static string Line(string label, string value)
	{
		return $"  {label,-14}{value}";
	}

	private static string Num(double value, string format)
	{
		return value.ToString(format, CultureInfo.InvariantCulture);
	}

	private static ValueRange RangeOf(IEnumerable<double> values)
	{
		double[] sorted = [.. values.Where(double.IsFinite).Order()];
		return sorted.Length == 0 ? default : new ValueRange(sorted[0], sorted[sorted.Length / 2], sorted[^1]);
	}
}
