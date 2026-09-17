namespace OsmoOverlay.Core.Telemetry;

public sealed record TelemetrySummary(
	int SampleCount,
	double DurationSeconds,
	double TotalDistanceMeters,
	double MaxSpeedKmh,
	double MinAltitudeMeters,
	double MaxAltitudeMeters,
	double MaxGForce,
	DateTime? RecordedAtUtc);

public static class TelemetryProcessor
{
	// Reference window for speed/heading/gradient, and the smoothing time constants for the
	// on-screen pitch/G-force readouts. All expressed in seconds (not sample count) so behavior
	// stays consistent regardless of the camera's telemetry sampling rate.
	private const double SpeedWindowSeconds = 1.0;
	private const double PitchTimeConstantSeconds = 0.3;
	private const double GForceTimeConstantSeconds = 0.3;

	public static List<DerivedFrame> Process(IReadOnlyList<TelemetryFrame> frames, bool smoothGps = false)
	{
		if (smoothGps) frames = GpsInterpolation.Apply(frames);

		var result = new List<DerivedFrame>(frames.Count);
		double cumulativeDistance = 0;
		var lastDistanceIndex = 0;
		var refIndex = 0;

		var originLat = frames[0].Latitude;
		var originLon = frames[0].Longitude;
		var metersPerDegLat = 111_320.0;
		var metersPerDegLon = 111_320.0 * Math.Cos(originLat * Math.PI / 180.0);

		var smoothedPitch = 0.0;
		var smoothedGForce = 0.0;

		for (var i = 0; i < frames.Count; i++)
		{
			TelemetryFrame current = frames[i];

			while (refIndex < i && current.SampleTimeSeconds - frames[refIndex].SampleTimeSeconds > SpeedWindowSeconds)
				refIndex++;
			TelemetryFrame reference = frames[refIndex];

			var dt = current.SampleTimeSeconds - reference.SampleTimeSeconds;
			var horizontalMeters = TelemetryMath.HaversineMeters(
				reference.Latitude, reference.Longitude, current.Latitude, current.Longitude);
			var verticalMeters = current.AltitudeMeters - reference.AltitudeMeters;

			// Prefer the GPS receiver's own measured velocity (from the raw djmd stream) over
			// differentiating position samples - it's not affected by GPS position quantization/lag.
			var speedKmh = current.GpsSpeedMs is { } gpsSpeedMs
				? gpsSpeedMs * 3.6
				: dt > 0
					? horizontalMeters / dt * 3.6
					: 0;
			var heading = horizontalMeters > 0.1
				? TelemetryMath.BearingDegrees(reference.Latitude, reference.Longitude, current.Latitude,
					current.Longitude)
				: result.Count > 0
					? result[^1].HeadingDegrees
					: 0;
			var gradient = horizontalMeters > 0.5 ? verticalMeters / horizontalMeters * 100.0 : 0;

			if (current.SampleTimeSeconds - frames[lastDistanceIndex].SampleTimeSeconds >= SpeedWindowSeconds)
			{
				TelemetryFrame prevStep = frames[lastDistanceIndex];
				cumulativeDistance += TelemetryMath.HaversineMeters(
					prevStep.Latitude, prevStep.Longitude, current.Latitude, current.Longitude);
				lastDistanceIndex = i;
			}

			// AccelX tracks forward/backward tilt (true pitch), not left/right lean - confirmed by
			// extracting frames from a controlled tilt-test recording: large AccelX swings show the
			// camera pitching to floor/ceiling with a level horizon, while large AccelY swings show
			// a canted horizon (roll) with the camera still facing forward. This gauge is meant to
			// read as left/right lean, so it uses AccelY. Negated: confirmed live in the GUI that the
			// un-negated sign put the dot on the wrong side (tilt left showed the dot going right).
			var rawPitch = -AngleMath.RadToDeg(Math.Atan2(current.AccelY,
				Math.Sqrt(current.AccelX * current.AccelX + current.AccelZ * current.AccelZ)));

			// Raw per-sample accelerometer readings are inherently noisy (vibration, bumps), so the
			// live HUD gauges show an exponential moving average instead of the instantaneous value.
			var frameDt = i > 0 ? current.SampleTimeSeconds - frames[i - 1].SampleTimeSeconds : 0;
			if (i == 0)
			{
				smoothedPitch = rawPitch;
				smoothedGForce = current.GForce;
			}
			else
			{
				smoothedPitch += Ema(frameDt, PitchTimeConstantSeconds) * (rawPitch - smoothedPitch);
				smoothedGForce += Ema(frameDt, GForceTimeConstantSeconds) * (current.GForce - smoothedGForce);
			}

			SunPosition sun = current.GpsTimestamp is { } ts
				? SunCalculator.Calculate(DateTime.SpecifyKind(ts, DateTimeKind.Utc), current.Latitude,
					current.Longitude)
				: default;

			var localEast = (current.Longitude - originLon) * metersPerDegLon;
			var localNorth = (current.Latitude - originLat) * metersPerDegLat;

			result.Add(new DerivedFrame(current, speedKmh, heading, gradient, cumulativeDistance, smoothedPitch, sun,
				localEast, localNorth, smoothedGForce));
		}

		return result;
	}

	private static double Ema(double dt, double timeConstantSeconds)
	{
		return 1.0 - Math.Exp(-Math.Max(dt, 0) / timeConstantSeconds);
	}

	public static DerivedFrame FindNearest(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		var lo = 0;
		var hi = frames.Count - 1;
		while (lo < hi)
		{
			var mid = (lo + hi) / 2;
			if (frames[mid].Raw.SampleTimeSeconds < seconds) lo = mid + 1;
			else hi = mid;
		}

		return frames[lo];
	}

	/// <summary>
	///     Contiguous [Start, End] SampleTimeSeconds ranges where the raw stream had no real GPS fix
	///     (TelemetryFrame.HasGpsFix false - see GpsForwardFill) - e.g. for a GUI to mark on a scrub
	///     timeline. Runs on the raw frames, not DerivedFrames, so it reflects what the camera actually
	///     recorded regardless of the (optional, cosmetic) GpsInterpolation smoothing setting.
	/// </summary>
	public static List<(double Start, double End)> FindGpsLossRanges(IReadOnlyList<TelemetryFrame> frames)
	{
		var ranges = new List<(double Start, double End)>();
		double? rangeStart = null;

		for (var i = 0; i < frames.Count; i++)
			if (!frames[i].HasGpsFix)
			{
				rangeStart ??= frames[i].SampleTimeSeconds;
			}
			else if (rangeStart is { } start)
			{
				ranges.Add((start, frames[i - 1].SampleTimeSeconds));
				rangeStart = null;
			}

		if (rangeStart is { } tailStart) ranges.Add((tailStart, frames[^1].SampleTimeSeconds));

		return ranges;
	}

	public static TelemetrySummary Summarize(IReadOnlyList<DerivedFrame> frames)
	{
		DerivedFrame first = frames[0];
		DerivedFrame last = frames[^1];

		var minAltitude = double.MaxValue;
		var maxAltitude = double.MinValue;
		var maxSpeed = 0.0;
		var maxGForce = 0.0;

		foreach (DerivedFrame f in frames)
		{
			minAltitude = Math.Min(minAltitude, f.Raw.AltitudeMeters);
			maxAltitude = Math.Max(maxAltitude, f.Raw.AltitudeMeters);
			maxSpeed = Math.Max(maxSpeed, f.SpeedKmh);
			maxGForce = Math.Max(maxGForce, f.Raw.GForce);
		}

		return new TelemetrySummary(
			frames.Count,
			last.Raw.SampleTimeSeconds - first.Raw.SampleTimeSeconds,
			last.CumulativeDistanceMeters,
			maxSpeed,
			minAltitude,
			maxAltitude,
			maxGForce,
			first.Raw.GpsTimestamp);
	}
}
