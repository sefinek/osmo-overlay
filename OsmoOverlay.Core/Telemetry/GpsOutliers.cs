namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     Fixes and receiver speeds no vehicle could have produced - a multipath jump tens of meters off the road for a moment,
///     a single speed sample several times the one around it - repaired before anything is derived from them, so they don't
///     become a speed record, a 45-degree lean (CameraTilt takes the turn they fake out of the tilt) or distance never
///     travelled. A position is plausible when the vehicle could have reached it from the last accepted fix at the speed it
///     had (the receiver's, else the one implied by the fixes) speeding up at MaxAccelerationMs2, give or take PositionNoise;
///     a receiver speed when it's within MaxAccelerationMs2 (plus SpeedNoise) of the last accepted one. Rejected stretches are
///     filled in by time between the accepted samples around them. A rejection lasting ResetSeconds means the earlier track
///     was the wrong one (or the vehicle really went there): the new one is taken as it is, so nothing real is cut out for
///     long. Never across a gap between files (StartsAfterGap). No-fix frames are left as they are.
/// </summary>
internal static class GpsOutliers
{
	// Faster than anything a camera of this kind rides on - a receiver speed above it is a spike whatever came before.
	private const double MaxSpeedMs = 100;
	// About 1.5 g: past a car's hardest braking and a motorcycle's launch.
	private const double MaxAccelerationMs2 = 15;
	// Two fixes' combined error - a consumer receiver's is a few meters each, more under trees and between buildings.
	private const double PositionNoiseMeters = 12;
	// The receiver measures its speed from the Doppler shift - far steadier than its position.
	private const double SpeedNoiseMs = 3;
	private const double ResetSeconds = 3;

	public static IReadOnlyList<TelemetryFrame> Repair(IReadOnlyList<TelemetryFrame> frames)
	{
		(bool[] badPosition, bool[] badSpeed) = Detect(frames);
		if (!badPosition.Contains(true) && !badSpeed.Contains(true)) return frames;

		var repaired = new List<TelemetryFrame>(frames);
		int segmentStart = 0;
		for (int i = 1; i <= frames.Count; i++)
		{
			if (i < frames.Count && !frames[i].StartsAfterGap) continue;

			RepairPositions(repaired, segmentStart, i, badPosition);
			RepairSpeeds(repaired, segmentStart, i, badSpeed);
			segmentStart = i;
		}

		return repaired;
	}

	/// <summary>Which frames' position and which frames' receiver speed are rejected - what Repair fills in.</summary>
	public static (bool[] Position, bool[] Speed) Detect(IReadOnlyList<TelemetryFrame> frames)
	{
		bool[] badPosition = new bool[frames.Count];
		bool[] badSpeed = new bool[frames.Count];
		int segmentStart = 0;
		for (int i = 1; i <= frames.Count; i++)
		{
			if (i < frames.Count && !frames[i].StartsAfterGap) continue;

			FindPositionOutliers(frames, segmentStart, i, badPosition);
			FindSpeedOutliers(frames, segmentStart, i, badSpeed);
			segmentStart = i;
		}

		return (badPosition, badSpeed);
	}

	private static void FindPositionOutliers(IReadOnlyList<TelemetryFrame> frames, int start, int end, bool[] bad)
	{
		int? accepted = null, previousAccepted = null;
		int? runStart = null, runEnd = null;
		for (int i = start; i < end; i++)
		{
			TelemetryFrame frame = frames[i];
			if (!frame.HasGpsFix) continue;

			if (accepted is { } last && !Reachable(frames, last, previousAccepted, i))
			{
				runStart ??= i;
				runEnd = i;
				if (frame.SampleTimeSeconds - frames[runStart.Value].SampleTimeSeconds < ResetSeconds)
				{
					bad[i] = true;
					continue;
				}
			}

			// The run ends: it was the real track if it ran into this fix (the earlier one being wrong, or the vehicle having
			// really gone there - the reach grows with time), a jump if this fix is back where the vehicle was.
			if (runStart is { } first && (frame.SampleTimeSeconds - frames[first].SampleTimeSeconds >= ResetSeconds || Joins(frames, runEnd!.Value, i)))
			{
				for (int j = first; j < i; j++) bad[j] = false;
			}

			runStart = runEnd = null;
			if (accepted is not { } before || !SamePosition(frames[before], frame)) previousAccepted = accepted;
			accepted = i;
		}
	}

	/// <summary>Whether the vehicle could have got from the last accepted fix to frame `i`'s position in the time between them.</summary>
	private static bool Reachable(IReadOnlyList<TelemetryFrame> frames, int last, int? previous, int i)
	{
		TelemetryFrame from = frames[last], to = frames[i];
		double dt = to.SampleTimeSeconds - from.SampleTimeSeconds;
		double meters = TelemetryMath.HaversineMeters(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
		double speed = from.GpsSpeedMs ?? ImpliedSpeed(frames, previous, last) ?? MaxSpeedMs;
		double reach = Math.Min(Math.Min(speed, MaxSpeedMs) + MaxAccelerationMs2 * dt, MaxSpeedMs) * dt;
		return meters <= reach + PositionNoiseMeters;
	}

	/// <summary>Whether the fix at `from` could lead straight on to the one at `to` - at `to`'s speed, or any a vehicle could have.</summary>
	private static bool Joins(IReadOnlyList<TelemetryFrame> frames, int from, int to)
	{
		TelemetryFrame a = frames[from], b = frames[to];
		double dt = b.SampleTimeSeconds - a.SampleTimeSeconds;
		double speed = Math.Min(b.GpsSpeedMs ?? MaxSpeedMs, MaxSpeedMs);
		return TelemetryMath.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude) <= (speed + MaxAccelerationMs2 * dt) * dt + PositionNoiseMeters;
	}

	/// <summary>The speed the last two distinct accepted fixes imply - for a camera that doesn't record the receiver's.</summary>
	private static double? ImpliedSpeed(IReadOnlyList<TelemetryFrame> frames, int? previous, int last)
	{
		if (previous is not { } p) return null;

		double dt = frames[last].SampleTimeSeconds - frames[p].SampleTimeSeconds;
		return dt > 0
			? TelemetryMath.HaversineMeters(frames[p].Latitude, frames[p].Longitude, frames[last].Latitude, frames[last].Longitude) / dt
			: null;
	}

	private static void FindSpeedOutliers(IReadOnlyList<TelemetryFrame> frames, int start, int end, bool[] bad)
	{
		int? accepted = null;
		int? runStart = null;
		for (int i = start; i < end; i++)
		{
			if (frames[i].GpsSpeedMs is not { } speed) continue;

			bool plausible = speed <= MaxSpeedMs && (accepted is not { } last ||
			                                         Math.Abs(speed - frames[last].GpsSpeedMs!.Value) <=
			                                         MaxAccelerationMs2 * (frames[i].SampleTimeSeconds - frames[last].SampleTimeSeconds) + SpeedNoiseMs);
			if (!plausible)
			{
				runStart ??= i;
				if (speed > MaxSpeedMs || frames[i].SampleTimeSeconds - frames[runStart.Value].SampleTimeSeconds < ResetSeconds)
				{
					bad[i] = true;
					continue;
				}

				for (int j = runStart.Value; j < i; j++) bad[j] = false;
			}

			runStart = null;
			accepted = i;
		}
	}

	/// <summary>Each rejected run's position by time between the accepted fixes around it - the last one held at the segment's end.</summary>
	private static void RepairPositions(List<TelemetryFrame> frames, int start, int end, bool[] bad)
	{
		for (int i = start; i < end; i++)
		{
			if (!bad[i]) continue;

			// A fix is only ever rejected against an accepted one before it.
			TelemetryFrame from = frames[Previous(frames, start, i, j => frames[j].HasGpsFix && !bad[j])];
			int after = Next(frames, i, end, j => frames[j].HasGpsFix && !bad[j]);
			TelemetryFrame to = after >= 0 ? frames[after] : from;
			double share = Share(from, frames[i], to);
			frames[i] = frames[i] with
			{
				Latitude = Lerp(from.Latitude, to.Latitude, share),
				Longitude = Lerp(from.Longitude, to.Longitude, share),
				AltitudeMeters = Lerp(from.AltitudeMeters, to.AltitudeMeters, share),
				GpsCorrected = frames[i].GpsCorrected | GpsCorrection.Position
			};
		}
	}

	private static void RepairSpeeds(List<TelemetryFrame> frames, int start, int end, bool[] bad)
	{
		for (int i = start; i < end; i++)
		{
			if (!bad[i]) continue;

			int before = Previous(frames, start, i, j => frames[j].GpsSpeedMs is not null && !bad[j]);
			int after = Next(frames, i, end, j => frames[j].GpsSpeedMs is not null && !bad[j]);
			TelemetryFrame? from = before >= 0 ? frames[before] : null;
			TelemetryFrame? to = after >= 0 ? frames[after] : null;
			double? speed = (from, to) switch
			{
				({ } a, { } b) => Lerp(a.GpsSpeedMs!.Value, b.GpsSpeedMs!.Value, Share(a, frames[i], b)),
				({ } a, null) => a.GpsSpeedMs,
				(null, { } b) => b.GpsSpeedMs,
				_ => null
			};
			frames[i] = frames[i] with { GpsSpeedMs = speed, GpsCorrected = frames[i].GpsCorrected | GpsCorrection.Speed };
		}
	}

	private static int Previous(List<TelemetryFrame> frames, int start, int i, Func<int, bool> good)
	{
		for (int j = i - 1; j >= start; j--)
		{
			if (good(j)) return j;
		}

		return -1;
	}

	private static int Next(List<TelemetryFrame> frames, int i, int end, Func<int, bool> good)
	{
		for (int j = i + 1; j < end; j++)
		{
			if (good(j)) return j;
		}

		return -1;
	}

	private static double Share(TelemetryFrame from, TelemetryFrame at, TelemetryFrame to)
	{
		double span = to.SampleTimeSeconds - from.SampleTimeSeconds;
		return span > 0 ? Math.Clamp((at.SampleTimeSeconds - from.SampleTimeSeconds) / span, 0, 1) : 0;
	}

	private static double Lerp(double a, double b, double share)
	{
		return a + (b - a) * share;
	}

	private static bool SamePosition(TelemetryFrame a, TelemetryFrame b)
	{
		return a.Latitude == b.Latitude && a.Longitude == b.Longitude;
	}
}
