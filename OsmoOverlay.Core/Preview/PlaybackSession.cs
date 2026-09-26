using System.Diagnostics;
using System.Threading.Channels;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     One playback, from Play to Pause or the end: a pipeline of its own threads feeding the presentation.
///     - Decoding (DecodeAsync) walks the plan's stretches on the preview's decoder, every FrameStep-th frame converted,
///     and once it falls behind the sound decodes past frames without converting them (CatchUpFrames).
///     - Composing (ComposeAsync) draws the overlay onto each decoded frame, dropping ones the sound has already passed
///     - on its own thread, so decoding the next frame and drawing the overlay on this one overlap.
///     - Feeding the sound (FeedAudioAsync) pushes the same stretches' audio ahead of the device.
///     - Presenting (TakeDueFrame) is driven by the display: the GUI's render thread asks once per refresh (through
///     PlaybackFrameSource) and gets the frame due by then. Nothing waits on a timer, and the UI thread isn't
///     involved, so frames land on the display's own refresh whatever the UI is busy with.
///     Small bounded channels join the stages, so each runs at most a few frames ahead and a Pause is immediate.
///     Frame buffers come from and go back to the preview's shared FrameBufferPool.
/// </summary>
internal sealed class PlaybackSession
{
	// A sped-up playback shows at most about this many frames a second (FrameStep).
	private const double MaxShownFps = 60;

	// Frames decoded ahead of the compose thread, and composed ahead of the display - enough to ride out a slow frame
	// (a seek into the next stretch, a GC pause), few enough that each full-size frame buffered doesn't add up.
	private const int DecodedQueueFrames = 2;
	private const int ComposedQueueFrames = 3;

	// Audio decoded ahead of the device - enough to ride out a slow read, short enough that a Pause is silent at once.
	private const double AudioQueueSeconds = 0.25;

	// Behind the sound by more than this (real time, whatever the speed), a frame isn't worth composing any more.
	internal const double LateFrameSeconds = 0.1;

	// A frame the decoder would convert later than this (real time) is skipped past instead: it still has the
	// conversion, the overlay and the queue ahead of it. Measured at 4x on 4K: allowing 50 ms put every shown frame
	// ~64 ms behind the sound, 0 puts them ~20 ms behind, at the same even ~50 frames a second.
	internal const double ConvertLateSeconds = 0;

	// How far ahead of the sound (real time) a decoder that fell behind skips to - past what the frames queued between
	// it and the display take to get there, so it doesn't land behind again right away.
	private const double CatchUpLeadSeconds = 0.15;

	// A frame later than this many display frames (or frames of the video, whichever is longer) is skipped, when the
	// next one is due too - see TakeDueFrame.
	private const double MaxLateIntervals = 1.5;

	private readonly LibavVideoSource _video;
	private readonly LibavAudioSource? _audioSource;
	private readonly AudioOutput? _audioOutput;
	private readonly OverlayCompositor _compositor;
	private readonly FrameBufferPool _pool;
	private readonly PlaybackPlan _plan;
	private readonly PlaybackClock _clock;
	private readonly Channel<DecodedFrame> _decoded =
		Channel.CreateBounded<DecodedFrame>(new BoundedChannelOptions(DecodedQueueFrames) { SingleReader = true, SingleWriter = true });
	// Read by the render thread, and drained by StopAsync on the UI thread when the playback stops.
	private readonly Channel<PlaybackFrame> _composed =
		Channel.CreateBounded<PlaybackFrame>(new BoundedChannelOptions(ComposedQueueFrames) { SingleWriter = true });
	private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly CancellationTokenSource _stop = new();
	private readonly Lock _audioRestartLock = new();
	private CancellationTokenSource? _audioStretchCts;
	private (double Rate, double PlayTime)? _audioRestart;
	private volatile int _step;
	// Rate, for the decode threads.
	private double _rate;
	private Task _workers = Task.CompletedTask;
	private bool _started;
	private string? _streamError;
	// The display's refresh interval (real time), measured between TakeDueFrame calls, which come once per refresh.
	private double _displayInterval = 1 / 60.0;
	private long _lastTake;
	// For Summary: what reached the display and what didn't, and how late the shown frames were (real time).
	private long _firstShown;
	private int _shownFrames;
	private int _skippedAtDisplay;
	private int _droppedLate;
	private int _decodedPast;
	private double _lateTotal;
	private double _lateMax;

	public PlaybackSession(LibavVideoSource video, LibavAudioSource? audioSource, AudioOutput? audioOutput, OverlayCompositor compositor,
		FrameBufferPool pool, PlaybackPlan plan, double rate)
	{
		_video = video;
		_audioSource = audioSource;
		_audioOutput = audioOutput;
		_compositor = compositor;
		_pool = pool;
		_plan = plan;
		_clock = new PlaybackClock(audioOutput, audioSource?.SampleRate ?? 1, rate);
		_rate = rate;
		_step = FrameStep(rate, video.Fps);
	}

	public double Rate => Volatile.Read(ref _rate);

	// The frame shown last, in ticks on the recording's timeline - written by the render thread, read by the UI thread.
	private long _lastPositionTicks = -1;

	/// <summary>Where the frame shown last is on the recording's timeline - null before the first one.</summary>
	public TimeSpan? LastPosition => Interlocked.Read(ref _lastPositionTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

	/// <summary>Where playback is: the frame shown last, or before the first one where it starts.</summary>
	public TimeSpan Position => LastPosition ?? (_plan.First.Count > 0 ? _plan.First[0].Start : TimeSpan.Zero);

	/// <summary>Completes once the display took every frame (the end of the plan), or a stage failed (see Error).</summary>
	public Task Finished => _finished.Task;

	/// <summary>Why the playback ended early, if it did.</summary>
	public string? Error => _composed.Reader.Completion.Exception?.GetBaseException().Message ?? _streamError;

	/// <summary>Called once, after the previous session's StopAsync finished - no two sessions share the decoder or the device.</summary>
	public void Start()
	{
		_audioOutput?.Stop();
		Task decoder = Task.Run(DecodeAsync);
		Task composer = Task.Run(ComposeAsync);
		Task feeder = _clock.FollowsAudio ? Task.Run(FeedAudioAsync) : Task.CompletedTask;
		_workers = Task.WhenAll(decoder, composer, feeder);
	}

	/// <summary>Stops every stage, puts back the frame buffers still in flight and silences the device.</summary>
	public async Task StopAsync()
	{
		_finished.TrySetResult();
		await _stop.CancelAsync();
		await _workers;

		while (_decoded.Reader.TryRead(out DecodedFrame decoded)) _pool.Return(decoded.Frame.Bgra);
		while (_composed.Reader.TryRead(out PlaybackFrame composed)) _pool.Return(composed.Composed.Bgra);
		_audioOutput?.Stop();
		_stop.Dispose();
	}

	/// <summary>
	///     The display's turn (the render thread, once per refresh): the next frame due by the middle of this refresh interval -
	///     null when none is due yet. One new frame per refresh: a frame that missed its refresh by a little is still
	///     shown, a refresh late, rather than skipped - taking only the newest frame due dropped a couple of frames a
	///     second wherever the video's frame rate and the refresh rate nearly match, or the display came round a few ms
	///     late. Frames are skipped only once playback really fell behind: later than MaxLateIntervals with the next one
	///     due as well. The first frame starts the clock (and the sound). The caller owns the buffer and hands it back to
	///     the pool.
	/// </summary>
	public ComposedPreviewFrame? TakeDueFrame()
	{
		MeasureDisplayInterval();

		ChannelReader<PlaybackFrame> reader = _composed.Reader;
		if (!reader.TryPeek(out PlaybackFrame next))
		{
			if (reader.Completion.IsCompleted) _finished.TrySetResult();
			return null;
		}

		if (!_started)
		{
			_clock.Start(next.PlayTime);
			_started = true;
		}

		var rate = Rate;
		var now = _clock.Now;
		var dueBy = now + _displayInterval / 2 * rate;
		var maxLate = MaxLateIntervals * Math.Max(_displayInterval * rate, _step / _video.Fps);
		PlaybackFrame? shown = null;
		while (reader.TryPeek(out next))
		{
			// A stretch whose first frame arrived late (its seek took a while) restarts the stopwatch at that frame,
			// rather than the frames after it all being dropped to catch up. Following the sound, the decode threads
			// catch up with it instead.
			if (next.StartsStretch && shown is null && (now - next.PlayTime) / rate > LateFrameSeconds && !_clock.FollowsAudio)
			{
				_clock.Rebase(next.PlayTime);
				now = next.PlayTime;
				dueBy = now + _displayInterval / 2 * rate;
			}

			if (next.PlayTime > dueBy) break;

			reader.TryRead(out next);
			if (shown is { } skipped)
			{
				_pool.Return(skipped.Composed.Bgra);
				_skippedAtDisplay++;
			}

			shown = next;
			if (now - next.PlayTime <= maxLate || !reader.TryPeek(out PlaybackFrame following) || following.PlayTime > dueBy) break;
		}

		if (shown is not { } due) return null;

		var late = (now - due.PlayTime) / rate;
		_lateTotal += late;
		_lateMax = Math.Max(_lateMax, late);
		if (_shownFrames++ == 0) _firstShown = Stopwatch.GetTimestamp();

		Interlocked.Exchange(ref _lastPositionTicks, due.Composed.Position.Ticks);
		return due.Composed;
	}

	/// <summary>One line for the log about how the playback went - what to look at when it didn't look smooth.</summary>
	public string Summary()
	{
		var seconds = _shownFrames > 0 ? Stopwatch.GetElapsedTime(_firstShown).TotalSeconds : 0;
		return $"Preview playback at {Rate:0.##}x: {_shownFrames} frames shown in {seconds:0.0} s ({_shownFrames / Math.Max(seconds, 1e-9):0.0} a second), " +
		       $"skipped at the display {_skippedAtDisplay}, dropped late {Volatile.Read(ref _droppedLate)}, " +
		       $"decoded past to catch up {Volatile.Read(ref _decodedPast)}, shown late by {_lateTotal / Math.Max(_shownFrames, 1) * 1000:0.0} ms " +
		       $"on average, {_lateMax * 1000:0.0} at most, display {1 / _displayInterval:0} Hz, clock {(_clock.FollowsAudio ? "audio" : "stopwatch")}";
	}

	private void MeasureDisplayInterval()
	{
		var now = Stopwatch.GetTimestamp();
		if (_lastTake != 0)
		{
			var interval = Stopwatch.GetElapsedTime(_lastTake, now).TotalSeconds;
			// A window that stopped rendering for a while (minimized) says nothing about the refresh rate.
			if (interval is > 0.002 and < 0.05) _displayInterval += (interval - _displayInterval) * 0.1;
		}

		_lastTake = now;
	}

	/// <summary>
	///     Applies right away: the decoder converts every FrameStep-th frame from the next one on, and the sound -
	///     tempo-changed without changing its pitch (AudioTempo) - restarts from where playback is at the new tempo, its
	///     queue dropped, while the picture carries on without a seek.
	/// </summary>
	public void SetRate(double rate)
	{
		Volatile.Write(ref _rate, rate);
		_step = FrameStep(rate, _video.Fps);
		_clock.SetRate(rate);
		if (!_clock.FollowsAudio) return;

		lock (_audioRestartLock)
		{
			_audioRestart = (rate, _clock.Now);
			_audioStretchCts?.Cancel();
		}
	}

	/// <summary>
	///     Frames decoded per frame shown: sped up, only every Nth is converted and shown, so the screen stays at the
	///     source's frame rate, at most ~60 a second, instead of asking for 240 converted frames a second at 4x.
	/// </summary>
	internal static int FrameStep(double rate, double fps)
	{
		return Math.Max(1, (int)Math.Ceiling(rate * fps / MaxShownFps - 1e-9));
	}

	/// <summary>
	///     Frames to decode past without converting them before the next conversion - none while the frame it would
	///     convert can still be shown on time. With the sound as the clock a late frame is dropped anyway, so converting
	///     one (the 4K download from the GPU alone is 7-8 ms) only put the decoder further behind: a decoder that
	///     converted every frame could never catch up, the picture froze while the sound played on, and at 4x one that
	///     skipped only part of the way converted frames the compose thread then dropped - hundreds a minute.
	///     Lateness and the lead it aims for are real time: at 4x a tenth of a second of play time goes by in 25 ms.
	///     The decoder asks again after each skip, so the skip goes in batches, in proportion to how far behind: another
	///     `step` frames per LateFrameSeconds of lateness, up to two seconds of frames. A decoder that can't keep up at
	///     all (4K at 4x asks for 240 decoded frames a second) then shows fewer frames, each on time, evenly spaced.
	/// </summary>
	/// <param name="convertPlayTime">Where the frame the next read converts sits on the play timeline.</param>
	/// <param name="step">Frames the read decodes anyway (FrameStep).</param>
	internal static int CatchUpFrames(double? audioPosition, double convertPlayTime, double fps, double rate, int step)
	{
		if (audioPosition is not { } now) return 0;

		var late = now - convertPlayTime;
		var lateSeconds = late / rate;
		if (lateSeconds <= ConvertLateSeconds) return 0;

		var needed = Math.Ceiling((late + CatchUpLeadSeconds * rate) * fps);
		var perRead = Math.Min(step * Math.Ceiling(lateSeconds / LateFrameSeconds), Math.Ceiling(fps * 2));
		return (int)Math.Min(needed, perRead);
	}

	/// <summary>Plays the stretches one after another (endlessly while looping), one playback stream each.</summary>
	private async Task DecodeAsync()
	{
		ChannelWriter<DecodedFrame> writer = _decoded.Writer;
		CancellationToken ct = _stop.Token;
		var fps = _video.Fps;
		TimeSpan halfFrame = TimeSpan.FromSeconds(0.5 / fps);
		// Where each stretch starts on the play timeline - the stretches back to back, the way the sound is pushed.
		double playOffset = 0;
		try
		{
			foreach (PlaybackStretch stretch in _plan.Stretches())
			{
				LibavVideoSource.PlaybackStream stream = _video.OpenPlaybackStream(stretch.Start, ct);
				var playStart = playOffset;
				playOffset += stretch.Seconds;
				var startsStretch = true;
				while (!ct.IsCancellationRequested)
				{
					var step = _step;
					// The first frame of a stretch is where the sound resumes too - only later ones can fall behind it.
					if (!startsStretch)
					{
						var convertPlayTime = playStart + (stream.NextPosition - stretch.Start).TotalSeconds + (step - 1) / fps;
						var skip = CatchUpFrames(_clock.AudioPosition, convertPlayTime, fps, Rate, step);
						if (skip > 0)
						{
							if (!stream.Skip(skip)) break;

							Interlocked.Add(ref _decodedPast, skip);
							if (stream.NextPosition >= stretch.End - halfFrame) break;

							continue;
						}
					}

					VideoFrame? frame = stream.TryReadNextFrame(step);
					if (frame is null) break;

					if (stream.Position >= stretch.End - halfFrame)
					{
						_pool.Return(frame.Bgra);
						break;
					}

					var playTime = playStart + (stream.Position - stretch.Start).TotalSeconds;
					await WriteOrRecycleAsync(writer, new DecodedFrame(frame, stream.Position, playTime, startsStretch), frame.Bgra, ct);
					startsStretch = false;
				}

				if (stream.Error is { } error)
				{
					_streamError = error;
					break;
				}
			}

			writer.TryComplete();
		}
		catch (OperationCanceledException)
		{
			writer.TryComplete();
		}
		catch (Exception ex)
		{
			writer.TryComplete(ex);
		}
	}

	/// <summary>The overlay onto each decoded frame, in place; frames the sound has already passed are dropped uncomposed.</summary>
	private async Task ComposeAsync()
	{
		ChannelWriter<PlaybackFrame> writer = _composed.Writer;
		CancellationToken ct = _stop.Token;
		try
		{
			await foreach (DecodedFrame decoded in _decoded.Reader.ReadAllAsync(ct))
			{
				if (_clock.AudioPosition is { } audio && (audio - decoded.PlayTime) / Rate > LateFrameSeconds)
				{
					_pool.Return(decoded.Frame.Bgra);
					Interlocked.Increment(ref _droppedLate);
					continue;
				}

				if (_compositor.ComposeInPlace(decoded.Frame, decoded.Position) is not { } composed) break;

				await WriteOrRecycleAsync(writer, new PlaybackFrame(composed, decoded.PlayTime, decoded.StartsStretch), composed.Bgra, ct);
			}

			writer.TryComplete();
		}
		catch (OperationCanceledException)
		{
			writer.TryComplete();
		}
		catch (Exception ex)
		{
			writer.TryComplete(ex);
		}
	}

	private async Task WriteOrRecycleAsync<T>(ChannelWriter<T> writer, T item, byte[] buffer, CancellationToken ct)
	{
		try
		{
			await writer.WriteAsync(item, ct);
		}
		catch (OperationCanceledException)
		{
			_pool.Return(buffer);
			throw;
		}
	}

	/// <summary>
	///     Pushes the stretches' sound back to back, AudioQueueSeconds ahead of the device - the same joins the render
	///     makes. A speed change (SetRate) interrupts it: the queue is dropped and the sound restarts from the play time
	///     the change came at, at the new tempo. Tells the clock how much went out, and when it's done (or failed, which
	///     hands the clock to its stopwatch).
	/// </summary>
	private async Task FeedAudioAsync()
	{
		LibavAudioSource source = _audioSource!;
		AudioOutput output = _audioOutput!;
		IEnumerable<PlaybackStretch> stretches = _plan.Stretches();
		var rate = Rate;
		try
		{
			while (true)
			{
				using var stretchCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
				lock (_audioRestartLock)
				{
					_audioStretchCts = stretchCts;
					// A change that came in between is applied right away.
					if (_audioRestart is not null) stretchCts.Cancel();
				}

				try
				{
					await FeedAsync(source, output, stretches, rate, stretchCts.Token);
					return;
				}
				catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
				{
					double playTime;
					lock (_audioRestartLock)
					{
						(rate, playTime) = _audioRestart!.Value;
						_audioRestart = null;
					}

					output.Clear();
					_clock.RestartAudio(playTime, rate);
					stretches = _plan.StretchesFrom(playTime);
				}
				finally
				{
					// Before it's disposed - SetRate must not cancel a source that's gone.
					lock (_audioRestartLock)
					{
						_audioStretchCts = null;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (InvalidOperationException ex)
		{
			AppLogger.Warn(ex, "Preview audio stopped");
		}
		finally
		{
			_clock.AudioFinished();
		}
	}

	private async Task FeedAsync(LibavAudioSource source, AudioOutput output, IEnumerable<PlaybackStretch> stretches, double rate,
		CancellationToken ct)
	{
		using AudioTempo? tempo = rate != 1 ? new AudioTempo(rate, source.SampleRate, source.Channels) : null;
		foreach (PlaybackStretch stretch in stretches)
		{
			source.Seek(stretch.Start.TotalSeconds);
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				while (output.QueuedSeconds > AudioQueueSeconds) await Task.Delay(10, ct);

				ReadOnlySpan<float> samples = source.Read(stretch.End.TotalSeconds);
				if (samples.IsEmpty) break;

				if (tempo is not null) samples = tempo.Process(samples);
				output.Push(samples);
				_clock.AddPushed(samples.Length / source.Channels);
			}
		}
	}

	private readonly record struct DecodedFrame(VideoFrame Frame, TimeSpan Position, double PlayTime, bool StartsStretch);

	/// <summary>PlayTime: seconds on the play timeline (PlaybackClock) - the stretches back to back, from 0.</summary>
	private readonly record struct PlaybackFrame(ComposedPreviewFrame Composed, double PlayTime, bool StartsStretch);
}
