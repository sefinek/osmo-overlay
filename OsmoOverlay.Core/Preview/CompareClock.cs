using System.Diagnostics;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The one clock every tile of the comparison follows: a position on the videos' common timeline that runs at the
///     playback rate while started and stands still while paused. Videos are decoded against it, never against each
///     other, so none of them can drift away from the rest.
/// </summary>
internal sealed class CompareClock(Func<long>? timestamp = null)
{
	private readonly Func<long> _now = timestamp ?? Stopwatch.GetTimestamp;
	private readonly Lock _lock = new();
	private double _anchorSeconds;
	private long _anchorTicks;
	private double _rate = 1;
	private bool _running;

	/// <summary>Seconds on the common timeline.</summary>
	public double Position
	{
		get
		{
			lock (_lock) return PositionLocked();
		}
	}

	public bool IsRunning
	{
		get
		{
			lock (_lock) return _running;
		}
	}

	public double Rate
	{
		get
		{
			lock (_lock) return _rate;
		}
		set
		{
			lock (_lock)
			{
				Reanchor();
				_rate = value;
			}
		}
	}

	public void Start(double seconds)
	{
		lock (_lock)
		{
			_anchorSeconds = seconds;
			_anchorTicks = _now();
			_running = true;
		}
	}

	public void Pause()
	{
		lock (_lock)
		{
			Reanchor();
			_running = false;
		}
	}

	/// <summary>Moves the clock, whether it runs or not.</summary>
	public void Seek(double seconds)
	{
		lock (_lock)
		{
			_anchorSeconds = seconds;
			_anchorTicks = _now();
		}
	}

	private double PositionLocked()
	{
		return _running ? _anchorSeconds + (_now() - _anchorTicks) / (double)Stopwatch.Frequency * _rate : _anchorSeconds;
	}

	private void Reanchor()
	{
		_anchorSeconds = PositionLocked();
		_anchorTicks = _now();
	}
}
