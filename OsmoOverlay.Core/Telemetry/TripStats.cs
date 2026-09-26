namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     Running statistics up to each frame (the TripStat widget shows the value so far, the route intro the last frame's),
///     computed once per set of frames. Nothing is counted across a cut or a gap between files (StartsAfterCut).
///     Elevation gain/loss use a ClimbThresholdMeters hysteresis instead of summing every altitude change: GPS altitude
///     wanders by a metre or two, and on 6 real Osmo Action 6 rides the plain sum reported 3-10x the whole ride's
///     altitude range on flat ground (28 m of climbing inside a 3 m range), while with 3 m gain - loss matched the net
///     change within 2 m.
///     Moving time counts only frames at MovingThresholdKmh or faster (auto-pause, as Garmin/Strava do), and the average
///     speed is over that time.
/// </summary>
public sealed class TripStats
{
	public const double MovingThresholdKmh = 3;
	internal const double ClimbThresholdMeters = 3;

	private readonly double[] _maxSpeedKmh;
	private readonly double[] _movingSeconds;
	private readonly double[] _gainMeters;
	private readonly double[] _lossMeters;
	private readonly double[] _distanceMeters;

	private TripStats(int count)
	{
		_maxSpeedKmh = new double[count];
		_movingSeconds = new double[count];
		_gainMeters = new double[count];
		_lossMeters = new double[count];
		_distanceMeters = new double[count];
	}

	public static TripStats Compute(IReadOnlyList<DerivedFrame> frames)
	{
		var stats = new TripStats(frames.Count);
		if (frames.Count == 0) return stats;

		double maxSpeed = 0, moving = 0, gain = 0, loss = 0;
		var anchor = frames[0].Raw.AltitudeMeters;

		for (var i = 0; i < frames.Count; i++)
		{
			DerivedFrame frame = frames[i];
			maxSpeed = Math.Max(maxSpeed, frame.SpeedKmh);

			if (i > 0 && frame.StartsAfterCut)
			{
				anchor = frame.Raw.AltitudeMeters;
			}
			else if (i > 0)
			{
				if (frame.SpeedKmh >= MovingThresholdKmh)
					moving += Math.Max(frame.Raw.SampleTimeSeconds - frames[i - 1].Raw.SampleTimeSeconds, 0);

				var change = frame.Raw.AltitudeMeters - anchor;
				if (change >= ClimbThresholdMeters)
				{
					gain += change;
					anchor = frame.Raw.AltitudeMeters;
				}
				else if (change <= -ClimbThresholdMeters)
				{
					loss -= change;
					anchor = frame.Raw.AltitudeMeters;
				}
			}

			stats._maxSpeedKmh[i] = maxSpeed;
			stats._movingSeconds[i] = moving;
			stats._gainMeters[i] = gain;
			stats._lossMeters[i] = loss;
			stats._distanceMeters[i] = frame.CumulativeDistanceMeters;
		}

		return stats;
	}

	public int Count => _maxSpeedKmh.Length;

	public double MaxSpeedKmh(int index)
	{
		return _maxSpeedKmh[index];
	}

	public double MovingSeconds(int index)
	{
		return _movingSeconds[index];
	}

	/// <summary>Distance over moving time - 0 until the first second of movement, so a start from standstill doesn't flash a wild value.</summary>
	public double AverageSpeedKmh(int index)
	{
		return _movingSeconds[index] >= 1 ? _distanceMeters[index] / _movingSeconds[index] * 3.6 : 0;
	}

	public double ElevationGainMeters(int index)
	{
		return _gainMeters[index];
	}

	public double ElevationLossMeters(int index)
	{
		return _lossMeters[index];
	}
}
