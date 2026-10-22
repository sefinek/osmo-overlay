namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>One fix of a track recorded by another device (a bike computer, a watch): UTC time, position, altitude and speed where it has them.</summary>
public sealed record ExternalTrackPoint(DateTime Utc, double Latitude, double Longitude, double? AltitudeMeters = null, double? SpeedMs = null);

/// <summary>A stretch of time the other device's timer was stopped (FIT's timer events), so it wrote no fixes.</summary>
public readonly record struct ExternalTrackPause(DateTime FromUtc, DateTime ToUtc);

/// <summary>What the track says about a moment: a fix, the device standing still on a stop, or a loss of signal.</summary>
public enum ExternalTrackState
{
	Fix,
	Paused,
	Lost
}

/// <summary>Where the track puts the camera at a moment - null Speed when the track has none and its fixes can't tell.</summary>
public readonly record struct ExternalTrackSample(ExternalTrackState State, double Latitude, double Longitude, double? AltitudeMeters, double? SpeedMs);

/// <summary>
///     A track from another device, on its own clock (UTC): its fixes in time order, and where its timer was stopped.
///     Between two fixes further apart than a normal interval (GapSeconds) the device wrote nothing: when its timer was
///     stopped there (a FIT's events) or it had hardly moved (under StandingMetersPerSecond), it stood still - a pause, which
///     a bike computer makes at every red light - and the camera with it; otherwise the signal was lost, and nothing
///     is known of where the camera was.
/// </summary>
public sealed class ExternalTrack
{
	/// <summary>A gap longer than this many times the fixes' usual interval (and than MinGapSeconds) is a gap.</summary>
	private const double GapIntervals = 3;

	private const double MinGapSeconds = 3;

	/// <summary>Over a gap, a device that moved slower than this on average stood still.</summary>
	private const double StandingMetersPerSecond = 1.0;

	public ExternalTrack(IEnumerable<ExternalTrackPoint> points, IEnumerable<ExternalTrackPause>? pauses = null)
	{
		var sorted = new List<ExternalTrackPoint>();
		foreach (ExternalTrackPoint point in points.OrderBy(p => p.Utc))
		{
			if (!IsPosition(point) || (sorted.Count > 0 && point.Utc <= sorted[^1].Utc)) continue;
			sorted.Add(point);
		}

		if (sorted.Count < 2) throw new InvalidDataException("The track has fewer than two usable fixes.");

		Points = sorted;
		Pauses = [.. (pauses ?? []).OrderBy(p => p.FromUtc)];
		double[] intervals = [.. sorted.Zip(sorted.Skip(1), (a, b) => (b.Utc - a.Utc).TotalSeconds).Order()];
		GapSeconds = Math.Max(MinGapSeconds, GapIntervals * intervals[intervals.Length / 2]);
	}

	public IReadOnlyList<ExternalTrackPoint> Points { get; }
	public IReadOnlyList<ExternalTrackPause> Pauses { get; }

	/// <summary>The time between two fixes from which the device is taken to have written nothing.</summary>
	public double GapSeconds { get; }

	public DateTime StartUtc => Points[0].Utc;
	public DateTime EndUtc => Points[^1].Utc;

	private static bool IsPosition(ExternalTrackPoint point)
	{
		return double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) && Math.Abs(point.Latitude) <= 90 && Math.Abs(point.Longitude) <= 180 &&
		       !(point.Latitude == 0 && point.Longitude == 0);
	}

	/// <summary>
	///     The sample at `utc`, or null outside the track (before its first fix or after its last). `cursor` is the index of the
	///     last fix at or before the previous call's time - pass the same variable for times that only go forward, 0 to start (an earlier time starts over).
	/// </summary>
	public ExternalTrackSample? At(DateTime utc, ref int cursor)
	{
		if (utc < StartUtc || utc > EndUtc) return null;

		cursor = Math.Clamp(cursor, 0, Points.Count - 2);
		if (Points[cursor].Utc > utc) cursor = 0;
		while (cursor < Points.Count - 2 && Points[cursor + 1].Utc <= utc) cursor++;

		ExternalTrackPoint from = Points[cursor];
		ExternalTrackPoint to = Points[cursor + 1];
		double span = (to.Utc - from.Utc).TotalSeconds;
		double fraction = Math.Clamp((utc - from.Utc).TotalSeconds / span, 0, 1);
		double latitude = from.Latitude + (to.Latitude - from.Latitude) * fraction;
		double longitude = from.Longitude + (to.Longitude - from.Longitude) * fraction;
		double? altitude = from.AltitudeMeters is { } a0 && to.AltitudeMeters is { } a1 ? a0 + (a1 - a0) * fraction : from.AltitudeMeters ?? to.AltitudeMeters;

		if (span <= GapSeconds)
		{
			double? speed = from.SpeedMs is { } s0 && to.SpeedMs is { } s1 ? s0 + (s1 - s0) * fraction : Displacement(from, to) / span;
			return new ExternalTrackSample(ExternalTrackState.Fix, latitude, longitude, altitude, speed);
		}

		return IsStanding(from, to, span)
			? new ExternalTrackSample(ExternalTrackState.Paused, latitude, longitude, altitude, 0)
			: new ExternalTrackSample(ExternalTrackState.Lost, latitude, longitude, altitude, null);
	}

	private bool IsStanding(ExternalTrackPoint from, ExternalTrackPoint to, double spanSeconds)
	{
		double paused = Pauses.Sum(p => Math.Max(0, (Min(p.ToUtc, to.Utc) - Max(p.FromUtc, from.Utc)).TotalSeconds));
		return paused >= spanSeconds / 2 || Displacement(from, to) / spanSeconds < StandingMetersPerSecond;
	}

	private static double Displacement(ExternalTrackPoint a, ExternalTrackPoint b)
	{
		return TelemetryMath.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
	}

	private static DateTime Min(DateTime a, DateTime b)
	{
		return a < b ? a : b;
	}

	private static DateTime Max(DateTime a, DateTime b)
	{
		return a > b ? a : b;
	}
}
