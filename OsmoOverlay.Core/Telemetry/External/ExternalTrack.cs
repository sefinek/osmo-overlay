namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>One fix of a track recorded by another device (a bike computer, a watch): UTC time, position, altitude and speed where it has them.</summary>
public sealed record ExternalTrackPoint(DateTime Utc, double Latitude, double Longitude, double? AltitudeMeters = null, double? SpeedMs = null, bool StartsAfterGap = false,
	double? TemperatureCelsius = null, double? DistanceMeters = null);

public sealed record ExternalBatteryReading(DateTime Utc, double Percent);

/// <summary>A stretch of time the other device's timer was stopped, without implying that the device stood still.</summary>
public readonly record struct ExternalTrackPause(DateTime FromUtc, DateTime ToUtc);

/// <summary>A recorded fix, a timer pause or an unexplained gap. Only a fix supplies position and speed.</summary>
public enum ExternalTrackState
{
	Fix,
	Paused,
	Lost
}

/// <summary>Where the track puts the camera at a moment - null Speed when the track has none and its fixes can't tell.</summary>
public readonly record struct ExternalTrackSample(ExternalTrackState State, double Latitude, double Longitude, double? AltitudeMeters, double? SpeedMs,
	double? TemperatureCelsius = null, double? DistanceMeters = null);

/// <summary>
///     A track on its own UTC clock. Gaps and timer pauses preserve elapsed time and do not imply a standstill,
///     even when their endpoints are close. Actual fixes on either boundary remain usable.
/// </summary>
public sealed class ExternalTrack
{
	/// <summary>A gap longer than this many times the fixes' usual interval (and than MinGapSeconds) is a gap.</summary>
	private const double GapIntervals = 3;

	private const double MinGapSeconds = 3;

	public ExternalTrack(IEnumerable<ExternalTrackPoint> points, IEnumerable<ExternalTrackPause>? pauses = null, IEnumerable<ExternalBatteryReading>? batteries = null)
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
		Batteries = [.. (batteries ?? []).Where(b => double.IsFinite(b.Percent) && b.Percent is >= 0 and <= 100).OrderBy(b => b.Utc)];
		double[] intervals = [.. sorted.Zip(sorted.Skip(1), (a, b) => (b.Utc - a.Utc).TotalSeconds).Order()];
		GapSeconds = Math.Max(MinGapSeconds, GapIntervals * intervals[intervals.Length / 2]);
	}

	public IReadOnlyList<ExternalTrackPoint> Points { get; }
	public IReadOnlyList<ExternalTrackPause> Pauses { get; }
	public IReadOnlyList<ExternalBatteryReading> Batteries { get; }

	public double? BatteryAt(DateTime utc)
	{
		if (utc < StartUtc || utc > EndUtc) return null;
		int low = 0, high = Batteries.Count;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (Batteries[middle].Utc <= utc) low = middle + 1;
			else high = middle;
		}
		return low > 0 ? Batteries[low - 1].Percent : null;
	}

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
		if (utc == from.Utc) return FixAt(cursor);
		if (utc == to.Utc) return FixAt(cursor + 1);
		double span = (to.Utc - from.Utc).TotalSeconds;
		bool paused = Pauses.Any(p => p.FromUtc < utc && utc < p.ToUtc);
		if (!Connected(from, to))
			return new ExternalTrackSample(paused ? ExternalTrackState.Paused : ExternalTrackState.Lost, from.Latitude, from.Longitude, from.AltitudeMeters, null);
		double fraction = Math.Clamp((utc - from.Utc).TotalSeconds / span, 0, 1);
		double latitude = from.Latitude + (to.Latitude - from.Latitude) * fraction;
		double longitude = from.Longitude + (to.Longitude - from.Longitude) * fraction;
		double? altitude = from.AltitudeMeters is { } a0 && to.AltitudeMeters is { } a1 ? a0 + (a1 - a0) * fraction : from.AltitudeMeters ?? to.AltitudeMeters;

		double? speed = from.SpeedMs is { } s0 && to.SpeedMs is { } s1 ? s0 + (s1 - s0) * fraction : Displacement(from, to) / span;
		double? temperature = Interpolate(from.TemperatureCelsius, to.TemperatureCelsius, fraction);
		double? distance = to.DistanceMeters >= from.DistanceMeters ? Interpolate(from.DistanceMeters, to.DistanceMeters, fraction) : null;
		return new ExternalTrackSample(ExternalTrackState.Fix, latitude, longitude, altitude, speed, temperature, distance);
	}

	private ExternalTrackSample FixAt(int index)
	{
		ExternalTrackPoint point = Points[index];
		double? speed = point.SpeedMs;
		if (speed is null)
		{
			int other = index + 1 < Points.Count && Connected(point, Points[index + 1]) ? index + 1 : index - 1;
			if (other >= 0)
			{
				ExternalTrackPoint adjacent = Points[other];
				double seconds = Math.Abs((point.Utc - adjacent.Utc).TotalSeconds);
				if (other > index ? Connected(point, adjacent) : Connected(adjacent, point)) speed = Displacement(point, adjacent) / seconds;
			}
		}
		return new ExternalTrackSample(ExternalTrackState.Fix, point.Latitude, point.Longitude, point.AltitudeMeters, speed, point.TemperatureCelsius, point.DistanceMeters);
	}

	private static double? Interpolate(double? from, double? to, double fraction) => from is { } a && to is { } b ? a + (b - a) * fraction : null;

	private bool Connected(ExternalTrackPoint from, ExternalTrackPoint to) => !to.StartsAfterGap &&
		(to.Utc - from.Utc).TotalSeconds <= GapSeconds && !Pauses.Any(p => p.FromUtc < to.Utc && p.ToUtc > from.Utc);

	private static double Displacement(ExternalTrackPoint a, ExternalTrackPoint b)
	{
		return TelemetryMath.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
	}
}
