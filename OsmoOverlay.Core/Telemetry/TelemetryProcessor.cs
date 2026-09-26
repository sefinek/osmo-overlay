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
	// Reference window for speed/heading/gradient, and the smoothing time constant for the on-screen
	// G-force readout (roll/pitch: CameraTilt). All expressed in seconds (not sample count) so behavior
	// stays consistent regardless of the camera's telemetry sampling rate.
	internal const double SpeedWindowSeconds = 1.0;
	private const double GForceTimeConstantSeconds = 0.3;

	public static List<DerivedFrame> Process(IReadOnlyList<TelemetryFrame> frames, bool smoothGps = false)
	{
		if (smoothGps) frames = GpsInterpolation.Apply(frames);

		var cumulativeDistances = SteppedDistances(frames);
		var refIndex = 0;

		var speeds = new double[frames.Count];
		var headings = new double[frames.Count];
		var gradients = new double[frames.Count];

		for (var i = 0; i < frames.Count; i++)
		{
			TelemetryFrame current = frames[i];

			if (current.StartsAfterGap) refIndex = i;
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
			speeds[i] = speedKmh;
			headings[i] = horizontalMeters > 0.1
				? TelemetryMath.BearingDegrees(reference.Latitude, reference.Longitude, current.Latitude,
					current.Longitude)
				: i > 0
					? headings[i - 1]
					: 0;
			gradients[i] = horizontalMeters > 0.5 ? verticalMeters / horizontalMeters * 100.0 : 0;
		}

		var (roll, pitch) = CameraTilt.Compute(frames, speeds, headings);

		var originLat = frames[0].Latitude;
		var originLon = frames[0].Longitude;
		var metersPerDegLat = 111_320.0;
		var metersPerDegLon = 111_320.0 * Math.Cos(AngleMath.DegToRad(originLat));

		var result = new List<DerivedFrame>(frames.Count);
		var smoothedGForce = 0.0;

		for (var i = 0; i < frames.Count; i++)
		{
			TelemetryFrame current = frames[i];

			// Raw per-sample accelerometer readings are inherently noisy (vibration, bumps), so the
			// live HUD gauges show an exponential moving average instead of the instantaneous value.
			if (i == 0 || current.StartsAfterGap)
				smoothedGForce = current.GForce;
			else
				smoothedGForce += Ema(current.SampleTimeSeconds - frames[i - 1].SampleTimeSeconds, GForceTimeConstantSeconds) *
				                  (current.GForce - smoothedGForce);

			SunPosition sun = current.GpsTimestamp is { } ts
				? SunCalculator.Calculate(DateTime.SpecifyKind(ts, DateTimeKind.Utc), current.Latitude,
					current.Longitude)
				: default;

			var localEast = (current.Longitude - originLon) * metersPerDegLon;
			var localNorth = (current.Latitude - originLat) * metersPerDegLat;

			result.Add(new DerivedFrame(current, speeds[i], headings[i], gradients[i], cumulativeDistances[i], roll[i], pitch[i],
				sun, localEast, localNorth, smoothedGForce, current.StartsAfterGap));
		}

		return result;
	}

	/// <summary>
	///     Running distance per frame, advanced in steps of at least SpeedWindowSeconds - summing every
	///     sample-to-sample hop at 60 Hz would add up GPS jitter into distance never travelled. The last frame
	///     also gets the final partial step (up to a second of travel), so the total is complete. Nothing is added
	///     across a gap between files (StartsAfterGap): what was travelled while the camera was off isn't in the video.
	/// </summary>
	internal static double[] SteppedDistances(IReadOnlyList<TelemetryFrame> frames)
	{
		var distances = new double[frames.Count];
		double total = 0;
		var lastStep = 0;
		for (var i = 1; i < frames.Count; i++)
		{
			if (frames[i].StartsAfterGap)
			{
				// The partial step up to the previous file's last frame, like the recording's own last frame gets.
				total += TelemetryMath.HaversineMeters(frames[lastStep].Latitude, frames[lastStep].Longitude,
					frames[i - 1].Latitude, frames[i - 1].Longitude);
				distances[i - 1] = total;
				lastStep = i;
				distances[i] = total;
				continue;
			}

			var isLast = i == frames.Count - 1;
			if (frames[i].SampleTimeSeconds - frames[lastStep].SampleTimeSeconds >= SpeedWindowSeconds || isLast)
			{
				total += TelemetryMath.HaversineMeters(frames[lastStep].Latitude, frames[lastStep].Longitude,
					frames[i].Latitude, frames[i].Longitude);
				lastStep = i;
			}

			distances[i] = total;
		}

		return distances;
	}

	private static double Ema(double dt, double timeConstantSeconds)
	{
		return 1.0 - Math.Exp(-Math.Max(dt, 0) / timeConstantSeconds);
	}

	public static DerivedFrame FindNearest(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		return frames[FindIndex(frames, seconds)];
	}

	/// <summary>Index of the first frame at or after `seconds` (the last one past the end) - frames must be sorted by SampleTimeSeconds.</summary>
	public static int FindIndex(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		var lo = 0;
		var hi = frames.Count - 1;
		while (lo < hi)
		{
			var mid = (lo + hi) / 2;
			if (frames[mid].Raw.SampleTimeSeconds < seconds) lo = mid + 1;
			else hi = mid;
		}

		return lo;
	}

	/// <summary>True if the recording has at least one real GPS fix anywhere - false means the camera never had a signal at all (e.g. filmed indoors), not that it was "lost".</summary>
	public static bool HasAnyGpsFix(IReadOnlyList<TelemetryFrame> frames)
	{
		return frames.Any(f => f.HasGpsFix);
	}

	/// <summary>True if the recording has at least one real GPS timestamp anywhere (DateTimeText/UtcTimeText read this, not HasGpsFix - a fix without a decoded clock string is theoretically possible).</summary>
	public static bool HasAnyGpsTimestamp(IReadOnlyList<TelemetryFrame> frames)
	{
		return frames.Any(f => f.GpsTimestamp is not null);
	}

	/// <summary>
	///     Contiguous [Start, End] SampleTimeSeconds ranges where the raw stream had no real GPS fix
	///     (TelemetryFrame.HasGpsFix false - see GpsForwardFill) - e.g. for a GUI to mark on a scrub
	///     timeline. Runs on the raw frames, not DerivedFrames, so it reflects what the camera actually
	///     recorded regardless of the (optional, cosmetic) GpsInterpolation smoothing setting. Callers
	///     that want to flag only genuine anomalies (a fix that dropped out mid-recording, not a file
	///     that never had one at all) should gate this behind HasAnyGpsFix themselves.
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
