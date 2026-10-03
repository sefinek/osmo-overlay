using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Reframe;

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
	// A slope is measured over this much distance travelled, centered on the frame: GPS altitude wanders a few meters on its
	// own, so over a second or two of walking it read as a 10% climb on a flat bridge. Centered, it doesn't lag behind the road.
	internal const double GradientWindowMeters = 100;
	// Below this the receiver's own speed says the camera stands still - a step's position change is then the fix wandering,
	// not travel. Well under walking pace, so a slow climb still counts.
	internal const double StandstillKmh = 1.0;

	/// <param name="camera">
	///     Reads the accelerometer's axes (ICameraFormat.Gravity) for roll, pitch and the G-meter - all 0 without it or
	///     when it doesn't know them.
	/// </param>
	/// <param name="speedCorrectionPercent">
	///     Added to every shown speed (DerivedFrame.SpeedKmh) for a GPS that reads low - a scale, since a receiver's error grows
	///     with speed and standing still must stay 0. Tilt, heading and distance keep the measured values.
	/// </param>
	public static List<DerivedFrame> Process(IReadOnlyList<TelemetryFrame> frames, ICameraFormat? camera, bool smoothGps = false,
		double speedCorrectionPercent = 0)
	{
		double speedFactor = SpeedCalibration.Factor(speedCorrectionPercent);
		if (smoothGps) frames = GpsInterpolation.Apply(frames);

		double[] cumulativeDistances = SteppedDistances(frames);
		int refIndex = 0;

		double[] speeds = new double[frames.Count];
		double[] headings = new double[frames.Count];
		double[] gradients = Gradients(frames, cumulativeDistances);

		for (int i = 0; i < frames.Count; i++)
		{
			TelemetryFrame current = frames[i];

			if (current.StartsAfterGap) refIndex = i;
			while (refIndex < i && current.SampleTimeSeconds - frames[refIndex].SampleTimeSeconds > SpeedWindowSeconds)
				refIndex++;
			TelemetryFrame reference = frames[refIndex];

			double dt = current.SampleTimeSeconds - reference.SampleTimeSeconds;
			double horizontalMeters = TelemetryMath.HaversineMeters(
				reference.Latitude, reference.Longitude, current.Latitude, current.Longitude);

			// Prefer the GPS receiver's own measured velocity (when the camera records it) over
			// differentiating position samples - it's not affected by GPS position quantization/lag.
			double speedKmh = current.GpsSpeedMs is { } gpsSpeedMs
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
		}

		Direction[]? gravity = GravityOf(frames, camera);
		(double[] roll, double[] pitch) = gravity is null
			? (new double[frames.Count], new double[frames.Count])
			: CameraTilt.Compute(frames, gravity, speeds, headings);

		double originLat = frames[0].Latitude;
		double originLon = frames[0].Longitude;
		double metersPerDegLat = 111_320.0;
		double metersPerDegLon = 111_320.0 * Math.Cos(AngleMath.DegToRad(originLat));

		var result = new List<DerivedFrame>(frames.Count);
		double smoothedGForce = 0.0;

		for (int i = 0; i < frames.Count; i++)
		{
			TelemetryFrame current = frames[i];

			// Raw per-sample accelerometer readings are inherently noisy (vibration, bumps), so the
			// live HUD gauges show an exponential moving average instead of the instantaneous value.
			if (i == 0 || current.StartsAfterGap)
			{
				smoothedGForce = current.GForce;
			}
			else
			{
				smoothedGForce += Ema(current.SampleTimeSeconds - frames[i - 1].SampleTimeSeconds, GForceTimeConstantSeconds) *
				                  (current.GForce - smoothedGForce);
			}

			SunPosition sun = current.GpsTimestamp is { } ts
				? SunCalculator.Calculate(DateTime.SpecifyKind(ts, DateTimeKind.Utc), current.Latitude,
					current.Longitude)
				: default;

			double localEast = (current.Longitude - originLon) * metersPerDegLon;
			double localNorth = (current.Latitude - originLat) * metersPerDegLat;
			// The G-meter's dot: sideways to the right, forward up.
			(double lateral, double longitudinal) = gravity is null ? (0, 0) : (gravity[i].X, -gravity[i].Z);

			result.Add(new DerivedFrame(current, speeds[i] * speedFactor, headings[i], gradients[i], cumulativeDistances[i], roll[i], pitch[i],
				sun, localEast, localNorth, smoothedGForce, lateral, longitudinal, current.StartsAfterGap));
		}

		return result;
	}

	/// <summary>Every frame's gravity in the camera's own space - null when the camera doesn't know its axes.</summary>
	internal static Direction[]? GravityOf(IReadOnlyList<TelemetryFrame> frames, ICameraFormat? camera)
	{
		if (camera is null || frames.Count == 0 || camera.Gravity(frames[0]) is null) return null;
		return [.. frames.Select(f => camera.Gravity(f) ?? default)];
	}

	/// <summary>
	///     Running distance per frame, advanced in steps of at least SpeedWindowSeconds - summing every
	///     sample-to-sample hop at 60 Hz would add up GPS jitter into distance never travelled. The last frame
	///     also gets the final partial step (up to a second of travel), so the total is complete. Nothing is added
	///     across a gap between files (StartsAfterGap): what was travelled while the camera was off isn't in the video,
	///     nor for a step the receiver measured as standing still at both ends (StandstillKmh).
	/// </summary>
	internal static double[] SteppedDistances(IReadOnlyList<TelemetryFrame> frames)
	{
		double[] distances = new double[frames.Count];
		double total = 0;
		int lastStep = 0;
		for (int i = 1; i < frames.Count; i++)
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

			bool isLast = i == frames.Count - 1;
			if (frames[i].SampleTimeSeconds - frames[lastStep].SampleTimeSeconds >= SpeedWindowSeconds || isLast)
			{
				if (!IsStandingStill(frames[lastStep]) || !IsStandingStill(frames[i]))
				{
					total += TelemetryMath.HaversineMeters(frames[lastStep].Latitude, frames[lastStep].Longitude,
						frames[i].Latitude, frames[i].Longitude);
				}

				lastStep = i;
			}

			distances[i] = total;
		}

		return distances;
	}

	private static bool IsStandingStill(TelemetryFrame frame)
	{
		return frame.GpsSpeedMs * 3.6 < StandstillKmh;
	}

	/// <summary>
	///     Percent climb over GradientWindowMeters centered on each frame (half behind, half ahead) - 0 where less than half of
	///     that was travelled around it (a short clip, a long stop). Never across a gap between files.
	/// </summary>
	internal static double[] Gradients(IReadOnlyList<TelemetryFrame> frames, double[] distances)
	{
		const double half = GradientWindowMeters / 2;
		double[] gradients = new double[frames.Count];
		// Distance grows in steps (SteppedDistances): the altitude goes with the frame its step was taken at, not one up to
		// a second of travel (or a whole stop) past it.
		int[] stepFrame = new int[frames.Count];
		for (int i = 0; i < frames.Count; i++)
			stepFrame[i] = i > 0 && !frames[i].StartsAfterGap && distances[i - 1] == distances[i] ? stepFrame[i - 1] : i;

		int back = 0, ahead = 0;
		for (int i = 0; i < frames.Count; i++)
		{
			if (frames[i].StartsAfterGap) back = i;
			ahead = Math.Max(ahead, i);

			while (back < i && distances[i] - distances[back + 1] >= half) back++;
			while (ahead + 1 < frames.Count && !frames[ahead + 1].StartsAfterGap && distances[ahead] - distances[i] < half) ahead++;

			int from = stepFrame[back];
			double span = distances[ahead] - distances[from];
			if (span >= half) gradients[i] = (frames[ahead].AltitudeMeters - frames[from].AltitudeMeters) / span * 100;
		}

		return gradients;
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
		int lo = 0;
		int hi = frames.Count - 1;
		while (lo < hi)
		{
			int mid = (lo + hi) / 2;
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
	///     (TelemetryFrame.HasGpsFix false) - e.g. for a GUI to mark on a scrub
	///     timeline. Runs on the raw frames, not DerivedFrames, so it reflects what the camera actually
	///     recorded regardless of the (optional, cosmetic) GpsInterpolation smoothing setting. Callers
	///     that want to flag only genuine anomalies (a fix that dropped out mid-recording, not a file
	///     that never had one at all) should gate this behind HasAnyGpsFix themselves.
	/// </summary>
	public static List<(double Start, double End)> FindGpsLossRanges(IReadOnlyList<TelemetryFrame> frames)
	{
		var ranges = new List<(double Start, double End)>();
		double? rangeStart = null;

		for (int i = 0; i < frames.Count; i++)
		{
			if (!frames[i].HasGpsFix)
			{
				rangeStart ??= frames[i].SampleTimeSeconds;
			}
			else if (rangeStart is { } start)
			{
				ranges.Add((start, frames[i - 1].SampleTimeSeconds));
				rangeStart = null;
			}
		}

		if (rangeStart is { } tailStart) ranges.Add((tailStart, frames[^1].SampleTimeSeconds));

		return ranges;
	}

	public static TelemetrySummary Summarize(IReadOnlyList<DerivedFrame> frames)
	{
		DerivedFrame first = frames[0];
		DerivedFrame last = frames[^1];

		double minAltitude = double.MaxValue;
		double maxAltitude = double.MinValue;
		double maxSpeed = 0.0;
		double maxGForce = 0.0;

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
