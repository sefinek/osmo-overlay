using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;
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
	// The same stretch the gradient widget measures a slope over (TelemetryProcessor.GradientWindowMeters).
	private const double SlopeWindowMeters = TelemetryProcessor.GradientWindowMeters;
	// The peaks of one category listed together are at least this far apart in time.
	private const double MinPeakSeparationSeconds = 10;

	public static string Label(PeakKind kind)
	{
		return kind switch
		{
			PeakKind.TopSpeed => CoreStrings.Peak_TopSpeed,
			PeakKind.HighestPoint => CoreStrings.Peak_HighestPoint,
			PeakKind.LowestPoint => CoreStrings.Peak_LowestPoint,
			PeakKind.StrongestG => CoreStrings.Peak_StrongestG,
			PeakKind.SteepestClimb => CoreStrings.Peak_SteepestClimb,
			PeakKind.SteepestDescent => CoreStrings.Peak_SteepestDescent,
			PeakKind.MaxLean => CoreStrings.Peak_MaxLean,
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	/// <summary>The single highest peak of `kind`, or null when the data has none (no GPS fix, a standstill, unknown axes).</summary>
	public static Peak? Find(IReadOnlyList<DerivedFrame> frames, PeakKind kind, double fps, long totalFrames, bool movingOnly = false)
	{
		List<Peak> top = FindTop(frames, kind, fps, totalFrames, 1, movingOnly);
		return top.Count > 0 ? top[0] : null;
	}

	/// <summary>
	///     The `count` highest peaks of `kind`, best first, at least MinPeakSeparationSeconds apart - the neighbouring frames of
	///     one peak are all nearly as high and would otherwise fill the list. Fewer (or none) when the data has no more such peaks.
	///     `movingOnly` leaves out the G-force and lean of frames the ride wasn't under way (TripStats.Riding): stopping,
	///     starting or standing they're the camera being handled, not the ride (the other kinds hold either way - an altitude
	///     or a slope stood on is still one).
	/// </summary>
	public static List<Peak> FindTop(IReadOnlyList<DerivedFrame> frames, PeakKind kind, double fps, long totalFrames, int count,
		bool movingOnly = false)
	{
		if (frames.Count == 0 || fps <= 0 || totalFrames <= 0 || count <= 0) return [];

		bool gpsKind = kind is not (PeakKind.StrongestG or PeakKind.MaxLean);
		bool[]? riding = movingOnly && !gpsKind ? TripStats.Riding(frames) : null;
		// The G-meter's own reading (dynamic acceleration against its baseline), so the peak is the number the widget shows there.
		(double Lateral, double Longitudinal)[]? deltas = kind == PeakKind.StrongestG ? GMeterDeltas.Compute(frames) : null;
		double[]? slopes = kind is PeakKind.SteepestClimb or PeakKind.SteepestDescent
			? Slopes(frames, kind == PeakKind.SteepestClimb ? 1 : -1)
			: null;

		List<(int Index, double Value)> candidates = [];
		for (int i = 0; i < frames.Count; i++)
		{
			DerivedFrame frame = frames[i];
			if (gpsKind && !frame.Raw.HasGpsFix) continue;
			if (riding is not null && !riding[i]) continue;

			double value = slopes is not null
				? slopes[i]
				: deltas is null
					? ValueOf(frame, kind)
					: Math.Sqrt(deltas[i].Lateral * deltas[i].Lateral + deltas[i].Longitudinal * deltas[i].Longitudinal);
			if (!double.IsNaN(value) && IsMeaningful(kind, value)) candidates.Add((i, value));
		}

		List<Peak> peaks = [];
		List<double> times = [];
		foreach ((int index, double value) in candidates.OrderByDescending(c => c.Value))
		{
			double time = frames[index].Raw.RecordingTimeSeconds;
			if (times.Any(t => Math.Abs(t - time) < MinPeakSeparationSeconds)) continue;

			times.Add(time);
			double shown = kind is PeakKind.LowestPoint or PeakKind.SteepestDescent ? -value : value;
			peaks.Add(new Peak(Math.Clamp((long)Math.Round(time * fps), 0, totalFrames - 1), shown));
			if (peaks.Count == count) break;
		}

		return peaks;
	}

	/// <summary>Below these a recording just stood still or had no such data - there's no peak to show.</summary>
	private static bool IsMeaningful(PeakKind kind, double value)
	{
		return kind switch
		{
			PeakKind.TopSpeed => value >= MinSpeedKmh,
			PeakKind.SteepestClimb or PeakKind.SteepestDescent => value >= MinGradientPercent,
			PeakKind.MaxLean => value >= MinLeanDegrees,
			PeakKind.StrongestG => value > 0,
			_ => true
		};
	}

	/// <summary>
	///     The slope (percent, times `sign`) over the last SlopeWindowMeters travelled, put on the frame in the middle of that
	///     stretch; NaN everywhere else. Stretches don't run across a cut or a gap between files, nor over frames without a fix.
	/// </summary>
	private static double[] Slopes(IReadOnlyList<DerivedFrame> frames, int sign)
	{
		double[] slopes = new double[frames.Count];
		Array.Fill(slopes, double.NaN);

		int start = 0;
		int from = 0;
		for (int i = 0; i < frames.Count; i++)
		{
			if (frames[i].StartsAfterCut) start = from = i;
			if (!frames[i].Raw.HasGpsFix)
			{
				start = from = i + 1;
				continue;
			}

			// The newest start that still spans the window - the shortest stretch of at least SlopeWindowMeters.
			while (from + 1 < i && frames[i].CumulativeDistanceMeters - frames[from + 1].CumulativeDistanceMeters >= SlopeWindowMeters) from++;
			if (from < start || from >= i) continue;

			double distance = frames[i].CumulativeDistanceMeters - frames[from].CumulativeDistanceMeters;
			if (distance < SlopeWindowMeters) continue;

			double slope = (frames[i].Raw.AltitudeMeters - frames[from].Raw.AltitudeMeters) / distance * 100 * sign;
			int middle = (from + i) / 2;
			if (double.IsNaN(slopes[middle]) || slope > slopes[middle]) slopes[middle] = slope;
		}

		return slopes;
	}

	/// <summary>The value to maximize - lows and descents are negated.</summary>
	private static double ValueOf(DerivedFrame frame, PeakKind kind)
	{
		return kind switch
		{
			PeakKind.TopSpeed => frame.SpeedKmh,
			PeakKind.HighestPoint => frame.Raw.AltitudeMeters,
			PeakKind.LowestPoint => -frame.Raw.AltitudeMeters,
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

		return
		[
			.. moments.Where(m => m is not null && m.Frame >= 0 && m.Frame < totalFrames)
				.GroupBy(m => m.Frame).Select(g => g.Last()).OrderBy(m => m.Frame)
		];
	}
}
