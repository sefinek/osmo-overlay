namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The djmd stream carries one telemetry record per video frame, but the GPS receiver only
///     reports a new fix a few times a second - most records just repeat the last known position
///     verbatim, so anything reading it directly (Compass trail, Map pan/center) visibly "holds" then
///     jumps at the GPS rate instead of the video's frame rate. This smooths that staircase into a
///     continuous ramp by linearly interpolating between real, distinct fixes. Optional (see
///     OverlaySettings.SmoothGpsMotion) - a cosmetic choice, not a correctness fix.
/// </summary>
public static class GpsInterpolation
{
	// A gap this long between two real fixes is more likely a genuine signal loss (tunnel, garage)
	// than normal GPS cadence - interpolating a straight line across it would draw a route the
	// vehicle probably didn't take. Past this threshold the held frames are left untouched, same as
	// GpsForwardFill's own frozen-position behavior for a dropped fix.
	private const double MaxInterpolationGapSeconds = 3.0;

	// A "held" sample repeats the previous one's Lat/Lon bit-for-bit today (see GpsForwardFill), but
	// exact `==` would make this feature silently do nothing for a camera/firmware that re-serializes
	// an unchanged position with sub-millimeter rounding noise each frame. ~1cm at the equator - well
	// below any real movement, but enough to absorb that noise.
	private const double SamePositionEpsilonDegrees = 1e-7;

	public static List<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames)
	{
		var result = new List<TelemetryFrame>(frames);
		if (result.Count < 2) return result;

		var anchorIndex = 0;
		for (var i = 1; i < result.Count; i++)
		{
			TelemetryFrame current = result[i];
			TelemetryFrame previous = result[i - 1];
			if (current.StartsAfterGap)
			{
				anchorIndex = i;
				continue;
			}

			if (IsSamePosition(current, previous)) continue;

			InterpolateRun(result, anchorIndex, i);
			anchorIndex = i;
		}

		return result;
	}

	private static bool IsSamePosition(TelemetryFrame a, TelemetryFrame b)
	{
		return Math.Abs(a.Latitude - b.Latitude) < SamePositionEpsilonDegrees &&
		       Math.Abs(a.Longitude - b.Longitude) < SamePositionEpsilonDegrees;
	}

	/// <summary>Fills every frame strictly between two real, distinct fixes (`from`/`to`) with a linear ramp - both endpoints themselves are left untouched.</summary>
	private static void InterpolateRun(List<TelemetryFrame> frames, int from, int to)
	{
		if (to - from < 2) return;

		TelemetryFrame start = frames[from];
		TelemetryFrame end = frames[to];

		// (0,0) is this codebase's sentinel for "no fix has ever been seen yet" (GpsForwardFill's
		// initial default, before the very first real fix in a recording - see also
		// DjiMetaTelemetryParser's own (0,0) rejection). Without this guard, the leading run before
		// the first fix would linearly interpolate straight through Null Island - a huge, nonsensical
		// jump - instead of being left untouched like every other never-had-a-fix-yet case.
		if (IsNullIsland(start) || IsNullIsland(end)) return;

		var span = end.SampleTimeSeconds - start.SampleTimeSeconds;
		if (span <= 0 || span > MaxInterpolationGapSeconds) return;

		for (var i = from + 1; i < to; i++)
		{
			TelemetryFrame frame = frames[i];
			var t = (frame.SampleTimeSeconds - start.SampleTimeSeconds) / span;
			frames[i] = frame with
			{
				Latitude = Lerp(start.Latitude, end.Latitude, t),
				Longitude = Lerp(start.Longitude, end.Longitude, t),
				AltitudeMeters = Lerp(start.AltitudeMeters, end.AltitudeMeters, t),
				GpsSpeedMs = start.GpsSpeedMs is { } s0 && end.GpsSpeedMs is { } s1 ? Lerp(s0, s1, t) : frame.GpsSpeedMs
			};
		}
	}

	private static double Lerp(double a, double b, double t)
	{
		return a + (b - a) * t;
	}

	private static bool IsNullIsland(TelemetryFrame frame)
	{
		return frame.Latitude == 0 && frame.Longitude == 0;
	}
}
