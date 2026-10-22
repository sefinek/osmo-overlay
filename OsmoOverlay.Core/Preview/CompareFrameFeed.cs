namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The decoded frames of one tile waiting for their time. The display takes the frame due by the comparison's clock
///     (TakeDueFrame, on the render thread) and skips the ones the clock has already passed, so a tile that decodes a
///     frame late shows the right picture for the time rather than falling behind its neighbours.
/// </summary>
public sealed class CompareFrameFeed : IPlayedFrames
{
	private readonly Func<double> _clockSeconds;
	private readonly Lock _lock = new();
	private readonly Queue<ComposedPreviewFrame> _waiting = new();
	private volatile bool _playing;

	internal CompareFrameFeed(Func<double> clockSeconds)
	{
		_clockSeconds = clockSeconds;
	}

	public bool IsPlaying => _playing;

	public PreviewMeasurements? Measurements => null;

	/// <summary>Frames decoded and not yet taken.</summary>
	internal int Count
	{
		get
		{
			lock (_lock) return _waiting.Count;
		}
	}

	internal void SetPlaying(bool playing)
	{
		_playing = playing;
	}

	/// <summary>The decoder's side: frames come in order of their position.</summary>
	internal void Enqueue(ComposedPreviewFrame frame)
	{
		lock (_lock) _waiting.Enqueue(frame);
	}

	/// <summary>Drops every waiting frame - a seek or a stop - and hands its buffers back.</summary>
	internal void Clear()
	{
		lock (_lock)
		{
			while (_waiting.TryDequeue(out ComposedPreviewFrame? frame)) PlaybackFrameSource.Release(frame);
		}
	}

	public ComposedPreviewFrame? TakeDueFrame()
	{
		double now = _clockSeconds();
		ComposedPreviewFrame? due = null;
		lock (_lock)
		{
			while (_waiting.TryPeek(out ComposedPreviewFrame? next) && next.Position.TotalSeconds <= now + 1e-6)
			{
				if (due is not null) PlaybackFrameSource.Release(due);
				due = _waiting.Dequeue();
			}
		}

		return due;
	}
}
