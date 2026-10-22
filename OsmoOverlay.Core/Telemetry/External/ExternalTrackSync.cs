namespace OsmoOverlay.Core.Telemetry.External;

public enum TrackSyncMethod
{
	/// <summary>The camera's clock and the track's agree - the recording lies within the track's time.</summary>
	Clock,

	/// <summary>The camera's clock is elsewhere (a camera never set to the time), so the recording was matched to the track by how both moved.</summary>
	Motion
}

/// <summary>`TrackTimeAtVideoStartUtc` is the moment on the track's clock of the recording's first frame (ExternalTrackMerge); Correlation is 1 for Clock.</summary>
public sealed record TrackSyncEstimate(DateTime TrackTimeAtVideoStartUtc, TrackSyncMethod Method, double Correlation);

/// <summary>
///     Where on the track's clock a recording began. Two devices that both took their time from satellites agree to the second,
///     but a camera's own clock is only as right as the last time somebody set it: a GoPro off by minutes is common. So:
///     when the recording lies within the track's time by the camera's clock, that is taken to be right (Clock). Otherwise the
///     recording is slid along the track until how much the camera shook (the accelerometer's variation, once a second)
///     follows the track's speed best (Motion) - a ride shakes a camera far more than standing does. That match is accepted only when it
///     is clear: a correlation of at least MinCorrelation, and the next best match somewhere else well behind. A recording
///     that doesn't move with the track (a camera turned about on the spot while the bike computer saw a stop) gets
///     no estimate - the offset then has to be given.
/// </summary>
public static class ExternalTrackSync
{
	/// <summary>The resolution of the search, and of the two profiles it compares.</summary>
	private const double StepSeconds = 0.5;

	/// <summary>Less overlap than this (seconds) between the recording and the track says nothing.</summary>
	private const double MinOverlapSeconds = 20;

	public const double MinCorrelation = 0.5;

	/// <summary>How far a second candidate must stay behind the best one, and how far from it (seconds) it has to be to count as another.</summary>
	private const double MinMargin = 0.1;

	private const double DistinctSeconds = 5;

	public static TrackSyncEstimate? Estimate(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? videoStartUtc, ExternalTrack track)
	{
		if (frames.Count == 0 || fps <= 0) return null;

		double videoSeconds = frames[^1].SampleTimeSeconds + 1 / fps;
		if (videoStartUtc is { } start && Overlap(start, videoSeconds, track) >= Math.Min(videoSeconds, (track.EndUtc - track.StartUtc).TotalSeconds) / 2)
			return new TrackSyncEstimate(start, TrackSyncMethod.Clock, 1);

		return ByMotion(frames, fps, videoSeconds, track);
	}

	private static double Overlap(DateTime start, double seconds, ExternalTrack track)
	{
		DateTime from = start > track.StartUtc ? start : track.StartUtc;
		DateTime to = start.AddSeconds(seconds) < track.EndUtc ? start.AddSeconds(seconds) : track.EndUtc;
		return Math.Max(0, (to - from).TotalSeconds);
	}

	private static TrackSyncEstimate? ByMotion(IReadOnlyList<TelemetryFrame> frames, double fps, double videoSeconds, ExternalTrack track)
	{
		double[] camera = CameraShake(frames, fps, videoSeconds);
		double trackSeconds = (track.EndUtc - track.StartUtc).TotalSeconds;
		double[] speed = TrackSpeed(track, trackSeconds);

		// Shift s: the recording's first frame is at s seconds into the track (negative: the track begins after it).
		int minOverlap = (int)(MinOverlapSeconds / StepSeconds);
		int from = -(camera.Length - minOverlap);
		int to = speed.Length - minOverlap;
		List<(int Shift, double Correlation)> scores = [];
		for (int shift = from; shift <= to; shift++)
		{
			if (Correlate(camera, speed, shift, minOverlap) is { } r) scores.Add((shift, r));
		}

		if (scores.Count == 0) return null;

		(int bestShift, double best) = scores.MaxBy(s => s.Correlation);
		double rival = scores.Where(s => Math.Abs(s.Shift - bestShift) * StepSeconds > DistinctSeconds).Select(s => s.Correlation).DefaultIfEmpty(-1).Max();
		if (best < MinCorrelation || best - rival < MinMargin) return null;

		return new TrackSyncEstimate(track.StartUtc.AddSeconds(bestShift * StepSeconds), TrackSyncMethod.Motion, best);
	}

	/// <summary>How much the camera shook in each half second: the standard deviation of the accelerometer's length (g) over the frames in it.</summary>
	internal static double[] CameraShake(IReadOnlyList<TelemetryFrame> frames, double fps, double videoSeconds)
	{
		int bins = Math.Max(1, (int)Math.Floor(videoSeconds / StepSeconds));
		double[] sum = new double[bins], sumSquares = new double[bins];
		int[] count = new int[bins];
		foreach (TelemetryFrame frame in frames)
		{
			int bin = (int)(frame.SampleTimeSeconds / StepSeconds);
			if (bin >= bins) continue;

			double g = frame.GForce;
			sum[bin] += g;
			sumSquares[bin] += g * g;
			count[bin]++;
		}

		double[] shake = new double[bins];
		for (int i = 0; i < bins; i++)
		{
			if (count[i] < 2) continue;

			double mean = sum[i] / count[i];
			shake[i] = Math.Sqrt(Math.Max(0, sumSquares[i] / count[i] - mean * mean));
		}

		return shake;
	}

	/// <summary>The track's speed (m/s) in each half second from its first fix; a lost signal is NaN, a stop 0.</summary>
	internal static double[] TrackSpeed(ExternalTrack track, double trackSeconds)
	{
		double[] speed = new double[(int)Math.Floor(trackSeconds / StepSeconds) + 1];
		int cursor = 0;
		for (int i = 0; i < speed.Length; i++)
		{
			speed[i] = track.At(track.StartUtc.AddSeconds(i * StepSeconds), ref cursor) is { } sample
				? sample.State == ExternalTrackState.Lost ? double.NaN : sample.SpeedMs ?? double.NaN
				: double.NaN;
		}

		return speed;
	}

	/// <summary>Pearson's correlation of the camera's shake with the track's speed where the recording, `shift` steps into the track, overlaps it - null with too little to go by.</summary>
	private static double? Correlate(double[] camera, double[] speed, int shift, int minOverlap)
	{
		double n = 0, sumC = 0, sumS = 0, sumCC = 0, sumSS = 0, sumCS = 0;
		for (int i = Math.Max(0, -shift); i < camera.Length && i + shift < speed.Length; i++)
		{
			double s = speed[i + shift];
			if (double.IsNaN(s)) continue;

			double c = camera[i];
			n++;
			sumC += c;
			sumS += s;
			sumCC += c * c;
			sumSS += s * s;
			sumCS += c * s;
		}

		if (n < minOverlap) return null;

		double varianceC = sumCC - sumC * sumC / n;
		double varianceS = sumSS - sumS * sumS / n;
		if (varianceC <= 1e-12 || varianceS <= 1e-12) return null;

		return (sumCS - sumC * sumS / n) / Math.Sqrt(varianceC * varianceS);
	}
}
