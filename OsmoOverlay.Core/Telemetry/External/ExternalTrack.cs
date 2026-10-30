namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>One fix of a track recorded by another device (a bike computer, a watch): UTC time, position, altitude and speed where it has them.</summary>
public sealed record ExternalTrackPoint(
	DateTime Utc,
	double Latitude,
	double Longitude,
	double? AltitudeMeters = null,
	double? SpeedMs = null,
	bool StartsAfterGap = false,
	double? TemperatureCelsius = null,
	double? DistanceMeters = null,
	// Height above where the flight began (a drone) - not the altitude, which is above sea level.
	double? HeightMeters = null,
	// Climb rate, up is positive (a drone).
	double? VerticalSpeedMs = null,
	int? Satellites = null,
	int? GpsLevel = null,
	string? FlightMode = null,
	DroneAttitude? Attitude = null);

/// <summary>Where a flight began (a drone's take-off point), for the distance from it.</summary>
public readonly record struct ExternalTrackHome(double Latitude, double Longitude);

public sealed record ExternalBatteryReading(DateTime Utc, double Percent);

/// <summary>A drone's battery at a moment (a flight log) - kept apart from ExternalBatteryReading, which feeds the device battery widget.</summary>
public sealed record ExternalDroneBatteryReading(DateTime Utc, DroneBattery Battery);

/// <summary>A drone's radio links at a moment (a flight log).</summary>
public sealed record ExternalDroneSignalReading(DateTime Utc, DroneSignal Signal);

/// <summary>The remote controller's sticks at a moment (a flight log).</summary>
public sealed record ExternalDroneSticksReading(DateTime Utc, DroneSticks Sticks);

/// <summary>Something that happened at a moment on the track's clock (a flight log's mode changes, warnings, battery levels).</summary>
public sealed record ExternalTrackEvent(DateTime Utc, FlightEvent Event);

/// <summary>A stretch of time the device says its camera was recording, on the track's own clock - a log's way of telling where a recording lies on it.</summary>
public readonly record struct ExternalTrackRecording(DateTime StartUtc, DateTime EndUtc);

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
public readonly record struct ExternalTrackSample(
	ExternalTrackState State,
	double Latitude,
	double Longitude,
	double? AltitudeMeters,
	double? SpeedMs,
	double? TemperatureCelsius = null,
	double? DistanceMeters = null,
	double? HeightMeters = null,
	double? VerticalSpeedMs = null,
	int? Satellites = null,
	int? GpsLevel = null,
	string? FlightMode = null,
	DroneAttitude? Attitude = null);

/// <summary>
///     A track on its own UTC clock. Gaps and timer pauses preserve elapsed time and do not imply a standstill,
///     even when their endpoints are close. Actual fixes on either boundary remain usable.
/// </summary>
public sealed class ExternalTrack
{
	/// <summary>A gap longer than this many times the fixes' usual interval (and than MinGapSeconds) is a gap.</summary>
	private const double GapIntervals = 3;

	private const double MinGapSeconds = 3;

	public ExternalTrack(IEnumerable<ExternalTrackPoint> points, IEnumerable<ExternalTrackPause>? pauses = null, IEnumerable<ExternalBatteryReading>? batteries = null,
		IEnumerable<ExternalTrackRecording>? recordings = null,
		ExternalTrackHome? home = null, IEnumerable<ExternalDroneBatteryReading>? droneBatteries = null, IEnumerable<ExternalTrackEvent>? events = null,
		IEnumerable<ExternalDroneSignalReading>? droneSignals = null, string? deviceName = null, IEnumerable<ExternalDroneSticksReading>? droneSticks = null)
	{
		Home = home;
		DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
		DroneBatteries = [.. (droneBatteries ?? []).Where(b => double.IsFinite(b.Battery.Percent)).OrderBy(b => b.Utc)];
		Events = [.. (events ?? []).OrderBy(e => e.Utc)];
		DroneSignals = [.. (droneSignals ?? []).OrderBy(r => r.Utc)];
		DroneSticks = [.. (droneSticks ?? []).OrderBy(r => r.Utc)];
		var sorted = new List<ExternalTrackPoint>();
		foreach (ExternalTrackPoint point in points.OrderBy(p => p.Utc))
		{
			if (!IsPosition(point) || (sorted.Count > 0 && point.Utc <= sorted[^1].Utc)) continue;
			sorted.Add(point);
		}

		if (sorted.Count < 2) throw new InvalidDataException("The track has fewer than two usable fixes.");

		Points = sorted;
		Pauses = [.. (pauses ?? []).OrderBy(p => p.FromUtc)];
		Recordings = [.. (recordings ?? []).Where(r => r.EndUtc > r.StartUtc).OrderBy(r => r.StartUtc)];
		Batteries = [.. (batteries ?? []).Where(b => double.IsFinite(b.Percent) && b.Percent is >= 0 and <= 100).OrderBy(b => b.Utc)];
		double[] intervals = [.. sorted.Zip(sorted.Skip(1), (a, b) => (b.Utc - a.Utc).TotalSeconds).Order()];
		GapSeconds = Math.Max(MinGapSeconds, GapIntervals * intervals[intervals.Length / 2]);
	}

	public IReadOnlyList<ExternalTrackPoint> Points { get; }
	public IReadOnlyList<ExternalTrackPause> Pauses { get; }
	public IReadOnlyList<ExternalBatteryReading> Batteries { get; }

	/// <summary>Where the flight began - null for a track that has no such point (FIT, GPX).</summary>
	public ExternalTrackHome? Home { get; }

	/// <summary>What the device calls itself - a drone's name as its owner gave it (a flight log); null for a track that doesn't say.</summary>
	public string? DeviceName { get; }

	/// <summary>When the device's camera recorded - empty for a track that doesn't say (FIT, GPX).</summary>
	public IReadOnlyList<ExternalTrackRecording> Recordings { get; }

	/// <summary>The drone's battery readings and the events of its flight - empty for a track that has none (FIT, GPX).</summary>
	public IReadOnlyList<ExternalDroneBatteryReading> DroneBatteries { get; }

	public IReadOnlyList<ExternalTrackEvent> Events { get; }

	public IReadOnlyList<ExternalDroneSignalReading> DroneSignals { get; }

	public IReadOnlyList<ExternalDroneSticksReading> DroneSticks { get; }

	/// <summary>The latest stick reading at or before `utc`, held until the track's end.</summary>
	public DroneSticks? DroneSticksAt(DateTime utc)
	{
		if (utc < StartUtc || utc > EndUtc) return null;

		int low = 0, high = DroneSticks.Count;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (DroneSticks[middle].Utc <= utc) low = middle + 1;
			else high = middle;
		}

		return low > 0 ? DroneSticks[low - 1].Sticks : null;
	}

	/// <summary>The latest radio link reading at or before `utc`, held until the track's end.</summary>
	public DroneSignal? DroneSignalAt(DateTime utc)
	{
		if (utc < StartUtc || utc > EndUtc) return null;

		int low = 0, high = DroneSignals.Count;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (DroneSignals[middle].Utc <= utc) low = middle + 1;
			else high = middle;
		}

		return low > 0 ? DroneSignals[low - 1].Signal : null;
	}

	/// <summary>The latest drone battery reading at or before `utc`, held until the track's end.</summary>
	public DroneBattery? DroneBatteryAt(DateTime utc)
	{
		if (utc < StartUtc || utc > EndUtc) return null;

		int low = 0, high = DroneBatteries.Count;
		while (low < high)
		{
			int middle = low + (high - low) / 2;
			if (DroneBatteries[middle].Utc <= utc) low = middle + 1;
			else high = middle;
		}

		return low > 0 ? DroneBatteries[low - 1].Battery : null;
	}

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
		double? height = Interpolate(from.HeightMeters, to.HeightMeters, fraction);
		return new ExternalTrackSample(ExternalTrackState.Fix, latitude, longitude, altitude, speed, temperature, distance, height,
			Interpolate(from.VerticalSpeedMs, to.VerticalSpeedMs, fraction), from.Satellites, from.GpsLevel, from.FlightMode, from.Attitude);
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

		return new ExternalTrackSample(ExternalTrackState.Fix, point.Latitude, point.Longitude, point.AltitudeMeters, speed, point.TemperatureCelsius, point.DistanceMeters, point.HeightMeters,
			point.VerticalSpeedMs, point.Satellites, point.GpsLevel, point.FlightMode, point.Attitude);
	}

	private static double? Interpolate(double? from, double? to, double fraction) => from is { } a && to is { } b ? a + (b - a) * fraction : null;

	private bool Connected(ExternalTrackPoint from, ExternalTrackPoint to) => !to.StartsAfterGap &&
	                                                                          (to.Utc - from.Utc).TotalSeconds <= GapSeconds && !Pauses.Any(p => p.FromUtc < to.Utc && p.ToUtc > from.Utc);

	private static double Displacement(ExternalTrackPoint a, ExternalTrackPoint b)
	{
		return TelemetryMath.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
	}
}
