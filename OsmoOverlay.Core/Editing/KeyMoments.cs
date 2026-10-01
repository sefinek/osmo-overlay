using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

/// <summary>A point of the recording the user marked: a frame of the combined recording and what to call it.</summary>
public sealed record KeyMoment(long Frame, string Name);

public enum PeakKind
{
	TopSpeed,
	HighestPoint,
	LowestPoint,
	StrongestG,
	SteepestClimb,
	SteepestDescent,
	MaxLean
}

/// <summary>Where a peak of the telemetry is: its frame and the value there, in km/h, meters, G, percent or degrees.</summary>
public readonly record struct Peak(long Frame, double Value);

/// <summary>Finds the recording's extremes for the key moments panel's jump buttons.</summary>
public static class KeyMoments
{
	// Below these a recording just stood still or had no such data (no GPS, unknown accelerometer axes) - there's no peak to show.
	private const double MinSpeedKmh = 1;
	private const double MinGradientPercent = 1;
	private const double MinLeanDegrees = 1;

	public static string Label(PeakKind kind)
	{
		return kind switch
		{
			PeakKind.TopSpeed => "Top speed",
			PeakKind.HighestPoint => "Highest point",
			PeakKind.LowestPoint => "Lowest point",
			PeakKind.StrongestG => "Strongest G-force",
			PeakKind.SteepestClimb => "Steepest climb",
			PeakKind.SteepestDescent => "Steepest descent",
			PeakKind.MaxLean => "Deepest lean",
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	/// <summary>The first frame where `kind` peaks, or null when the data has none (no GPS fix, a standstill, unknown axes).</summary>
	public static Peak? Find(IReadOnlyList<DerivedFrame> frames, PeakKind kind, double fps, long totalFrames)
	{
		if (frames.Count == 0 || fps <= 0 || totalFrames <= 0) return null;

		bool gpsKind = kind is not (PeakKind.StrongestG or PeakKind.MaxLean);
		DerivedFrame? best = null;
		double bestValue = double.NegativeInfinity;
		foreach (DerivedFrame frame in frames)
		{
			if (gpsKind && !frame.Raw.HasGpsFix) continue;

			double value = ValueOf(frame, kind);
			if (double.IsNaN(value) || value <= bestValue) continue;

			best = frame;
			bestValue = value;
		}

		if (best is null) return null;

		double shown = kind switch
		{
			PeakKind.LowestPoint or PeakKind.SteepestDescent => -bestValue,
			_ => bestValue
		};
		bool meaningful = kind switch
		{
			PeakKind.TopSpeed => bestValue >= MinSpeedKmh,
			PeakKind.SteepestClimb or PeakKind.SteepestDescent => bestValue >= MinGradientPercent,
			PeakKind.MaxLean => bestValue >= MinLeanDegrees,
			PeakKind.StrongestG => bestValue > 0,
			_ => true
		};
		if (!meaningful) return null;

		long frameIndex = Math.Clamp((long)Math.Round(best.Raw.RecordingTimeSeconds * fps), 0, totalFrames - 1);
		return new Peak(frameIndex, shown);
	}

	/// <summary>The value to maximize - lows and descents are negated.</summary>
	private static double ValueOf(DerivedFrame frame, PeakKind kind)
	{
		return kind switch
		{
			PeakKind.TopSpeed => frame.SpeedKmh,
			PeakKind.HighestPoint => frame.Raw.AltitudeMeters,
			PeakKind.LowestPoint => -frame.Raw.AltitudeMeters,
			PeakKind.StrongestG => frame.SmoothedGForce,
			PeakKind.SteepestClimb => frame.GradientPercent,
			PeakKind.SteepestDescent => -frame.GradientPercent,
			PeakKind.MaxLean => Math.Abs(frame.RollDegrees),
			_ => double.NaN
		};
	}

	/// <summary>Adds a moment, replacing one on the same frame; the list stays in time order.</summary>
	public static List<KeyMoment> Add(IEnumerable<KeyMoment> moments, KeyMoment moment, long totalFrames)
	{
		if (moment.Frame < 0 || moment.Frame >= totalFrames) return [.. moments];

		return [.. moments.Where(m => m.Frame != moment.Frame).Append(moment).OrderBy(m => m.Frame)];
	}

	/// <summary>Moments inside the recording, one per frame, in time order - what a loaded project is cleaned up to.</summary>
	public static List<KeyMoment> Normalize(IEnumerable<KeyMoment>? moments, long totalFrames)
	{
		if (moments is null) return [];

		return [.. moments.Where(m => m is not null && m.Frame >= 0 && m.Frame < totalFrames)
			.GroupBy(m => m.Frame).Select(g => g.Last()).OrderBy(m => m.Frame)];
	}
}
