namespace OsmoOverlay.Core.Telemetry.External;

public enum TrackSyncMethod
{
	/// <summary>The camera's clock is elsewhere (a camera never set to the time), so the recording was matched to the track by how both moved.</summary>
	Motion
}

/// <summary>An approximate motion match, requiring visual verification. Correlation is not a probability.</summary>
public sealed record TrackSyncEstimate(DateTime TrackTimeAtVideoStartUtc, TrackSyncMethod Method, double Correlation);

/// <summary>
///     Suggests a video start by correlating accelerometer variation with track speed, excluding gaps and timer pauses.
///     A strong, distinct match is still a heuristic: camera shake is not a speed sensor. Overlapping clock ranges
///     do not establish alignment. Manual anchors are independent of the camera's clock. The search runs on half-second bins, ranked by significance;
///     the winner is then refined to a few hundredths of a second (Refine).
/// </summary>
public static class ExternalTrackSync
{
	/// <summary>The resolution of the search, and of the two profiles it compares.</summary>
	private const double StepSeconds = 0.5;

	/// <summary>Less overlap than this (seconds) between the recording and the track says nothing.</summary>
	private const double MinOverlapSeconds = 20;

	public const double MinCorrelation = 0.5;

	/// <summary>
	///     How far a second candidate must stay behind the best one - as a share of the best one's significance (see Significance) - and how
	///     far from it (seconds) it has to be to count as another.
	/// </summary>
	private const double MaxRivalShare = 0.85;

	private const double DistinctSeconds = 5;

	/// <summary>The refinement of the best match: how far around it (seconds) and at what step the recording is slid again, on the exact times of the camera's bins.</summary>
	private const double RefineSpanSeconds = 1, RefineStepSeconds = 0.05;

	/// <summary>How many points of the track a bin is averaged from.</summary>
	private const int BinSamples = 5;

	public static TrackSyncEstimate? Estimate(IReadOnlyList<TelemetryFrame> frames, double fps, ExternalTrack track)
	{
		if (frames.Count == 0 || fps <= 0) return null;

		return ByMotion(frames, fps, frames[^1].SampleTimeSeconds + 1 / fps, track);
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
		List<(int Shift, double Correlation, double Significance)> scores = [];
		for (int shift = from; shift <= to; shift++)
		{
			if (Correlate(camera, speed, shift, minOverlap) is { } match) scores.Add((shift, match.Correlation, Significance(match.Correlation, match.Samples)));
		}

		if (scores.Count == 0) return null;

		(int bestShift, double best, double bestSignificance) = scores.MaxBy(s => s.Significance);
		double rival = scores.Where(s => Math.Abs(s.Shift - bestShift) * StepSeconds > DistinctSeconds).Select(s => s.Significance).DefaultIfEmpty(0).Max();
		if (best < MinCorrelation || rival > bestSignificance * MaxRivalShare) return null;

		(double startSeconds, double refined) = Refine(camera, track, bestShift * StepSeconds, minOverlap) ?? (bestShift * StepSeconds, best);
		return new TrackSyncEstimate(track.StartUtc.AddSeconds(startSeconds), TrackSyncMethod.Motion, Math.Max(best, refined));
	}

	/// <summary>
	///     The best match slid on by tenths of a step: the coarse search compares bins that start at whole steps, which leaves its answer up to half a step off and
	///     biased. Here the track's speed is read at the middle of each of the camera's bins for each candidate start (seconds into the track), so the answer is as
	///     exact as the two profiles allow. Null when no candidate has enough to go by.
	/// </summary>
	private static (double StartSeconds, double Correlation)? Refine(double[] camera, ExternalTrack track, double coarseSeconds, int minOverlap)
	{
		double[] aligned = new double[camera.Length];
		(double StartSeconds, double Correlation)? best = null;
		int steps = (int)Math.Round(RefineSpanSeconds / RefineStepSeconds);
		for (int k = -steps; k <= steps; k++)
		{
			double start = coarseSeconds + k * RefineStepSeconds;
			int cursor = 0;
			for (int i = 0; i < aligned.Length; i++) aligned[i] = MeanSpeed(track, start + i * StepSeconds, ref cursor);

			if (Correlate(camera, aligned, 0, minOverlap) is { } match && (best is null || match.Correlation > best.Value.Correlation)) best = (start, match.Correlation);
		}

		return best;
	}

	/// <summary>
	///     Fisher's z of a correlation over `samples` pairs: the same correlation means more over a long overlap than over a short one, so a match that
	///     only a third of the recording overlaps doesn't outrank one that all of it does. A ranking, not a probability (neighbouring bins aren't independent).
	/// </summary>
	private static double Significance(double correlation, double samples)
	{
		return Math.Atanh(Math.Clamp(correlation, -0.999, 0.999)) * Math.Sqrt(Math.Max(samples - 3, 0));
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
		for (int i = 0; i < speed.Length; i++) speed[i] = SpeedAt(track, track.StartUtc.AddSeconds(i * StepSeconds), ref cursor);

		return speed;
	}

	/// <summary>The track's mean speed over the half second from `seconds` into the track, as the camera's bin is a mean over its half second; NaN when any sample is missing.</summary>
	private static double MeanSpeed(ExternalTrack track, double seconds, ref int cursor)
	{
		double sum = 0;
		for (int k = 0; k < BinSamples; k++)
		{
			double speed = SpeedAt(track, track.StartUtc.AddSeconds(seconds + (k + 0.5) * StepSeconds / BinSamples), ref cursor);
			if (double.IsNaN(speed)) return double.NaN;

			sum += speed;
		}

		return sum / BinSamples;
	}

	/// <summary>The track's speed (m/s) at a moment: NaN outside it, in a gap or a timer pause, and where it has no speed.</summary>
	private static double SpeedAt(ExternalTrack track, DateTime utc, ref int cursor)
	{
		return track.At(utc, ref cursor) is { State: ExternalTrackState.Fix } sample ? sample.SpeedMs ?? double.NaN : double.NaN;
	}

	/// <summary>Pearson's correlation of the camera's shake with the track's speed where the recording, `shift` steps into the track, overlaps it - null with too little to go by.</summary>
	private static (double Correlation, double Samples)? Correlate(double[] camera, double[] speed, int shift, int minOverlap)
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

		return ((sumCS - sumC * sumS / n) / Math.Sqrt(varianceC * varianceS), n);
	}
}
