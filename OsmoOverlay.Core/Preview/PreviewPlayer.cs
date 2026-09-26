using System.Runtime.InteropServices;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     A preview frame with the overlay, BGRA (Width * 4 bytes a row) at the preview size. Its buffer belongs to a
///     pool: from FrameReady it's only valid during the callback, from PlaybackFrameSource until handed back.
/// </summary>
public sealed record ComposedPreviewFrame(TimeSpan Position, byte[] Bgra, int Width, int Height)
{
	internal FrameBufferPool? Owner { get; init; }
}

/// <summary>
///     The live preview: the open recording (its decoders and sound, and the overlay - OverlayCompositor), seeking
///     (latest wins) and playback (PlaybackSession). Public members are called from the UI thread, and FrameReady,
///     PlaybackStarted and PlaybackStopped are raised on it; Message can come from any thread. FrameReady brings the
///     stills (paused, seeking); played frames the GUI takes itself from Frames on its render thread, once per display
///     refresh - playback is paced by the display (see PlaybackSession).
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
	// Frame buffers in flight at most: the decoder's back-step cache (up to 15), the playback pipeline, the still and
	// the frame being shown. The pool only keeps what came back, so this is a ceiling, not an allocation.
	private const int PooledFrames = 32;

	private OpenRecording? _recording;
	private PlaybackSession? _session;
	// Every stopped session's StopAsync - a new one starts only after them, so two never share the decoder or the
	// device (the old one's final AudioOutput.Stop would otherwise clear the new one's sound).
	private Task _sessionsStopped = Task.CompletedTask;
	// Bumped by every OpenAsync/Close - an open overtaken while it awaited drops what it opened.
	private int _openGeneration;
	private bool _resumeAfterScrub;
	private CancellationTokenSource? _mapPrepareCts;
	private CancellationTokenSource? _routeIntroPrepareCts;
	// Seeks, latest wins (see RequestSeek): only the newest request waits, the one decoding finishes.
	private (TimeSpan Position, SeekAccuracy Accuracy)? _pendingSeek;
	private bool _seekLoopRunning;
	// Bumped by Play/Close - a seek decoded before that is dropped instead of shown.
	private int _seekGeneration;
	private CancellationTokenSource _seekCts = new();
	// Kept across Close/OpenAsync - the GUI reopens the preview for some settings changes, and these are its view state.
	private OutputTimeline? _outputTimeline;
	private bool _showOverlay = true;
	private float _audioGain;

	public bool IsPlaying => _session is not null;
	public bool HasAudio => _recording?.AudioOutput is not null;

	/// <summary>1 = real time. Other speeds keep the sound, tempo-changed without changing its pitch (AudioTempo).</summary>
	public double PlaybackRate { get; private set; } = 1;

	/// <summary>Playback starts over at the end - of LoopRange when set, else of the recording (see SetLoop).</summary>
	public bool Loop { get; private set; }

	public TimeRange? LoopRange { get; private set; }
	public TimeSpan Duration => _recording?.Video.Duration ?? TimeSpan.Zero;

	/// <summary>
	///     Frames a second playback shows at the current speed when it keeps up: the recording's rate times the speed,
	///     only every FrameStep-th frame once that passes ~60. 0 with no recording open.
	/// </summary>
	public double PlaybackFrameRate =>
		_recording is { } recording ? PlaybackRate * recording.Fps / PlaybackSession.FrameStep(PlaybackRate, recording.Fps) : 0;

	/// <summary>Played frames, for the GUI's render thread - see PlaybackFrameSource.</summary>
	public PlaybackFrameSource Frames { get; } = new();

	public void Dispose()
	{
		Close();
	}

	/// <summary>The frame's Bgra buffer is only valid for the duration of the callback - it's reused for a later frame right after, so copy out of it, don't keep it.</summary>
	public event Action<ComposedPreviewFrame>? FrameReady;

	/// <summary>Playback began - from here on the GUI takes played frames from Frames until it stops.</summary>
	public event Action? PlaybackStarted;

	public event Action? PlaybackStopped;
	public event Action<string>? Message;

	public async Task OpenAsync(FileSummary summary, int previewWidth, int previewHeight)
	{
		Close();
		var generation = _openGeneration;

		if (summary.TelemetryFrames is not { Count: > 0 } rawFrames)
			throw new InvalidOperationException("File has no telemetry to preview.");

		// Recomputed rather than taken from summary.DerivedFrames, so a changed SmoothGpsMotion applies on every open.
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<DerivedFrame> recordingFrames = TelemetryProcessor.Process(rawFrames, settings.SmoothGpsMotion);
		var availability = new OverlayAvailability(TelemetryProcessor.HasAnyGpsFix(rawFrames),
			TelemetryProcessor.HasAnyGpsTimestamp(rawFrames), summary.ContainerRecordingStartUtc is not null);
		List<PlaybackSegment> segments = PlaybackSegment.Of(summary);
		var pool = new FrameBufferPool(PooledFrames);

		// The opens block; awaiting them through Task.Run keeps this method - and the FrameReady it raises - on the UI thread.
		LibavVideoSource video = await Task.Run(() => new LibavVideoSource(segments, summary.Video.Fps, previewWidth, previewHeight, pool));
		(LibavAudioSource? audioSource, AudioOutput? audioOutput) = await Task.Run(() => OpenAudio(segments));
		if (generation != _openGeneration)
		{
			video.Dispose();
			audioOutput?.Dispose();
			audioSource?.Dispose();
			return;
		}

		AppLogger.Info($"Preview decoder: {video.DecoderDescription}");
		audioOutput?.SetGain(_audioGain);

		var (width, height) = (summary.Video.Width, summary.Video.Height);
		(List<OverlayPreset> presets, var activeId) = OverlayPresetStore.Load(width, height);
		IReadOnlyList<OverlayElement> layout = availability.Apply(presets.First(p => p.Id == activeId).Elements);
		RouteIntroSettings routeIntro = RouteIntroSettings.ForRecording(settings, availability.GpsFix);
		var compositor = new OverlayCompositor(frames => new OverlayRenderer(width, height, frames[0].Raw.AltitudeMeters, layout, frames,
				TelemetryProcessor.Summarize(frames).MaxSpeedKmh, settings.ShowWatermark, summary.CameraModel,
				summary.ContainerRecordingStartUtc, settings.MapTileUrlTemplate, settings.MapAttribution, settings.MapShowAttribution,
				settings.MapApiKey, routeIntro) { RouteAcrossCuts = settings.RouteAcrossCuts },
			recordingFrames, summary.TotalFrameCount / summary.Video.Fps, availability, _outputTimeline, _showOverlay, pool);
		var recording = new OpenRecording(video, audioSource, audioOutput, pool, compositor, segments, width, height, summary.Video.Fps,
			routeIntro.Enabled);
		_recording = recording;

		ComposedPreviewFrame? first;
		try
		{
			first = await Task.Run(() => DecodeStill(recording, TimeSpan.Zero, SeekAccuracy.Exact, CancellationToken.None));
		}
		catch (ObjectDisposedException) when (generation != _openGeneration)
		{
			// Closed while the first frame decoded.
			return;
		}

		PublishStill(recording, first);

		// Map and route-intro tiles are fetched in the background, not awaited here: a slow or unreachable tile server
		// would otherwise hold up even the first frame. The widgets draw their placeholder until the tiles land.
		if (layout.Any(e => e is MapWidgetElement { Visible: true })) StartMapWidgetPreparation(recording);
		StartRouteIntroPreparation(recording);
	}

	public void Close()
	{
		_openGeneration++;
		StopSession(false, false);
		CancelSeeks();
		_mapPrepareCts?.Cancel();
		_mapPrepareCts = null;
		_routeIntroPrepareCts?.Cancel();
		_routeIntroPrepareCts = null;
		_resumeAfterScrub = false;

		if (_recording is not { } recording) return;

		_recording = null;
		_ = DisposeWhenStoppedAsync(recording, _sessionsStopped);
	}

	/// <summary>Off the UI thread, and only once playback let go of it: disposing the decoder waits for a decode still running.</summary>
	private static async Task DisposeWhenStoppedAsync(OpenRecording recording, Task sessionsStopped)
	{
		try
		{
			await sessionsStopped;
			await Task.Run(recording.Dispose);
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "Closing the preview failed");
		}
	}

	/// <summary>0-1 on a perceptual (squared) curve, so the slider's middle sounds like half as loud; applies right away, also mid-playback.</summary>
	public void SetAudioVolume(double volume, bool muted)
	{
		_audioGain = muted ? 0 : (float)(Math.Clamp(volume, 0, 1) * Math.Clamp(volume, 0, 1));
		_recording?.AudioOutput?.SetGain(_audioGain);
	}

	/// <summary>The audio track and the playback device, or (null, null) - the preview then plays without sound.</summary>
	private static (LibavAudioSource?, AudioOutput?) OpenAudio(IReadOnlyList<PlaybackSegment> segments)
	{
		LibavAudioSource? source;
		try
		{
			source = LibavAudioSource.TryOpen(segments);
		}
		catch (InvalidOperationException ex)
		{
			AppLogger.Warn(ex, "Preview audio unavailable - the audio track couldn't be opened");
			return (null, null);
		}

		if (source is null) return (null, null);

		AudioOutput? output = AudioOutput.TryOpen(source.SampleRate, source.Channels);
		if (output is not null) return (source, output);

		source.Dispose();
		return (null, null);
	}

	public void TogglePlayPause(TimeSpan currentPosition)
	{
		_resumeAfterScrub = false;
		if (IsPlaying) Pause();
		else Play(currentPosition);
	}

	public void Play(TimeSpan fromPosition)
	{
		if (_recording is not { } recording) return;

		StopSession(false, false);
		CancelSeeks();
		// The still is from before playback - it'd be the wrong frame to put an edit on until the stop decodes the
		// frame on screen again (StopSession).
		recording.Compositor.ClearStill();

		PlaybackPlan plan = PlaybackPlan.For(_outputTimeline, fromPosition, recording.Video.Duration, Loop, LoopRange);
		var session = new PlaybackSession(recording.Video, recording.AudioSource, recording.AudioOutput, recording.Compositor,
			recording.Pool, plan, PlaybackRate);
		_session = session;
		Frames.Attach(session);
		_ = StartWhenFreeAsync(session, _sessionsStopped);
		_ = StopWhenFinishedAsync(session);
		PlaybackStarted?.Invoke();
	}

	/// <summary>Ends the playback once the display took its last frame, or a stage failed.</summary>
	private async Task StopWhenFinishedAsync(PlaybackSession session)
	{
		await session.Finished;
		if (!ReferenceEquals(_session, session)) return;

		if (session.Error is { } error) Message?.Invoke($"Playback stopped: {error}");
		StopSession(true);
	}

	private async Task StartWhenFreeAsync(PlaybackSession session, Task sessionsStopped)
	{
		await sessionsStopped;
		if (ReferenceEquals(_session, session)) session.Start();
	}

	public void Pause()
	{
		StopSession(true);
	}

	/// <summary>
	///     Ends the running playback, if there is one. Its frames had the overlay drawn onto them, so the frame on screen
	///     is decoded again as a clean still (restoreStill) - what layout edits and the rest put the overlay on anew.
	/// </summary>
	private void StopSession(bool raiseStopped, bool restoreStill = true)
	{
		if (_session is not { } session) return;

		_session = null;
		Frames.Attach(null);
		if (session.LastPosition is not null) AppLogger.Info(session.Summary());
		_sessionsStopped = Task.WhenAll(_sessionsStopped, session.StopAsync());
		if (raiseStopped) PlaybackStopped?.Invoke();
		if (restoreStill && session.LastPosition is { } position) RequestSeek(position);
	}

	/// <summary>Applies to a running playback right away (see PlaybackSession.SetRate), else to the next Play.</summary>
	public void SetPlaybackRate(double rate)
	{
		if (rate <= 0 || rate == PlaybackRate) return;

		PlaybackRate = rate;
		_session?.SetRate(rate);
	}

	/// <summary>
	///     Loops playback over a range of the recording (the In/Out selection) or, with none, the whole of it - cut-out
	///     parts are skipped as always. A running playback carries on under the new plan from where it is.
	/// </summary>
	public void SetLoop(bool loop, TimeRange? range)
	{
		if (loop == Loop && range == LoopRange) return;

		// A new range only matters to a playback that loops (or did until now).
		var affectsPlayback = loop || Loop;
		Loop = loop;
		LoopRange = range;
		if (affectsPlayback && _session is { } session) Play(session.Position);
	}

	public void BeginScrubDrag()
	{
		if (!IsPlaying) return;

		_resumeAfterScrub = true;
		StopSession(false, false);
	}

	/// <summary>Resumes playback if the drag interrupted it, otherwise replaces the drag's keyframes with the exact frame.</summary>
	public void EndScrubDrag(TimeSpan currentPosition)
	{
		if (!_resumeAfterScrub)
		{
			RequestSeek(currentPosition);
			return;
		}

		_resumeAfterScrub = false;
		if (!IsPlaying) Play(currentPosition);
	}

	/// <summary>
	///     Shows the preview the way a cut render will come out (see OverlayCompositor): cut-out moments show the plain
	///     video when scrubbed to and are skipped by playback. Null = the whole recording. Playback is paused first -
	///     it plays the stretches it started with.
	/// </summary>
	public void SetOutputTimeline(OutputTimeline? timeline)
	{
		Pause();
		_outputTimeline = timeline;
		if (_recording is not { } recording) return;

		PublishStill(recording, recording.Compositor.SetTimeline(timeline));
		// The map widget's mosaic stays unless the route now reaches past it; the route intro frames the whole route.
		if (!recording.Compositor.Renderer.MapMosaicCoversRoute) StartMapWidgetPreparation(recording);
		StartRouteIntroPreparation(recording);
	}

	/// <summary>
	///     Swaps the live layout (drag/visibility edits from the GUI) and puts the overlay on the still again - no
	///     decoding, so dragging stays smooth. The same availability filter as the open: every live edit re-sends the
	///     raw preset layout, and a widget this file can't feed must not come back with it.
	/// </summary>
	public void SetLayout(IReadOnlyList<OverlayElement> layout)
	{
		if (_recording is not { } recording) return;

		layout = recording.Compositor.Filter(layout);
		PublishStill(recording, recording.Compositor.Change(r => r.Layout = layout));
		if (recording.Compositor.Renderer.NeedsMapPrepare(layout)) StartMapWidgetPreparation(recording);
	}

	/// <summary>Lets Settings toggle the watermark live without reopening the file.</summary>
	public void SetShowWatermark(bool show)
	{
		if (_recording is { } recording) PublishStill(recording, recording.Compositor.Change(r => r.ShowWatermark = show));
	}

	/// <summary>How the drawn routes cross a cut - applies to the frame on screen right away.</summary>
	public void SetRouteAcrossCuts(RouteJoin join)
	{
		if (_recording is { } recording) PublishStill(recording, recording.Compositor.Change(r => r.RouteAcrossCuts = join));
	}

	/// <summary>Shows the plain video (false) or the video with the overlay - on the frame on screen right away.</summary>
	public void SetShowOverlay(bool show)
	{
		_showOverlay = show;
		if (_recording is { } recording) PublishStill(recording, recording.Compositor.SetShowOverlay(show));
	}

	/// <summary>
	///     The frame at a position as a PNG at the recording's full resolution, with the overlay exactly as the render
	///     draws it there - none where the position is cut out or while the overlay is hidden. Decoded by a decoder of
	///     its own at full size, so the running preview isn't disturbed.
	/// </summary>
	public async Task<byte[]> RenderSnapshotPngAsync(TimeSpan position)
	{
		if (_recording is not { } recording) throw new InvalidOperationException("No recording is open.");

		return await Task.Run(() =>
		{
			using var source = new LibavVideoSource(recording.Segments, recording.Fps, recording.Width, recording.Height);
			VideoFrame frame = source.GetFrame(position, SeekAccuracy.Exact, CancellationToken.None)
			                   ?? throw new InvalidOperationException("The frame couldn't be decoded.");

			recording.Compositor.DrawOverlayOnto(frame.Bgra, frame.Width, frame.Height, position);
			return EncodePng(frame.Bgra, frame.Width, frame.Height);
		});
	}

	private static byte[] EncodePng(byte[] bgra, int width, int height)
	{
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
		GCHandle pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
		try
		{
			using var pixmap = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
			using SKData png = pixmap.Encode(SKEncodedImageFormat.Png, 100)
			                   ?? throw new InvalidOperationException("The frame couldn't be encoded as PNG.");
			return png.ToArray();
		}
		finally
		{
			pin.Free();
		}
	}

	/// <summary>
	///     Shows the frame at a position. Latest wins, with no debounce: an idle player starts decoding right away,
	///     and while a decode runs only the newest request waits for it - so a timeline drag shows frames as fast as
	///     they decode (Keyframe while dragging: ~15-20 ms each in-process), and frame stepping never waits on a
	///     timer. A newer seek doesn't cancel the running decode - its frame is still worth showing until the next
	///     one lands - only Play/Close do (CancelSeeks). Cancellation stays a plain cooperative check returning null,
	///     not an exception: a superseded seek is expected, not exceptional.
	/// </summary>
	public void RequestSeek(TimeSpan position, SeekAccuracy accuracy = SeekAccuracy.Exact)
	{
		if (_recording is null) return;

		_pendingSeek = (position, accuracy);
		if (_seekLoopRunning) return;

		_seekLoopRunning = true;
		_ = RunSeekLoopAsync();
	}

	private async Task RunSeekLoopAsync()
	{
		try
		{
			while (_pendingSeek is { } request && _recording is { } recording)
			{
				_pendingSeek = null;
				var generation = _seekGeneration;
				CancellationToken ct = _seekCts.Token;

				ComposedPreviewFrame? composed;
				try
				{
					composed = await Task.Run(() => DecodeStill(recording, request.Position, request.Accuracy, ct));
				}
				catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
				{
					if (generation == _seekGeneration) Message?.Invoke($"Preview seek failed: {ex.Message}");
					continue;
				}

				if (generation == _seekGeneration) PublishStill(recording, composed);
				else if (composed is not null) recording.Pool.Return(composed.Bgra);
			}
		}
		finally
		{
			_seekLoopRunning = false;
		}
	}

	/// <summary>Drops a waiting seek and stops the one decoding - Play and Close take over from any of them.</summary>
	private void CancelSeeks()
	{
		_pendingSeek = null;
		_seekGeneration++;
		_seekCts.Cancel();
		_seekCts.Dispose();
		_seekCts = new CancellationTokenSource();
	}

	private static ComposedPreviewFrame? DecodeStill(OpenRecording recording, TimeSpan position, SeekAccuracy accuracy, CancellationToken ct)
	{
		return recording.Video.GetFrame(position, accuracy, ct) is { } frame ? recording.Compositor.ShowStill(frame, position) : null;
	}

	private void StartMapWidgetPreparation(OpenRecording recording)
	{
		// Cancels a fetch still running for an older key (the zoom box fires per click, with no debounce), so it can't
		// land after a newer one - ApplyMapMosaic's key check covers whatever is already past cancelling.
		_ = PrepareMosaicAsync(recording, static (r, progress, ct) => r.BuildMapMosaicAsync(progress, ct),
			static (r, mosaic, key) => r.ApplyMapMosaic(mosaic, key), "Fetching map tiles", "Map preview failed",
			Restart(ref _mapPrepareCts));
	}

	private void StartRouteIntroPreparation(OpenRecording recording)
	{
		if (!recording.ShowsRouteIntro) return;

		_ = PrepareMosaicAsync(recording, static (r, progress, ct) => r.BuildRouteIntroMosaicAsync(progress, ct),
			static (r, mosaic, key) => r.ApplyRouteIntroMapMosaic(mosaic, key), "Fetching route overview map",
			"Route overview map failed", Restart(ref _routeIntroPrepareCts));
	}

	private static CancellationToken Restart(ref CancellationTokenSource? cts)
	{
		cts?.Cancel();
		cts = new CancellationTokenSource();
		return cts.Token;
	}

	/// <summary>
	///     Fetches a map mosaic without holding anything up for however long that takes - the network fetch runs
	///     outside the compositor's lock (see OverlayRenderer.BuildMapMosaicAsync), only the swap into the renderer and
	///     the still composed again happen inside it.
	/// </summary>
	private async Task PrepareMosaicAsync<TKey>(OpenRecording recording,
		Func<OverlayRenderer, Action<int, int>, CancellationToken, Task<(RouteMapMosaic?, TKey)>> build,
		Action<OverlayRenderer, RouteMapMosaic?, TKey> apply, string progressText, string failureText, CancellationToken ct)
	{
		RouteMapMosaic? mosaic;
		TKey key;
		try
		{
			(mosaic, key) = await build(recording.Compositor.Renderer,
				(fetched, total) => Message?.Invoke($"{progressText}: {fetched}/{total}"), ct);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Message?.Invoke($"{failureText}: {ex.Message}");
			return;
		}

		if (!ReferenceEquals(_recording, recording))
		{
			mosaic?.Dispose();
			return;
		}

		PublishStill(recording, recording.Compositor.Change(r => apply(r, mosaic, key)));
	}

	/// <summary>A composed still goes on screen unless playback is showing its own frames by now.</summary>
	private void PublishStill(OpenRecording recording, ComposedPreviewFrame? frame)
	{
		if (IsPlaying && frame is not null)
		{
			recording.Pool.Return(frame.Bgra);
			return;
		}

		Publish(recording.Pool, frame);
	}

	private void Publish(FrameBufferPool pool, ComposedPreviewFrame? frame)
	{
		if (frame is null) return;

		FrameReady?.Invoke(frame);
		pool.Return(frame.Bgra);
	}

	/// <summary>Everything one opened recording holds - closed together, in the background (DisposeWhenStoppedAsync).</summary>
	private sealed class OpenRecording(
		LibavVideoSource video,
		LibavAudioSource? audioSource,
		AudioOutput? audioOutput,
		FrameBufferPool pool,
		OverlayCompositor compositor,
		IReadOnlyList<PlaybackSegment> segments,
		int width,
		int height,
		double fps,
		bool showsRouteIntro) : IDisposable
	{
		public LibavVideoSource Video { get; } = video;

		// Both null when the recording has no audio track or there's no playback device - playback is then silent.
		public LibavAudioSource? AudioSource { get; } = audioSource;
		public AudioOutput? AudioOutput { get; } = audioOutput;

		// Shared by the decoder, the compositor and playback - every preview frame buffer comes from here.
		public FrameBufferPool Pool { get; } = pool;
		public OverlayCompositor Compositor { get; } = compositor;

		// What a full-resolution snapshot needs to open a decoder of its own.
		public IReadOnlyList<PlaybackSegment> Segments { get; } = segments;
		public int Width { get; } = width;
		public int Height { get; } = height;
		public double Fps { get; } = fps;
		public bool ShowsRouteIntro { get; } = showsRouteIntro;

		public void Dispose()
		{
			Compositor.Dispose();
			Video.Dispose();
			AudioOutput?.Dispose();
			AudioSource?.Dispose();
		}
	}
}
