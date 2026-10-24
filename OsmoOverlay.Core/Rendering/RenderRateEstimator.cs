using System.Diagnostics;
using OsmoOverlay.Core.Localization;

namespace OsmoOverlay.Core;

/// <summary>
///     Render speed and time-left from a sliding window of recent progress rather than the average since
///     the start - the first seconds (ffmpeg spin-up, encoder lookahead filling, cold caches) run at a
///     very different pace than the rest, and a whole-run average would keep dragging the estimate
///     toward that for the entire render.
/// </summary>
internal sealed class RenderRateEstimator(double sourceFps)
{
	private const double WindowSeconds = 10;
	private const double WarmupSeconds = 3;

	private readonly Queue<(double Seconds, int Frames)> _samples = new();
	private readonly Stopwatch _clock = new();

	public void Add(int framesWritten)
	{
		if (!_clock.IsRunning) _clock.Start();

		double now = _clock.Elapsed.TotalSeconds;
		_samples.Enqueue((now, framesWritten));
		while (_samples.Count > 2 && now - _samples.Peek().Seconds > WindowSeconds)
			_samples.Dequeue();
	}

	/// <summary>Frames per second over the whole render so far (from the first frame written) - what's remembered for the next estimate.</summary>
	public double? AverageFps(int framesWritten)
	{
		double seconds = _clock.Elapsed.TotalSeconds;
		return seconds > 0 ? framesWritten / seconds : null;
	}

	/// <summary>" - 41.3 fps (0.69x realtime) - 00:12:05 left", or " - estimating time left..." during the first few seconds.</summary>
	public string Describe(int framesWritten, int totalFrames)
	{
		if (_clock.Elapsed.TotalSeconds < WarmupSeconds || _samples.Count < 2) return " - " + CoreStrings.Render_Estimating;

		(double Seconds, int Frames) oldest = _samples.Peek();
		double span = _clock.Elapsed.TotalSeconds - oldest.Seconds;
		if (span <= 0) return "";

		double fps = (framesWritten - oldest.Frames) / span;
		if (fps <= 0) return "";

		var left = TimeSpan.FromSeconds(Math.Max(totalFrames - framesWritten, 0) / fps);
		string realtime = sourceFps > 0 ? " " + string.Format(CoreStrings.Render_Realtime, fps / sourceFps) : "";
		return $" - {fps:0.0} fps{realtime} - " + string.Format(CoreStrings.Render_TimeLeft, left.ToString(@"hh\:mm\:ss"));
	}
}
