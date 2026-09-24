using System.Diagnostics;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Where playback is, in seconds on the play timeline (the kept stretches back to back, from 0). While sound plays
///     it is the audio device's position - video follows the sound, the way players keep A/V in sync (audio can't be
///     sped up or slowed down, video frames can be held or dropped) - and a stopwatch otherwise: without an audio
///     track or device, and once the audio has run out. Now is read by the pacing loop only; the audio feeder reports
///     pushed samples from its own thread.
/// </summary>
internal sealed class PlaybackClock(AudioOutput? audio, int sampleRate)
{
	private readonly Stopwatch _stopwatch = new();
	private double _stopwatchBase;
	private long _pushedFrames;
	private volatile bool _audioFinished;

	public bool FollowsAudio { get; private set; } = audio is not null;

	public double Now
	{
		get
		{
			if (FollowsAudio)
			{
				var queued = audio!.QueuedSeconds;
				var played = Interlocked.Read(ref _pushedFrames) / (double)sampleRate - queued - audio.DeviceLatencySeconds;
				if (!_audioFinished || queued > 0) return Math.Max(0, played);

				// The sound ran out (the track ends before the video, or failed): carry on from here on the stopwatch.
				FollowsAudio = false;
				_stopwatchBase = Math.Max(0, played);
				_stopwatch.Restart();
			}

			return _stopwatchBase + _stopwatch.Elapsed.TotalSeconds;
		}
	}

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
		if (FollowsAudio) audio!.Start();
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
		_stopwatch.Restart();
	}
}
