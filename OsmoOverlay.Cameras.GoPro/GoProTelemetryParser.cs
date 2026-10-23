using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.GoPro;

/// <summary>
///     Turns a recording's GPMF packets into one telemetry frame per video frame.
///     The streams and what is read from them:
///     ACCL - accelerometer, ~200 Hz, in m/s2 as the sensor feels it (gravity's opposite), averaged over each frame and kept in
///     the camera's own axes, in g (AccelX/Y/Z; GoProFormat.Gravity says what they are)
///     GRAV - the camera's own gravity vector, one per video frame, which turns into TelemetryFrame.Orientation (GoProTilt)
///     SHUT, ISOE, WBAL - exposure time, ISO and white balance per frame (WBAL every third or so)
///     GPS9 (HERO11 and later) or GPS5 (older) - position, altitude and 2D speed, ~10-18 Hz, with the receiver's fix, dilution and
///     time. A camera without GPS (HERO12) has none, and its frames have no fix.
///     Time: a stream's blocks carry STMP, the sensors' own clock in microseconds, and the streams one sample per video frame
///     (SHUT first, else GRAV...) start at the video's first frame - so a block's samples sit at (STMP - that stream's first) plus
///     their step, the next block's STMP in the same stream telling how far apart they are. Without STMP the packet's own time
///     (the file's) stands in, its samples spread evenly.
/// </summary>
internal static class GoProTelemetryParser
{
	private const double StandardGravity = 9.80665;

	// What the sensors' streams one per video frame are called - the first of them present gives the video's start on the sensors' clock.
	private static readonly string[] FrameClockStreams = ["SHUT", "GRAV", "ISOE", "CORI", "IORI"];

	public static TelemetryExtractionResult Parse(IReadOnlyList<GpmfPacket> packets, double fps, long? frameCount)
	{
		if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

		string? model = packets.SelectMany(p => p.Devices).Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
		double? clockStart = FrameClockStart(packets);
		GpmfSeries accel = GpmfSeries.Build(packets, "ACCL", clockStart);
		if (accel.Count == 0 || accel.Axes != 3) throw new InvalidDataException("The GoPro telemetry has no three-axis accelerometer samples.");

		int frames = (int)(frameCount ?? (long)Math.Round(packets.Sum(p => p.DurationSeconds) * fps));
		if (frames <= 0) throw new InvalidDataException("The GoPro telemetry covers no video frames.");

		double[] accelerations = accel.Resample(frames, fps, true);
		(int Axis, double Sign)[] channels = ImuAxes(accel.Spans.Select(b => b.Stream.InputOrientation).FirstOrDefault(o => o is not null));
		GpmfSeries gravity = GpmfSeries.Build(packets, "GRAV", clockStart);
		double[] gravities = gravity.Axes == 3 ? gravity.Resample(frames, fps, false) : [];
		(double[] iso, double[] shutter, double[] whiteBalance) = (Optional("ISOE"), Optional("SHUT"), Optional("WBAL"));
		GoProGps gps = GoProGps.Build(packets, clockStart);
		var audio = new GoProDiagnostics(packets, "AALP", 2, clockStart);
		var wind = new GoProDiagnostics(packets, "WNDM", 2, clockStart);
		var luma = new GoProDiagnostics(packets, "YAVG", 1, clockStart);
		var fill = new GpsForwardFill();

		var result = new List<TelemetryFrame>(frames);
		int temperatureSpan = 0;
		for (int f = 0; f < frames; f++)
		{
			while (temperatureSpan + 1 < accel.Spans.Count && accel.Spans[temperatureSpan + 1].StartSeconds <= f / fps) temperatureSpan++;
			GpmfSpan temperatureBlock = accel.Spans[temperatureSpan];
			double? temperature = f / fps >= temperatureBlock.StartSeconds - 1 / fps &&
			                      f / fps < temperatureBlock.StartSeconds + temperatureBlock.Stream.Count * temperatureBlock.StepSeconds
				? temperatureBlock.Stream.TemperatureCelsius : null;
			double[] g = new double[3];
			for (int channel = 0; channel < 3; channel++)
				g[channels[channel].Axis] = channels[channel].Sign * accelerations[f * accel.Axes + channel] / StandardGravity;

			(double? lat, double? lon, double? altitude, double? speed, DateTime? utc) = gps.At(f / fps);
			(double latitude, double longitude, double altitudeMeters, bool hasFix) = fill.Apply(lat, lon, altitude);
			result.Add(new TelemetryFrame(
				f, f / fps, latitude, longitude, altitudeMeters,
				hasFix ? utc : null,
				g[0], g[1], g[2],
				hasFix ? speed : null,
				iso.Length > f ? (float)iso[f] : null,
				shutter.Length > f ? shutter[f] : null,
				whiteBalance.Length > f ? (int)Math.Round(whiteBalance[f]) : null,
				hasFix,
				Orientation: gravities.Length > 0 ? GoProTilt.Orientation(new(gravities[f * 3], gravities[f * 3 + 1], gravities[f * 3 + 2])) : null,
				ImuTemperatureCelsius: temperature,
				AudioRmsDbfs: AudioLevel(audio.At(f / fps, 0)),
				AudioPeakDbfs: AudioLevel(audio.At(f / fps, 1)),
				WindProcessingActive: wind.At(f / fps, 0) switch { 0 => false, 1 => true, _ => null },
				WindProcessingLevel: wind.At(f / fps, 1) is >= 0 and <= 100 and { } meter ? meter : null,
				ImageLuma: luma.At(f / fps, 0) is >= 0 and <= 255 and { } y ? y : null));
		}

		return new TelemetryExtractionResult(result, model);

		double[] Optional(string key)
		{
			GpmfSeries series = GpmfSeries.Build(packets, key, clockStart);
			return series is { Count: > 0, Axes: 1 } ? series.Resample(frames, fps, false) : [];
		}
	}

	private static double? AudioLevel(double? value) => value is >= -128 and <= 0 ? value : null;

	private static double? FrameClockStart(IReadOnlyList<GpmfPacket> packets)
	{
		foreach (string key in FrameClockStreams)
		{
			foreach (GpmfPacket packet in packets)
			{
				foreach (GpmfDevice device in packet.Devices)
				{
					if (device.Streams.FirstOrDefault(s => s.Key == key)?.StampMicroseconds is { } stamp) return stamp;
				}
			}
		}

		return null;
	}

	/// <summary>
	///     Which of the camera's axes each ACCL channel is, from ORIN ("ZXY" on a HERO12): its letter names the axis and
	///     a lower case one means the channel runs the other way. The axes are named for the way the sensor feels them -
	///     X to the left, Y backward, Z up (found on a HERO12: ZXY put gravity's reaction on the first channel as up).
	///     Index 0, 1, 2 = X, Y, Z. Anything but a permutation of the letters X, Y, Z is taken as the HERO12's.
	/// </summary>
	internal static (int Axis, double Sign)[] ImuAxes(string? orientation)
	{
		return TryImuAxes(orientation) ?? TryImuAxes("ZXY")!;
	}

	private static (int Axis, double Sign)[]? TryImuAxes(string? orientation)
	{
		if (orientation is not { Length: 3 }) return null;

		(int, double)[] axes = new (int, double)[3];
		bool[] used = new bool[3];
		for (int i = 0; i < 3; i++)
		{
			int axis = char.ToUpperInvariant(orientation[i]) - 'X';
			if (axis is < 0 or > 2 || used[axis]) return null;

			used[axis] = true;
			axes[i] = (axis, char.IsLower(orientation[i]) ? -1 : 1);
		}

		return axes;
	}
}
