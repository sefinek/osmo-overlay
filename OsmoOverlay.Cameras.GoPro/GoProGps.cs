namespace OsmoOverlay.Cameras.GoPro;

/// <summary>
///     The GPS samples of a recording (GPMF's GPS9, else GPS5), on the video's clock, with whether each had a usable fix. GPS9
///     carries each sample's time, dilution and fix (days since 2000 and seconds since midnight, UTC); GPS5 has them per block
///     (GPSU, the time of its first sample, GPSF, GPSP).
/// </summary>
internal sealed class GoProGps
{
	// Past this long from the nearest sample (the receiver gave up, or stopped being written) a frame has no GPS.
	private const double MaxSampleDistanceSeconds = 2.0;

	private const double MaxDilution = 10;
	private const int MinFix = 2;

	private static readonly DateTime DaysEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	private readonly (double Lat, double Lon, double Altitude, double SpeedMs, DateTime? Utc, bool Fix)[] _samples;
	private readonly double[] _times;

	private GoProGps(double[] times, (double, double, double, double, DateTime?, bool)[] samples)
	{
		_times = times;
		_samples = samples;
	}

	public static GoProGps Build(IReadOnlyList<GpmfPacket> packets, double? clockStart)
	{
		GpmfSeries series = GpmfSeries.Build(packets, "GPS9", clockStart);
		bool nine = series.Count > 0 && series.Axes >= 9;
		if (!nine) series = GpmfSeries.Build(packets, "GPS5", clockStart);
		if (series.Count == 0 || series.Axes < 5) return new GoProGps([], []);

		List<double> times = [];
		List<(double, double, double, double, DateTime?, bool)> samples = [];
		foreach (GpmfSpan span in series.Spans)
		{
			for (int k = 0; k < span.Stream.Count; k++)
			{
				int index = span.FirstSample + k;
				ReadOnlySpan<double> row = series.Values.AsSpan(index * series.Axes, series.Axes);
				bool usable = nine
					? row[8] >= MinFix && row[7] <= MaxDilution
					: (span.Stream.GpsFix ?? MinFix) >= MinFix && (span.Stream.GpsPrecision ?? 0) <= MaxDilution;
				usable &= !(row[0] == 0 && row[1] == 0) && double.IsFinite(row[0] + row[1] + row[2] + row[3]);

				DateTime? utc = nine
					? DaysEpoch.AddDays(row[5]).AddSeconds(row[6])
					: GpmfReader.ParseGpsUtc(span.Stream.GpsUtc) is { } start ? start.AddSeconds(k * span.StepSeconds) : null;
				times.Add(series.Times[index]);
				samples.Add((row[0], row[1], row[2], row[3], utc, usable));
			}
		}

		return new GoProGps([.. times], [.. samples]);
	}

	/// <summary>
	///     What the nearest sample says at `t` seconds, the time cut to whole seconds like a receiver's - nothing (null) when
	///     that sample had no usable fix, or there is none within MaxSampleDistanceSeconds.
	/// </summary>
	public (double? Lat, double? Lon, double? AltitudeMeters, double? SpeedMs, DateTime? Utc) At(double t)
	{
		if (_times.Length == 0) return default;

		int i = Array.BinarySearch(_times, t);
		if (i < 0)
		{
			i = ~i;
			if (i == _times.Length || (i > 0 && t - _times[i - 1] <= _times[i] - t)) i--;
		}

		if (Math.Abs(_times[i] - t) > MaxSampleDistanceSeconds) return default;

		(double lat, double lon, double altitude, double speed, DateTime? utc, bool fix) = _samples[i];
		if (!fix) return default;

		return (lat, lon, altitude, speed, utc is { } time ? new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) : null);
	}
}
