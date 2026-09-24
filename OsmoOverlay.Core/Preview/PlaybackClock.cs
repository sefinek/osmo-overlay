using System.Diagnostics;

namespace OsmoOverlay.Core.Preview;

/// <summary>What PlaybackClock needs from the audio device - AudioOutput, or a fake in tests.</summary>
internal interface IAudioClockSource
{
	/// <summary>Audio pushed but not yet handed to the device.</summary>
	double QueuedSeconds { get; }

	/// <summary>The device's own buffer, played after what's still queued - also how much it takes at a time.</summary>
	double DeviceLatencySeconds { get; }

	void Start();
}

/// <summary>
///     Where playback is, in seconds on the play timeline (the kept stretches back to back, from 0).
///     While sound plays it follows the audio device - video follows the sound, the way players keep A/V in sync
///     (audio can't be sped up or slowed down, video frames can be held or dropped). The device takes the queued
///     sound a whole buffer at a time, so its position alone moves in steps of that buffer (~10 ms) - frames timed
///     against it would be shown 10 or 20 ms apart instead of an even 16.7. Now therefore runs on the wall clock
///     and is pulled toward the device's position a little at every reading (the position lags the sound actually
///     heard by 0 to one buffer, half of one on average), jumping to it only when they're far apart (a stalled or
///     restarted device). At a speed other than 1x the pushed sound is already tempo-changed (AudioTempo), so a
///     second of it is `rate` seconds of the play timeline.
///     Without an audio track or device, and once the audio has run out, a stopwatch running `rate` times real time.
///     Read from the presentation (Now), the decode threads (AudioPosition) and the audio feeder (AddPushed,
///     RestartAudio) at once, so every member takes the one lock.
/// </summary>
internal sealed class PlaybackClock
{
	// Further apart than this, the smoothed clock jumps to the device's position instead of easing toward it.
	private const double ResyncSeconds = 0.05;

	// The share of the gap to the device's position closed at each reading - about a third of a second to settle
	// when read every display frame.
	private const double SlewFactor = 0.05;

	private readonly Lock _lock = new();
	private readonly IAudioClockSource? _audio;
	private readonly int _sampleRate;
	private readonly Func<double> _wallSeconds;
	// The tempo the pushed sound was made at, and the speed asked for - they differ from a SetRate until the feeder
	// restarts the sound at the new speed (RestartAudio).
	private double _audioRate;
	private double _rate;
	private bool _followsAudio;
	private bool _audioFinished;
	// Where on the play timeline the pushed sound starts, and how much of it went out.
	private double _audioBase;
	private long _pushedFrames;
	private double _stopwatchBase;
	private double _stopwatchStart;
	private bool _smoothing;
	private double _smoothBase;
	private double _smoothWall;
	private double _lastNow;

	public PlaybackClock(IAudioClockSource? audio, int sampleRate, double rate) : this(audio, sampleRate, rate, WallClock())
	{
	}

	/// <param name="wallSeconds">A monotonic clock in seconds - the stopwatch and the smoothing read it (tests pass a fake).</param>
	internal PlaybackClock(IAudioClockSource? audio, int sampleRate, double rate, Func<double> wallSeconds)
	{
		_audio = audio;
		_sampleRate = sampleRate;
		_audioRate = rate;
		_rate = rate;
		_wallSeconds = wallSeconds;
		_followsAudio = audio is not null;
	}

	public bool FollowsAudio
	{
		get
		{
			lock (_lock) return _followsAudio;
		}
	}

	/// <summary>Never goes backwards, except when Rebase/RestartAudio put it somewhere on purpose.</summary>
	public double Now
	{
		get
		{
			lock (_lock)
			{
				var wall = _wallSeconds();
				double now;
				if (_followsAudio)
				{
					var device = DevicePosition(out var queued);
					now = Smooth(device, wall);
					if (_audioFinished && queued <= 0)
					{
						// The sound ran out (the track ends before the video, or failed): carry on from here on the stopwatch.
						_followsAudio = false;
						StartStopwatch(now, wall);
					}
				}
				else
				{
					now = _stopwatchBase + (wall - _stopwatchStart) * _rate;
				}

				_lastNow = Math.Max(_lastNow, Math.Max(0, now));
				return _lastNow;
			}
		}
	}

	/// <summary>
	///     The device's own position, for the decode threads to tell how far behind they run - null while the clock
	///     doesn't follow the sound (only the presentation times the stopwatch). Unsmoothed, and never switches the
	///     clock over, so reading it changes nothing.
	/// </summary>
	public double? AudioPosition
	{
		get
		{
			lock (_lock) return _followsAudio && !_audioFinished ? Math.Max(0, DevicePosition(out _)) : null;
		}
	}

	/// <summary>Sample frames of (tempo-changed) sound pushed to the device.</summary>
	public void AddPushed(int sampleFrames)
	{
		lock (_lock) _pushedFrames += sampleFrames;
	}

	public void AudioFinished()
	{
		lock (_lock) _audioFinished = true;
	}

	/// <summary>With the first frame: the device starts playing what's already queued, or the stopwatch starts.</summary>
	public void Start(double playTime)
	{
		lock (_lock)
		{
			_lastNow = playTime;
			if (_followsAudio)
			{
				_smoothing = false;
				_audio!.Start();
			}
			else
			{
				StartStopwatch(playTime, _wallSeconds());
			}
		}
	}

	/// <summary>
	///     Stopwatch only: restarts at a play time - a new stretch whose first frame took a while to decode shouldn't
	///     have that charged against the frames after it (they'd all be dropped as late). The audio clock needs no
	///     such help: the sound keeps its pace and the decode threads catch up with it.
	/// </summary>
	public void Rebase(double playTime)
	{
		lock (_lock)
		{
			if (_followsAudio) return;

			_lastNow = playTime;
			StartStopwatch(playTime, _wallSeconds());
		}
	}

	/// <summary>
	///     A new speed. The stopwatch changes pace right away from where it is; while following the sound, the pushed
	///     sound keeps its tempo until the feeder restarts it at the new one (RestartAudio) - the new speed also applies
	///     to the stopwatch if the sound runs out first.
	/// </summary>
	public void SetRate(double rate)
	{
		lock (_lock)
		{
			if (!_followsAudio)
			{
				var wall = _wallSeconds();
				StartStopwatch(_stopwatchBase + (wall - _stopwatchStart) * _rate, wall);
			}

			_rate = rate;
		}
	}

	/// <summary>The feeder dropped what was queued and pushes sound from this play time on, made at this tempo.</summary>
	public void RestartAudio(double playTime, double rate)
	{
		lock (_lock)
		{
			_audioBase = playTime;
			_audioRate = rate;
			_rate = rate;
			_pushedFrames = 0;
			_audioFinished = false;
			_smoothing = false;
			_lastNow = playTime;
		}
	}

	private double DevicePosition(out double queued)
	{
		queued = _audio!.QueuedSeconds;
		return _audioBase + (_pushedFrames / (double)_sampleRate - queued - _audio.DeviceLatencySeconds) * _audioRate;
	}

	private double Smooth(double device, double wall)
	{
		var target = device + _audio!.DeviceLatencySeconds / 2 * _audioRate;
		if (!_smoothing)
		{
			_smoothing = true;
			(_smoothBase, _smoothWall) = (target, wall);
			return target;
		}

		var predicted = _smoothBase + (wall - _smoothWall) * _audioRate;
		var error = target - predicted;
		_smoothBase = Math.Abs(error) > ResyncSeconds ? target : predicted + error * SlewFactor;
		_smoothWall = wall;
		return _smoothBase;
	}

	private void StartStopwatch(double playTime, double wall)
	{
		_stopwatchBase = playTime;
		_stopwatchStart = wall;
	}

	private static Func<double> WallClock()
	{
		var stopwatch = Stopwatch.StartNew();
		return () => stopwatch.Elapsed.TotalSeconds;
	}
}
