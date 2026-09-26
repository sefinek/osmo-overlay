using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Thumbnails for the timeline's filmstrip, one per second of the recording. Osmo keyframes come every second, so
///     each is a single keyframe decode (~15 ms at 4K) by a decoder of its own at thumbnail size, in the background.
///     Only what the timeline shows is generated (Request, newest request first); everything decoded stays cached
///     for the recording. Updated is raised from the background thread.
/// </summary>
public sealed class TimelineThumbnails : IDisposable
{
	private readonly LibavVideoSource _source;
	private readonly Lock _lock = new();
	private readonly Dictionary<int, byte[]> _thumbnails = [];
	private readonly SemaphoreSlim _wake = new(0);
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _worker;
	private Queue<int> _wanted = new();

	private TimelineThumbnails(LibavVideoSource source, int width, int height, int count)
	{
		_source = source;
		Width = width;
		Height = height;
		Count = count;
		_worker = Task.Run(RunAsync);
	}

	public int Width { get; }
	public int Height { get; }

	/// <summary>Slots, one per started second.</summary>
	public int Count { get; }

	public event Action? Updated;

	/// <summary>Blocking - opens the first file. Width and height should keep the recording's aspect ratio.</summary>
	public static TimelineThumbnails Open(IReadOnlyList<PlaybackSegment> segments, double fps, int width, int height)
	{
		var source = new LibavVideoSource(segments, fps, width, height);
		return new TimelineThumbnails(source, width, height, (int)Math.Ceiling(source.Duration.TotalSeconds));
	}

	/// <summary>BGRA, Width x Height - null until generated.</summary>
	public byte[]? TryGet(int slot)
	{
		lock (_lock)
		{
			return _thumbnails.GetValueOrDefault(slot);
		}
	}

	/// <summary>The generated slot nearest to this one, at most maxDistance away (the earlier one on a tie); null when none is.</summary>
	public int? NearestGenerated(int slot, int maxDistance)
	{
		lock (_lock)
		{
			if (_thumbnails.ContainsKey(slot)) return slot;

			for (var distance = 1; distance <= maxDistance; distance++)
			{
				if (_thumbnails.ContainsKey(slot - distance)) return slot - distance;
				if (_thumbnails.ContainsKey(slot + distance)) return slot + distance;
			}

			return null;
		}
	}

	/// <summary>Replaces what's waiting to be generated with these slots, in this order (the ones already done are skipped).</summary>
	public void Request(IEnumerable<int> slots)
	{
		lock (_lock)
		{
			_wanted = new Queue<int>(slots.Where(s => s >= 0 && s < Count && !_thumbnails.ContainsKey(s)).Distinct());
		}

		if (_wake.CurrentCount == 0) _wake.Release();
	}

	private async Task RunAsync()
	{
		CancellationToken ct = _cts.Token;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				await _wake.WaitAsync(ct);
				while (NextWanted() is { } slot)
				{
					VideoFrame? frame = _source.GetFrame(TimeSpan.FromSeconds(slot), SeekAccuracy.Keyframe, ct);
					if (frame is null) return;

					var copy = frame.Bgra.ToArray();
					_source.Recycle(frame);
					lock (_lock)
					{
						_thumbnails[slot] = copy;
					}

					Updated?.Invoke();
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
		{
			if (!ct.IsCancellationRequested) AppLogger.Warn(ex, "Timeline thumbnails stopped");
		}
	}

	private int? NextWanted()
	{
		lock (_lock)
		{
			while (_wanted.TryDequeue(out var slot))
				if (!_thumbnails.ContainsKey(slot))
					return slot;

			return null;
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			_worker.Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
		}

		_source.Dispose();
		_cts.Dispose();
		_wake.Dispose();
	}
}
