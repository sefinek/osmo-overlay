using System.Diagnostics;

namespace OsmoOverlay.Core.Preview;

/// <summary>What PlaybackClock needs from the audio device - AudioOutput, or a fake in tests.</summary>
internal interface IAudioClockSource
{
	/// <summary>Audio pushed but not yet handed to the device.</summary>
	double QueuedSeconds { get; }

	/// <summary>The device's own buffer, played after what's still queued.</summary>
	double DeviceLatencySeconds { get; }

	void Start();
}

/// <summary>
///     Where playback is, in seconds on the play timeline (the kept stretches back to back, from 0). While sound plays
///     it is the audio device's position - video follows the sound, the way players keep A/V in sync (audio can't be
///     sped up or slowed down, video frames can be held or dropped). At a speed other than 1x the pushed sound is
///     already tempo-changed (AudioTempo), so a second of it is `rate` seconds of the play timeline. Without an audio
///     track or device, and once the audio has run out, a stopwatch running `rate` times real time. Now is read by the
///     pacing loop only; the audio feeder reports pushed samples from its own thread.
/// </summary>
internal sealed class PlaybackClock
{
	private readonly IAudioClockSource? _audio;
	private readonly int _sampleRate;
	private readonly double _rate;
	private readonly Func<double> _wallSeconds;
	private double _stopwatchBase;
	private double _stopwatchStart;
	private long _pushedFrames;
	private volatile bool _audioFinished;

	public PlaybackClock(IAudioClockSource? audio, int sampleRate, double rate) : this(audio, sampleRate, rate, WallClock())
	{
	}

	/// <param name="wallSeconds">A monotonic clock in seconds - the stopwatch reads it (tests pass a fake).</param>
	internal PlaybackClock(IAudioClockSource? audio, int sampleRate, double rate, Func<double> wallSeconds)
	{
		_audio = audio;
		_sampleRate = sampleRate;
		_rate = rate;
		_wallSeconds = wallSeconds;
		FollowsAudio = audio is not null;
	}

	public bool FollowsAudio { get; private set; }

	public double Now
	{
		get
		{
			if (FollowsAudio)
			{
				var queued = _audio!.QueuedSeconds;
				var played = (Interlocked.Read(ref _pushedFrames) / (double)_sampleRate - queued - _audio.DeviceLatencySeconds) * _rate;
				if (!_audioFinished || queued > 0) return Math.Max(0, played);

				// The sound ran out (the track ends before the video, or failed): carry on from here on the stopwatch.
				FollowsAudio = false;
				Rebase(Math.Max(0, played));
			}

			return _stopwatchBase + (_wallSeconds() - _stopwatchStart) * _rate;
		}
	}

	/// <summary>Sample frames of (tempo-changed) sound pushed to the device.</summary>
	public void AddPushed(int sampleFrames)
	{
		Interlocked.Add(ref _pushedFrames, sampleFrames);
	}

	public void AudioFinished()
	{
		_audioFinished = true;
	}

	/// <summary>With the first frame: the device starts playing what's already queued, or the stopwatch starts.</summary>
	public void Start(double playTime)
	{
		if (FollowsAudio) _audio!.Start();
		else Rebase(playTime);
	}

	/// <summary>
	///     Stopwatch only: restarts at a play time - a new stretch whose first frame took a while to decode shouldn't
	///     have that charged against the frames after it (they'd be shown back to back to catch up). The audio clock
	///     needs no such help: the sound keeps its pace and late frames are dropped.
	/// </summary>
	public void Rebase(double playTime)
	{
		if (FollowsAudio) return;

		_stopwatchBase = playTime;
		_stopwatchStart = _wallSeconds();
	}

	private static Func<double> WallClock()
	{
		var stopwatch = Stopwatch.StartNew();
		return () => stopwatch.Elapsed.TotalSeconds;
	}
}
