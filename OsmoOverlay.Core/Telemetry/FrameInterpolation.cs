namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     A frame between two of a sparse telemetry (a GPS track's 10 Hz against a picture of 50 fps), so a widget moves with the
///     picture instead of in steps. Used only where frames are sparser than the picture (a camera's are one per video frame, and
///     a render of one reads them as they are). A function of the time asked for and the frames, never of what was drawn before.
/// </summary>
public static class FrameInterpolation
{
	/// <summary>The longest stretch between two frames that is still carried across; a longer one is a gap in the data.</summary>
	private const double MaxSpanSeconds = 0.5;

	public static DerivedFrame At(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		int next = TelemetryProcessor.FindIndex(frames, seconds);
		if (next == 0) return frames[0];

		DerivedFrame after = frames[next], before = frames[next - 1];
		double span = after.Raw.SampleTimeSeconds - before.Raw.SampleTimeSeconds;
		if (span <= 0 || span > MaxSpanSeconds || after.Raw.StartsAfterGap || after.StartsAfterCut) return frames[next];

		double fraction = Math.Clamp((seconds - before.Raw.SampleTimeSeconds) / span, 0, 1);
		if (fraction >= 1) return after;

		// What a person moves by hand or the aircraft banks by (sticks, attitude, the speed) goes through the frames around the two as a curve,
		// not a straight line: a line changes direction at every 10 Hz frame, which shows as a stutter at the pace of the log.
		DerivedFrame? earlier = next >= 2 && Continues(frames[next - 2], before) ? frames[next - 2] : null;
		DerivedFrame? later = next + 1 < frames.Count && Continues(after, frames[next + 1]) ? frames[next + 1] : null;

		// Whatever isn't a number that runs on (a flag, an event, a name) is the nearest frame's.
		DerivedFrame nearest = fraction < 0.5 ? before : after;
		bool fixes = before.Raw.HasGpsFix && after.Raw.HasGpsFix;
		TelemetryFrame raw = nearest.Raw with
		{
			SampleTimeSeconds = seconds,
			Latitude = fixes ? Lerp(before.Raw.Latitude, after.Raw.Latitude, fraction) : nearest.Raw.Latitude,
			Longitude = fixes ? Lerp(before.Raw.Longitude, after.Raw.Longitude, fraction) : nearest.Raw.Longitude,
			AltitudeMeters = Lerp(before.Raw.AltitudeMeters, after.Raw.AltitudeMeters, fraction),
			GpsTimestamp = before.Raw.GpsTimestamp is { } t0 && after.Raw.GpsTimestamp is { } t1
				? t0 + TimeSpan.FromTicks((long)((t1 - t0).Ticks * fraction))
				: nearest.Raw.GpsTimestamp,
			GpsSpeedMs = Lerp(before.Raw.GpsSpeedMs, after.Raw.GpsSpeedMs, fraction),
			HeightAboveHomeMeters = Lerp(before.Raw.HeightAboveHomeMeters, after.Raw.HeightAboveHomeMeters, fraction),
			DistanceFromHomeMeters = Lerp(before.Raw.DistanceFromHomeMeters, after.Raw.DistanceFromHomeMeters, fraction),
			VerticalSpeedMs = Lerp(before.Raw.VerticalSpeedMs, after.Raw.VerticalSpeedMs, fraction),
			DroneSticks = before.Raw.DroneSticks is { } s0 && after.Raw.DroneSticks is { } s1
				? new DroneSticks(
					Stick(earlier?.Raw.DroneSticks?.Aileron, s0.Aileron, s1.Aileron, later?.Raw.DroneSticks?.Aileron, fraction),
					Stick(earlier?.Raw.DroneSticks?.Elevator, s0.Elevator, s1.Elevator, later?.Raw.DroneSticks?.Elevator, fraction),
					Stick(earlier?.Raw.DroneSticks?.Throttle, s0.Throttle, s1.Throttle, later?.Raw.DroneSticks?.Throttle, fraction),
					Stick(earlier?.Raw.DroneSticks?.Rudder, s0.Rudder, s1.Rudder, later?.Raw.DroneSticks?.Rudder, fraction))
				: nearest.Raw.DroneSticks,
			DroneAttitude = before.Raw.DroneAttitude is { } a0 && after.Raw.DroneAttitude is { } a1
				? new DroneAttitude(
					Curve(earlier?.Raw.DroneAttitude?.PitchDegrees, a0.PitchDegrees, a1.PitchDegrees, later?.Raw.DroneAttitude?.PitchDegrees, fraction),
					Curve(earlier?.Raw.DroneAttitude?.RollDegrees, a0.RollDegrees, a1.RollDegrees, later?.Raw.DroneAttitude?.RollDegrees, fraction),
					AngleMath.NormalizeDegrees(a0.YawDegrees + Curve(Unwrapped(earlier?.Raw.DroneAttitude?.YawDegrees, a0.YawDegrees), 0,
						Unwrapped(a1.YawDegrees, a0.YawDegrees) ?? 0, Unwrapped(later?.Raw.DroneAttitude?.YawDegrees, a0.YawDegrees), fraction)))
				: nearest.Raw.DroneAttitude
		};

		return nearest with
		{
			Raw = raw,
			SpeedKmh = Math.Max(0, Curve(earlier?.SpeedKmh, before.SpeedKmh, after.SpeedKmh, later?.SpeedKmh, fraction)),
			HeadingDegrees = AngleMath.LerpDegrees(before.HeadingDegrees, after.HeadingDegrees, fraction),
			GradientPercent = Lerp(before.GradientPercent, after.GradientPercent, fraction),
			CumulativeDistanceMeters = Lerp(before.CumulativeDistanceMeters, after.CumulativeDistanceMeters, fraction),
			RollDegrees = Lerp(before.RollDegrees, after.RollDegrees, fraction),
			PitchDegrees = Lerp(before.PitchDegrees, after.PitchDegrees, fraction),
			LocalEastMeters = Lerp(before.LocalEastMeters, after.LocalEastMeters, fraction),
			LocalNorthMeters = Lerp(before.LocalNorthMeters, after.LocalNorthMeters, fraction),
			SmoothedGForce = Lerp(before.SmoothedGForce, after.SmoothedGForce, fraction),
			LateralAccelG = Lerp(before.LateralAccelG, after.LateralAccelG, fraction),
			LongitudinalAccelG = Lerp(before.LongitudinalAccelG, after.LongitudinalAccelG, fraction),
			StableHeadingDegrees = before.StableHeadingDegrees is { } h0 && after.StableHeadingDegrees is { } h1
				? AngleMath.LerpDegrees(h0, h1, fraction)
				: nearest.StableHeadingDegrees
		};
	}

	/// <summary>Whether `to` follows `from` on the same stretch - no gap or cut between them and no longer a span than one carried across.</summary>
	private static bool Continues(DerivedFrame from, DerivedFrame to)
	{
		double span = to.Raw.SampleTimeSeconds - from.Raw.SampleTimeSeconds;
		return span > 0 && span <= MaxSpanSeconds && !to.Raw.StartsAfterGap && !to.StartsAfterCut;
	}

	private static double Stick(double? earlier, double before, double after, double? later, double fraction)
	{
		return Math.Clamp(Curve(earlier, before, after, later, fraction), -1, 1);
	}

	/// <summary>An angle as degrees past `origin` along the short way (so a curve through it doesn't cross 360 to 0), null stays null.</summary>
	private static double? Unwrapped(double? degrees, double origin)
	{
		return degrees is { } value ? Math.IEEERemainder(value - origin, 360) : null;
	}

	/// <summary>
	///     The curve through four values evenly spaced in time (a Catmull-Rom spline) between the middle two: it passes through them
	///     and its slope is carried over from one frame to the next. Where there is no neighbour the line is simply carried on.
	/// </summary>
	private static double Curve(double? earlier, double before, double after, double? later, double fraction)
	{
		double p0 = earlier ?? 2 * before - after, p3 = later ?? 2 * after - before;
		double f2 = fraction * fraction, f3 = f2 * fraction;
		return 0.5 * (2 * before + (after - p0) * fraction + (2 * p0 - 5 * before + 4 * after - p3) * f2 + (3 * before - p0 - 3 * after + p3) * f3);
	}

	private static double Lerp(double a, double b, double fraction)
	{
		return a + (b - a) * fraction;
	}

	private static double? Lerp(double? a, double? b, double fraction)
	{
		return a is { } x && b is { } y ? x + (y - x) * fraction : fraction < 0.5 ? a : b;
	}
}
