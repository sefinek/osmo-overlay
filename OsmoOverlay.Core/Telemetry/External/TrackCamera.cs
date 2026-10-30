using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     Where the map behind a track looks and how wide: on the position, closer while it moves slowly and wider as it speeds up
///     (relative to the track's own pace, so a walk, a ride and a flight each use the whole range). Only a function of the time
///     asked for - the average of a few seconds around it - so the picture of a frame never depends on the frames drawn before.
/// </summary>
public static class TrackCamera
{
	/// <summary>How many times wider than its closest the picture gets.</summary>
	public const double MaxZoomOut = 4;

	private const double PositionHalfWindowSeconds = 1.5;
	private const double SpeedHalfWindowSeconds = 5;
	// A track that hardly moves (a hover) has no pace to measure against.
	private const double MinReferenceKmh = 5;

	public readonly record struct Shot(double Latitude, double Longitude, double Spread);

	/// <summary>The speed the picture is widest at: what the track keeps to for most of it (its 90th percentile).</summary>
	public static double ReferenceKmh(IReadOnlyList<DerivedFrame> frames)
	{
		if (frames.Count == 0) return MinReferenceKmh;

		double[] speeds = [.. frames.Select(f => f.SpeedKmh).Where(double.IsFinite).Order()];
		return speeds.Length == 0 ? MinReferenceKmh : Math.Max(MinReferenceKmh, speeds[(int)((speeds.Length - 1) * 0.9)]);
	}

	/// <summary>
	///     Null when the track has no fix at all. The frames are 10 Hz and the picture 30 fps, so the position is carried between the
	///     two windows around the time instead of jumping to the nearest frame's.
	/// </summary>
	public static Shot? At(IReadOnlyList<DerivedFrame> frames, double seconds, double referenceKmh)
	{
		if (frames.Count == 0) return null;

		(int index, double fraction, double step) = Locate(frames, seconds);
		int positionReach = (int)Math.Ceiling(PositionHalfWindowSeconds / step);
		if (AveragePosition(frames, index, positionReach) is not { } position) return null;

		(double latitude, double longitude) = position;
		if (fraction > 0 && AveragePosition(frames, index + 1, positionReach) is { } next)
		{
			latitude += (next.Latitude - latitude) * fraction;
			longitude += (next.Longitude - longitude) * fraction;
		}

		int center = fraction < 0.5 ? index : index + 1;
		int speedReach = (int)Math.Ceiling(SpeedHalfWindowSeconds / step);
		int from = Math.Max(0, center - speedReach), to = Math.Min(frames.Count - 1, center + speedReach);
		double speed = 0;
		for (int i = from; i <= to; i++) speed += double.IsFinite(frames[i].SpeedKmh) ? frames[i].SpeedKmh : 0;
		double ratio = Math.Clamp(speed / (to - from + 1) / Math.Max(referenceKmh, MinReferenceKmh), 0, 1);
		return new Shot(latitude, longitude, ratio * ratio * (3 - 2 * ratio));
	}

	/// <summary>
	///     The frame at a time with its position carried between the two frames around it (not across a gap), or null with no fix
	///     there - what the marker is drawn at, so it moves as smoothly as the view does.
	/// </summary>
	public static TelemetryFrame? FixAt(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		if (frames.Count == 0) return null;

		(int index, double fraction, _) = Locate(frames, seconds);
		TelemetryFrame before = frames[index].Raw;
		if (index + 1 >= frames.Count) return before.HasGpsFix ? before : null;

		TelemetryFrame after = frames[index + 1].Raw;
		TelemetryFrame nearest = fraction < 0.5 ? before : after;
		if (!nearest.HasGpsFix) return null;
		if (!before.HasGpsFix || !after.HasGpsFix || after.StartsAfterGap) return nearest;

		return nearest with
		{
			Latitude = before.Latitude + (after.Latitude - before.Latitude) * fraction,
			Longitude = before.Longitude + (after.Longitude - before.Longitude) * fraction,
			DroneAttitude = before.DroneAttitude is { } a && after.DroneAttitude is { } b
				? new DroneAttitude(a.PitchDegrees + (b.PitchDegrees - a.PitchDegrees) * fraction, a.RollDegrees + (b.RollDegrees - a.RollDegrees) * fraction,
					AngleMath.LerpDegrees(a.YawDegrees, b.YawDegrees, fraction))
				: nearest.DroneAttitude
		};
	}

	/// <summary>The frames are evenly spaced: the one at or before a time, how far past it the time is (0 to 1) and the spacing.</summary>
	internal static (int Index, double Fraction, double Step) Locate(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		int count = frames.Count;
		double first = frames[0].Raw.SampleTimeSeconds;
		double step = count > 1 ? (frames[^1].Raw.SampleTimeSeconds - first) / (count - 1) : 1;
		if (step <= 0) step = 1;

		double at = Math.Clamp((seconds - first) / step, 0, count - 1);
		int index = Math.Min((int)at, count - 1);
		return (index, at - index, step);
	}

	/// <summary>The mean of the fixes within a reach of a frame, or the nearest fix when there is none in it.</summary>
	private static (double Latitude, double Longitude)? AveragePosition(IReadOnlyList<DerivedFrame> frames, int center, int reach)
	{
		center = Math.Min(center, frames.Count - 1);
		(double latitude, double longitude, int fixes) = (0, 0, 0);
		for (int i = Math.Max(0, center - reach); i <= Math.Min(frames.Count - 1, center + reach); i++)
		{
			if (!frames[i].Raw.HasGpsFix) continue;
			latitude += frames[i].Raw.Latitude;
			longitude += frames[i].Raw.Longitude;
			fixes++;
		}

		if (fixes > 0) return (latitude / fixes, longitude / fixes);
		return NearestFix(frames, center) is { } nearest ? (nearest.Latitude, nearest.Longitude) : null;
	}

	private static TelemetryFrame? NearestFix(IReadOnlyList<DerivedFrame> frames, int index)
	{
		for (int distance = 1; distance < frames.Count; distance++)
		{
			if (index - distance >= 0 && frames[index - distance].Raw.HasGpsFix) return frames[index - distance].Raw;
			if (index + distance < frames.Count && frames[index + distance].Raw.HasGpsFix) return frames[index + distance].Raw;
		}

		return null;
	}
}
