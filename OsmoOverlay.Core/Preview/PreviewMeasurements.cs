namespace OsmoOverlay.Core.Preview;

public enum PreviewStage { Decode, Overlay, Draw }
public enum PreviewDrop { Decode, Overlay, Display }

public sealed record PreviewTiming(int Count, double MedianMs, double P95Ms);

public sealed record PreviewMeasurementResult(double Seconds, int PresentedFrames, double ExpectedFps,
	PreviewTiming Intervals, double LongestGapMs, int Stalls, int DecodeSkipped, int OverlayDropped, int DisplayDropped,
	PreviewTiming Decode, PreviewTiming Overlay, PreviewTiming Draw, double? DisplayRefreshHz)
{
	public double PresentedFps => PresentedFrames / Math.Max(Seconds, 0.001);
}

public sealed class PreviewMeasurements(double expectedFps, TimeProvider? timeProvider = null)
{
	private readonly Lock _gate = new();
	private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
	private readonly long _started = (timeProvider ?? TimeProvider.System).GetTimestamp();
	private long _lastPresented;
	private int _presented;
	private readonly List<double> _intervals = [];
	private readonly List<double>[] _stages = [[], [], []];
	private readonly List<double> _refreshIntervals = [];
	private readonly int[] _drops = new int[3];
	private PreviewMeasurementResult? _result;

	public void RecordStage(PreviewStage stage, long started)
	{
		lock (_gate)
		{
			if (_result is null) _stages[(int)stage].Add(_clock.GetElapsedTime(started).TotalMilliseconds);
		}
	}

	public void RecordDrop(PreviewDrop stage, int count = 1)
	{
		lock (_gate)
		{
			if (_result is null) _drops[(int)stage] += count;
		}
	}

	public void RecordRefresh(double seconds)
	{
		if (seconds is not (> 0.002 and < 0.05)) return;
		lock (_gate)
		{
			if (_result is null) _refreshIntervals.Add(seconds * 1000);
		}
	}

	public void RecordPresented()
	{
		lock (_gate)
		{
			if (_result is not null) return;
			long now = _clock.GetTimestamp();
			_intervals.Add(_clock.GetElapsedTime(_lastPresented == 0 ? _started : _lastPresented, now).TotalMilliseconds);
			_lastPresented = now;
			_presented++;
		}
	}

	public PreviewMeasurementResult Finish()
	{
		lock (_gate)
		{
			if (_result is not null) return _result;
			long now = _clock.GetTimestamp();
			double tail = _clock.GetElapsedTime(_lastPresented == 0 ? _started : _lastPresented, now).TotalMilliseconds;
			double stallMs = Math.Max(100, 3000 / Math.Max(expectedFps, 1));
			double longest = Math.Max(tail, _intervals.Count > 0 ? _intervals.Max() : 0);
			return _result = new PreviewMeasurementResult(_clock.GetElapsedTime(_started, now).TotalSeconds,
				_presented, expectedFps, Timing(_intervals), longest, _intervals.Count(ms => ms > stallMs) + (tail > stallMs ? 1 : 0),
				_drops[0], _drops[1], _drops[2], Timing(_stages[0]), Timing(_stages[1]), Timing(_stages[2]),
				_refreshIntervals.Count > 0 ? 1000 / Timing(_refreshIntervals).MedianMs : null);
		}
	}

	private static PreviewTiming Timing(List<double> samples)
	{
		if (samples.Count == 0) return new PreviewTiming(0, 0, 0);
		double[] ordered = samples.Order().ToArray();
		double median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
		return new PreviewTiming(ordered.Length, median, ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1]);
	}
}
