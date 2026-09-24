using System.Threading.Channels;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using MapMosaicKey = (string Url, int Zoom, double MaxZoomOutFactor);

namespace OsmoOverlay.Core.Preview;

public sealed record ComposedPreviewFrame(TimeSpan Position, byte[] Bgra);

/// <summary>
///     The live preview: decodes through LibavVideoSource and composes each frame with the overlay. Public
///     members are called from the UI thread, and FrameReady/PlaybackStopped/Message are raised on it.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
	// How many decoded+composed frames the background producer is allowed to run ahead of what
	// RunPlaybackAsync is currently pacing out - enough to absorb a transient decode hiccup (a slow
	// disk read, a GC pause, a seek into a new stretch) without it
	// reaching the screen as a stutter, without buffering so much that a Pause feels laggy or memory
	// use grows needlessly (each buffered frame is a full preview-resolution BGRA copy).
	private const int PlaybackPrefetchFrames = 3;

	// Audio decoded ahead of the device - enough to ride out a slow read, short enough that a Pause is silent at once
	// (AudioOutput.Stop drops it anyway).
	private const double AudioQueueSeconds = 0.25;

	// With the audio clock, a frame this late is dropped instead of shown - the video catches up with the sound.
	private const double LateFrameSeconds = 0.1;

	// Composed frames in flight: the playback channel, the one being composed and the one being published.
	private readonly FrameBufferPool _composedBuffers = new(PlaybackPrefetchFrames + 2);
	private readonly Lock _lock = new();
	// Telemetry on the output's timeline (see SetOutputTimeline) - what the renderer draws from.
	private IReadOnlyList<DerivedFrame>? _derivedFrames;
	// The same telemetry on the recording's own timeline, to re-map from when the cuts change.
	private IReadOnlyList<DerivedFrame>? _recordingFrames;
	private Func<IReadOnlyList<DerivedFrame>, IReadOnlyList<OverlayElement>, OverlayRenderer>? _createRenderer;
	private OverlaySettings? _settings;
	private bool _hasContainerTime;
	private bool _hasGpsFix;
	private bool _hasGpsTimestamp;
	private TimeSpan _lastPosition;
	private VideoFrame? _lastVideoFrame;
	// Overlay scratch buffer reused across every Compose call - always accessed under _lock.
	private byte[]? _overlayBuffer;
	private CancellationTokenSource? _mapPrepareCts;
	private CancellationTokenSource? _playbackCts;
	// The last playback run - the next one waits for it to finish, so two runs never share the decoders or the
	// audio device (the old one's final AudioOutput.Stop would otherwise clear the new one's sound).
	private Task _playbackRun = Task.CompletedTask;
	// Bumped by every OpenAsync/Close - an open that was overtaken while it awaited drops what it opened.
	private int _openGeneration;
	private OverlayRenderer? _renderer;
	private bool _resumeAfterScrub;
	private CancellationTokenSource? _routeIntroPrepareCts;
	// Seeks, latest wins (see RequestSeek): only the newest request waits, the one decoding finishes.
	private (TimeSpan Position, SeekAccuracy Accuracy)? _pendingSeek;
	private bool _seekLoopRunning;
	// Bumped by Play/Close - a seek decoded before that is dropped instead of shown.
	private int _seekGeneration;
	private CancellationTokenSource _seekCts = new();
	// Kept across Close/OpenAsync (the GUI reopens the preview for some settings changes) - only
	// SetOutputTimeline changes it. Null = the whole recording.
	private OutputTimeline? _outputTimeline;
	// A renderer replaced by SetOutputTimeline while playback may still hold it for one last frame -
	// disposed on the next replacement or Close instead of right away.
	private OverlayRenderer? _retiredRenderer;
	private LibavVideoSource? _video;
	// Both null when the recording has no audio track or there's no playback device - playback is then silent.
	private LibavAudioSource? _audioSource;
	private AudioOutput? _audioOutput;
	private float _audioGain;
	// Kept across Close/OpenAsync like the output timeline - a view toggle, not part of the file.
	private bool _showOverlay = true;
	private RouteJoin _routeAcrossCuts = OverlaySettingsStore.Load().RouteAcrossCuts;
	// What a full-resolution snapshot needs to open its own decoder (RenderSnapshotPngAsync).
	private IReadOnlyList<PlaybackSegment>? _segments;
	private int _videoWidth;
	private int _videoHeight;
	private double _fps;

	public bool IsPlaying => _playbackCts is not null;
	public bool HasAudio => _audioOutput is not null;

	/// <summary>1 = real time. Other speeds keep the sound, tempo-changed without changing its pitch (AudioTempo).</summary>
	public double PlaybackRate { get; private set; } = 1;

	/// <summary>Playback starts over at the end - of LoopRange when set, else of the recording (see SetLoop).</summary>
	public bool Loop { get; private set; }

	public TimeRange? LoopRange { get; private set; }
	public TimeSpan Duration => _video?.Duration ?? TimeSpan.Zero;

	public void Dispose()
	{
		Close();
	}

	/// <summary>The frame's Bgra buffer is only valid for the duration of the callback - it's reused for a later frame right after, so copy out of it, don't keep it.</summary>
	public event Action<ComposedPreviewFrame>? FrameReady;
	public event Action? PlaybackStopped;
	public event Action<string>? Message;

	public async Task OpenAsync(FileSummary summary, int previewWidth, int previewHeight)
	{
		Close();
		var generation = _openGeneration;

		if (summary.TelemetryFrames is not { Count: > 0 } rawFrames)
			throw new InvalidOperationException("File has no telemetry to preview.");

		// Recomputed here rather than reusing summary.DerivedFrames so a live SmoothGpsMotion toggle
		// is picked up on every open; kept as _derivedFrames so every call below uses this same
		// smoothed list instead of falling back to the cached, unsmoothed one.
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<DerivedFrame> recordingFrames = TelemetryProcessor.Process(rawFrames, settings.SmoothGpsMotion);
		IReadOnlyList<DerivedFrame> derivedFrames = MapToOutput(recordingFrames);
		_recordingFrames = recordingFrames;
		_derivedFrames = derivedFrames;
		_settings = settings;
		_hasGpsFix = TelemetryProcessor.HasAnyGpsFix(rawFrames);
		_hasGpsTimestamp = TelemetryProcessor.HasAnyGpsTimestamp(rawFrames);
		_hasContainerTime = summary.ContainerRecordingStartUtc is not null;

		List<PlaybackSegment> segments = PlaybackSegment.Of(summary);
		_segments = segments;
		_videoWidth = summary.Video.Width;
		_videoHeight = summary.Video.Height;
		_fps = summary.Video.Fps;

		// Opening the decoder and decoding the first frame are both blocking; awaiting Task.Run (rather
		// than running the whole method inside one) lets the continuation - and the FrameReady
		// event it raises - resume on the caller's thread (the UI thread), same as RunPlaybackAsync.
		LibavVideoSource video = await Task.Run(() => new LibavVideoSource(segments,
			summary.Video.Fps, previewWidth, previewHeight));
		(LibavAudioSource? audioSource, AudioOutput? audioOutput) = await Task.Run(() => OpenAudio(segments));
		if (generation != _openGeneration)
		{
			video.Dispose();
			audioOutput?.Dispose();
			audioSource?.Dispose();
			return;
		}

		AppLogger.Info($"Preview decoder: {video.DecoderDescription}");
		_audioSource = audioSource;
		_audioOutput = audioOutput;
		_audioOutput?.SetGain(_audioGain);
		(List<OverlayPreset> presets, var activeId) = OverlayPresetStore.Load(summary.Video.Width, summary.Video.Height);
		IReadOnlyList<OverlayElement> layout =
			OverlayDataRequirements.ApplyAvailability(presets.First(p => p.Id == activeId).Elements, _hasGpsFix,
				_hasGpsTimestamp, _hasContainerTime);
		var hasGpsFix = _hasGpsFix;
		_createRenderer = (frames, currentLayout) => new OverlayRenderer(summary.Video.Width, summary.Video.Height,
			frames[0].Raw.AltitudeMeters, currentLayout, frames, TelemetryProcessor.Summarize(frames).MaxSpeedKmh,
			settings.ShowWatermark, summary.CameraModel, summary.ContainerRecordingStartUtc,
			settings.MapTileUrlTemplate, settings.MapAttribution, settings.MapShowAttribution, settings.MapApiKey,
			RouteIntroSettings.ForRecording(settings, hasGpsFix)) { RouteAcrossCuts = _routeAcrossCuts };
		OverlayRenderer renderer = _createRenderer(derivedFrames, layout);

		_video = video;
		_renderer = renderer;

		// Map/RouteIntro tiles are fetched in the background (see PrepareMapInBackgroundAsync/
		// PrepareRouteIntroInBackgroundAsync below) instead of being awaited here - both widgets are on
		// by default, and awaiting a possibly-slow or unreachable tile server before showing even the
		// first frame made opening ANY file hang on network I/O with no way to tell "still fetching" from
		// "frozen". The first frame now shows immediately (Map/RouteIntro drawing their normal "no data
		// yet" placeholder - see DrawMapWidget/DrawRouteIntroMap), then recomposes once tiles land, same
		// UX SetLayout already gives a live MapWidget toggle.
		ComposedPreviewFrame? first;
		try
		{
			first = await Task.Run(() => DecodeAndCompose(video, TimeSpan.Zero, SeekAccuracy.Exact, CancellationToken.None));
		}
		catch (ObjectDisposedException) when (generation != _openGeneration)
		{
			// Closed while the first frame decoded.
			return;
		}

		if (first is not null) Publish(first);

		StartMapPreparation(renderer);
	}

	/// <summary>Map/route-intro tiles for a (new) renderer, fetched in the background - see OpenAsync.</summary>
	private void StartMapPreparation(OverlayRenderer renderer)
	{
		if (renderer.Layout.Any(e => e is MapWidgetElement { Visible: true }))
		{
			_mapPrepareCts?.Cancel();
			var mapCts = new CancellationTokenSource();
			_mapPrepareCts = mapCts;
			_ = PrepareMapInBackgroundAsync(renderer, mapCts.Token);
		}

		if (_settings is { ShowRouteIntro: true } && _hasGpsFix)
		{
			_routeIntroPrepareCts?.Cancel();
			var routeIntroCts = new CancellationTokenSource();
			_routeIntroPrepareCts = routeIntroCts;
			_ = PrepareRouteIntroInBackgroundAsync(renderer, routeIntroCts.Token);
		}
	}

	/// <summary>
	///     Shows the preview the way a cut render will come out (see OutputTimeline): inside kept pieces the
	///     overlay is drawn from telemetry on the output's timeline (route intro at the first kept frame,
	///     distance counting only kept parts, ...), cut-out moments show the plain video when scrubbed to and
	///     are skipped by playback (PlaybackStretches). Null = the whole
	///     recording. Stats like total distance are baked into the renderer, so it's rebuilt here - playback
	///     is paused first, since it holds the renderer it started with.
	/// </summary>
	public void SetOutputTimeline(OutputTimeline? timeline)
	{
		Pause();

		OverlayRenderer? renderer;
		lock (_lock)
		{
			_outputTimeline = timeline;
			if (_renderer is null || _recordingFrames is null || _createRenderer is null) return;

			IReadOnlyList<DerivedFrame> frames = MapToOutput(_recordingFrames);
			renderer = _createRenderer(frames, _renderer.Layout);
			_retiredRenderer?.Dispose();
			_retiredRenderer = _renderer;
			_renderer = renderer;
			_derivedFrames = frames;

			if (_lastVideoFrame is { } videoFrame)
			{
				ComposedPreviewFrame? composed = Compose(renderer, frames, videoFrame, _lastPosition);
				if (composed is not null) Publish(composed);
			}
		}

		StartMapPreparation(renderer);
	}

	private IReadOnlyList<DerivedFrame> MapToOutput(IReadOnlyList<DerivedFrame> recordingFrames)
	{
		return _outputTimeline?.MapFrames(recordingFrames) is { Count: > 0 } mapped ? mapped : recordingFrames;
	}

	public void Close()
	{
		_openGeneration++;
		_playbackCts?.Cancel();
		_playbackCts = null;
		CancelSeeks();
		_mapPrepareCts?.Cancel();
		_mapPrepareCts = null;
		_routeIntroPrepareCts?.Cancel();
		_routeIntroPrepareCts = null;
		_resumeAfterScrub = false;

		LibavVideoSource? video;
		lock (_lock)
		{
			video = _video;
			_video = null;
			_renderer?.Dispose();
			_renderer = null;
			_retiredRenderer?.Dispose();
			_retiredRenderer = null;
			_lastVideoFrame = null;
			_overlayBuffer = null;
		}

		// Outside _lock: the decoders wait for a decode still running on another thread.
		video?.Dispose();
		_audioOutput?.Dispose();
		_audioOutput = null;
		_audioSource?.Dispose();
		_audioSource = null;
		_derivedFrames = null;
		_recordingFrames = null;
		_createRenderer = null;
	}

	/// <summary>0-1 on a perceptual (squared) curve, so the slider's middle sounds like half as loud; applies right away, also mid-playback.</summary>
	public void SetAudioVolume(double volume, bool muted)
	{
		_audioGain = muted ? 0 : (float)(Math.Clamp(volume, 0, 1) * Math.Clamp(volume, 0, 1));
		_audioOutput?.SetGain(_audioGain);
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

	/// <summary>Applies to a running playback right away (it carries on from where it is at the new speed), else to the next Play.</summary>
	public void SetPlaybackRate(double rate)
	{
		if (rate <= 0 || rate == PlaybackRate) return;

		PlaybackRate = rate;
		RestartIfPlaying();
	}

	/// <summary>
	///     Loops playback over a range of the recording (the In/Out selection) or, with none, the whole of it - cut-out
	///     parts are skipped as always. Applies to a running playback right away, like SetPlaybackRate.
	/// </summary>
	public void SetLoop(bool loop, TimeRange? range)
	{
		if (loop == Loop && range == LoopRange) return;

		// A new range only matters to a playback that loops (or did until now).
		var affectsPlayback = loop || Loop;
		Loop = loop;
		LoopRange = range;
		if (affectsPlayback) RestartIfPlaying();
	}

	/// <summary>Replaces a running playback with one from where it is - without PlaybackStopped, to the GUI it's the same playback.</summary>
	private void RestartIfPlaying()
	{
		if (_playbackCts is not { } running) return;

		running.Cancel();
		_playbackCts = null;
		Play(_lastPosition);
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

	public void TogglePlayPause(TimeSpan currentPosition)
	{
		_resumeAfterScrub = false;

		if (_playbackCts is not null)
		{
			Pause();
			return;
		}

		Play(currentPosition);
	}

	public void Play(TimeSpan fromPosition)
	{
		if (_video is null || _renderer is null || _derivedFrames is null) return;

		CancelSeeks();

		var cts = new CancellationTokenSource();
		_playbackCts = cts;
		PlaybackPlan plan = PlanPlayback(_outputTimeline, fromPosition, _video.Duration, Loop, LoopRange);
		_playbackRun = RunPlaybackAsync(_playbackRun, _video, _audioSource, _audioOutput, plan, PlaybackRate, cts);
	}

	public void Pause()
	{
		if (_playbackCts is null) return;

		_playbackCts.Cancel();
		_playbackCts = null;
		PlaybackStopped?.Invoke();
	}

	public void BeginScrubDrag()
	{
		if (_playbackCts is null) return;

		_resumeAfterScrub = true;
		_playbackCts.Cancel();
		_playbackCts = null;
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
		if (_playbackCts is null) Play(currentPosition);
	}

	/// <summary>
	///     Swaps the live layout (drag/visibility edits from the GUI) and, if a frame is already
	///     cached, instantly recomposes it - no decoding, so dragging stays smooth.
	/// </summary>
	public void SetLayout(IReadOnlyList<OverlayElement> layout)
	{
		// Same filter OpenAsync applies up front - every live edit (drag, a checkbox, a preset switch)
		// re-sends the raw preset layout here, so without re-filtering each time, a widget this file's
		// telemetry can't support would come right back as soon as anything else changed.
		layout = OverlayDataRequirements.ApplyAvailability(layout, _hasGpsFix, _hasGpsTimestamp, _hasContainerTime);

		OverlayRenderer? renderer;
		lock (_lock)
		{
			if (_renderer is null) return;
			renderer = _renderer;
			renderer.Layout = layout;

			if (_lastVideoFrame is { } videoFrame && _derivedFrames is not null)
			{
				ComposedPreviewFrame? composed = Compose(renderer, _derivedFrames, videoFrame, _lastPosition);
				if (composed is not null) Publish(composed);
			}
		}

		if (renderer.NeedsMapPrepare(layout))
		{
			// Cancel any fetch already in flight for a previous key (e.g. the zoom NumericUpDown fires
			// ValueChanged per click/held-arrow with no debounce) - without this, an older, slower fetch
			// could finish after a newer one and clobber it. ApplyMapMosaic's own key check is a second,
			// belt-and-suspenders line of defense for whatever's already past the cancellation point.
			_mapPrepareCts?.Cancel();
			var cts = new CancellationTokenSource();
			_mapPrepareCts = cts;
			_ = PrepareMapInBackgroundAsync(renderer, cts.Token);
		}
	}

	/// <summary>
	///     Fetches map tiles for a newly-added/changed Map widget without blocking scrubbing or
	///     playback for however long that takes - the network fetch runs outside _lock (see
	///     OverlayRenderer.BuildMapMosaicAsync), and only the quick swap into the renderer + a
	///     recompose of the current frame happens inside it.
	/// </summary>
	private async Task PrepareMapInBackgroundAsync(OverlayRenderer renderer, CancellationToken ct)
	{
		RouteMapMosaic? mosaic;
		MapMosaicKey? key;
		try
		{
			(mosaic, key) = await renderer.BuildMapMosaicAsync(
				(fetched, total) => Message?.Invoke($"Fetching map tiles: {fetched}/{total}"), ct);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Message?.Invoke($"Map preview failed: {ex.Message}");
			return;
		}

		lock (_lock)
		{
			if (!ReferenceEquals(_renderer, renderer))
			{
				// The file was closed/reopened while this fetch was in flight - this result belongs to
				// a renderer nobody references anymore.
				mosaic?.Dispose();
				return;
			}

			renderer.ApplyMapMosaic(mosaic, key);

			if (_lastVideoFrame is not { } videoFrame || _derivedFrames is null) return;
			ComposedPreviewFrame? composed = Compose(renderer, _derivedFrames, videoFrame, _lastPosition);
			if (composed is not null) Publish(composed);
		}
	}

	/// <summary>Same shape as PrepareMapInBackgroundAsync, for the route-intro overview mosaic instead of MapWidget's.</summary>
	private async Task PrepareRouteIntroInBackgroundAsync(OverlayRenderer renderer, CancellationToken ct)
	{
		RouteMapMosaic? mosaic;
		string? key;
		try
		{
			(mosaic, key) = await renderer.BuildRouteIntroMosaicAsync(
				(fetched, total) => Message?.Invoke($"Fetching route overview map: {fetched}/{total}"), ct);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Message?.Invoke($"Route overview map failed: {ex.Message}");
			return;
		}

		lock (_lock)
		{
			if (!ReferenceEquals(_renderer, renderer))
			{
				mosaic?.Dispose();
				return;
			}

			renderer.ApplyRouteIntroMapMosaic(mosaic, key);

			if (_lastVideoFrame is not { } videoFrame || _derivedFrames is null) return;
			ComposedPreviewFrame? composed = Compose(renderer, _derivedFrames, videoFrame, _lastPosition);
			if (composed is not null) Publish(composed);
		}
	}

	/// <summary>How the drawn routes cross a cut - applies to the frame on screen right away, like SetShowWatermark.</summary>
	public void SetRouteAcrossCuts(RouteJoin join)
	{
		lock (_lock)
		{
			_routeAcrossCuts = join;
			if (_renderer is null) return;

			_renderer.RouteAcrossCuts = join;
			if (_lastVideoFrame is not { } videoFrame || _derivedFrames is null) return;

			ComposedPreviewFrame? composed = Compose(_renderer, _derivedFrames, videoFrame, _lastPosition);
			if (composed is not null) Publish(composed);
		}
	}

	/// <summary>Shows the plain video (false) or the video with the overlay - recomposes the frame on screen right away.</summary>
	public void SetShowOverlay(bool show)
	{
		lock (_lock)
		{
			_showOverlay = show;
			if (_renderer is null || _lastVideoFrame is not { } videoFrame || _derivedFrames is null) return;

			ComposedPreviewFrame? composed = Compose(_renderer, _derivedFrames, videoFrame, _lastPosition);
			if (composed is not null) Publish(composed);
		}
	}

	/// <summary>
	///     The frame at a position as a PNG at the recording's full resolution, with the overlay exactly as the render
	///     draws it there - none where the position is cut out or while the overlay is hidden. Decoded by a decoder of
	///     its own at full size, so the running preview isn't disturbed.
	/// </summary>
	public async Task<byte[]> RenderSnapshotPngAsync(TimeSpan position)
	{
		if (_segments is not { } segments) throw new InvalidOperationException("No recording is open.");

		var (width, height, fps) = (_videoWidth, _videoHeight, _fps);
		return await Task.Run(() =>
		{
			using var source = new LibavVideoSource(segments, fps, width, height);
			VideoFrame frame = source.GetFrame(position, SeekAccuracy.Exact, CancellationToken.None)
			                   ?? throw new InvalidOperationException("The frame couldn't be decoded.");

			byte[]? overlay = null;
			lock (_lock)
			{
				if (_showOverlay && _renderer is { } renderer && _derivedFrames is { Count: > 0 } frames &&
				    OutputSeconds(position) is { } seconds)
				{
					overlay = new byte[renderer.FrameBufferSize(width, height)];
					renderer.RenderInto(TelemetryProcessor.FindNearest(frames, seconds), overlay, width, height, true);
				}
			}

			var composed = new byte[width * height * 4];
			PreviewCompositor.Compose(composed, width, height, frame.Bgra, frame.Stride, overlay, width, height);
			return PreviewCompositor.EncodePng(composed, width, height);
		});
	}

	/// <summary>Mirrors SetLayout: lets Settings toggle the watermark live without reopening the file.</summary>
	public void SetShowWatermark(bool show)
	{
		lock (_lock)
		{
			if (_renderer is null) return;
			_renderer.ShowWatermark = show;

			if (_lastVideoFrame is not { } videoFrame || _derivedFrames is null) return;
			ComposedPreviewFrame? composed = Compose(_renderer, _derivedFrames, videoFrame, _lastPosition);
			if (composed is not null) Publish(composed);
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
		if (_video is null) return;

		_pendingSeek = (position, accuracy);
		if (_seekLoopRunning) return;

		_seekLoopRunning = true;
		_ = RunSeekLoopAsync();
	}

	private async Task RunSeekLoopAsync()
	{
		try
		{
			while (_pendingSeek is { } request && _video is { } video)
			{
				_pendingSeek = null;
				var (position, accuracy) = request;
				var generation = _seekGeneration;
				CancellationToken ct = _seekCts.Token;

				ComposedPreviewFrame? composed;
				try
				{
					composed = await Task.Run(() => DecodeAndCompose(video, position, accuracy, ct));
				}
				catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
				{
					if (generation == _seekGeneration) Message?.Invoke($"Preview seek failed: {ex.Message}");
					continue;
				}

				if (composed is null) continue;
				if (generation == _seekGeneration) Publish(composed);
				else _composedBuffers.Return(composed.Bgra);
			}
		}
		finally
		{
			_seekLoopRunning = false;
		}
	}

	/// <summary>
	///     Decode/compose and pacing/display are two separate concerns run on two separate threads,
	///     joined by a small bounded channel (ProduceFramesAsync is the writer, the loop below is the
	///     sole reader): the producer decodes flat-out, ahead of what's currently on screen, so a
	///     transient decode hiccup (the seek into a new stretch, a slow disk read, a
	///     GC pause) gets absorbed by frames already sitting in the channel instead of reaching the
	///     screen as a stutter. This replaced an earlier single-threaded decode-then-wait-then-display
	///     loop that had no way to hide a slow frame, plus a "Stale" catch-up mechanism that silently
	///     dropped a whole burst of frames after any slow open/seek - both are gone now; backpressure
	///     from the channel's bounded capacity is what keeps the producer from running away, and
	///     nothing needs to be discarded to "catch up" from a slow start.
	/// </summary>
	private async Task RunPlaybackAsync(Task previousRun, LibavVideoSource video, LibavAudioSource? audioSource, AudioOutput? audioOutput,
		PlaybackPlan plan, double rate, CancellationTokenSource ownCts)
	{
		CancellationToken ct = ownCts.Token;

		try
		{
			// Already cancelled by whoever started this one - only its cleanup is left, a few milliseconds.
			await previousRun;
			ct.ThrowIfCancellationRequested();

			// See HighResolutionTimer - default Windows timer resolution otherwise makes the Task.Delay
			// below overshoot by several ms per frame, which is most of this loop's entire real-time
			// budget on a demanding (e.g. 4K60) source.
			using var highResTimer = new HighResolutionTimer();

			var channel = Channel.CreateBounded<PlaybackFrame>(
				new BoundedChannelOptions(PlaybackPrefetchFrames) { SingleReader = true, SingleWriter = true });

			// Linked, not just `ct` directly: if the consumer loop below stops for a reason that has
			// nothing to do with `ct` (e.g. a FrameReady subscriber throws), the producer can otherwise
			// be left blocked forever on a full channel nobody is draining anymore - awaiting it in the
			// consumer's finally would then hang too. Cancelling this in that finally, unconditionally,
			// guarantees the producer can always be unblocked regardless of why the consumer stopped.
			using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task<string?> producer = Task.Run(
				() => ProduceFramesAsync(video, plan.Stretches(), FrameStep(rate, video.Fps), channel.Writer, producerCts.Token),
				producerCts.Token);

			PlaybackClock clock = new(audioOutput, audioSource?.SampleRate ?? 1, rate);
			audioOutput?.Stop();
			Task feeder = clock.FollowsAudio && audioSource is not null && audioOutput is not null
				? Task.Run(() => FeedAudioAsync(audioSource, audioOutput, clock, plan.Stretches(), rate, producerCts.Token), producerCts.Token)
				: Task.CompletedTask;

			string? stoppedEarly = null;
			try
			{
				var started = false;
				await foreach (PlaybackFrame playbackFrame in channel.Reader.ReadAllAsync(ct))
				{
					ComposedPreviewFrame composed = playbackFrame.Composed;
					if (!started)
					{
						clock.Start(playbackFrame.PlayTime);
						started = true;
					}
					else if (playbackFrame.StartsStretch)
					{
						clock.Rebase(playbackFrame.PlayTime);
					}

					var delay = playbackFrame.PlayTime - clock.Now;
					if (delay > 0)
					{
						await Task.Delay(TimeSpan.FromSeconds(delay), ct);
					}
					else if (clock.FollowsAudio && delay < -LateFrameSeconds)
					{
						_composedBuffers.Return(composed.Bgra);
						continue;
					}

					Publish(composed);
				}
			}
			finally
			{
				// Any real failure was already observed via the channel (ReadAllAsync rethrows it), so
				// a second throw here would only be a duplicate.
				producerCts.Cancel();
				try
				{
					stoppedEarly = await producer;
				}
				catch
				{
				}

				try
				{
					await feeder;
				}
				catch
				{
				}

				// After the feeder has stopped, so nothing it pushed last is left queued for the next Play.
				audioOutput?.Stop();
			}

			if (stoppedEarly is not null) Message?.Invoke($"Playback stopped: {stoppedEarly}");
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Message?.Invoke($"Playback stopped: {ex.Message}");
		}
		finally
		{
			if (ReferenceEquals(_playbackCts, ownCts))
			{
				_playbackCts = null;
				PlaybackStopped?.Invoke();
			}
		}
	}

	/// <summary>
	///     Plays the stretches one after another (endlessly while looping), one playback stream each; returns the
	///     decoder's error output if a stream ended well before its stretch did. Frames are decoded outside _lock - only
	///     composing needs it, so SetLayout, scrubbing and the rest never wait for a decode.
	/// </summary>
	private async Task<string?> ProduceFramesAsync(LibavVideoSource video, IEnumerable<PlaybackStretch> stretches, int step,
		ChannelWriter<PlaybackFrame> writer, CancellationToken ct)
	{
		var halfFrame = TimeSpan.FromSeconds(0.5 / video.Fps);
		// Where each stretch starts on the play timeline - the stretches back to back, the way the audio is pushed.
		double playOffset = 0;
		try
		{
			foreach (PlaybackStretch stretch in stretches)
			{
				using LibavVideoSource.PlaybackStream stream = video.OpenPlaybackStream(stretch.Start, ct);
				var startsStretch = true;
				var playStart = playOffset;
				playOffset += (stretch.End - stretch.Start).TotalSeconds;
				while (!ct.IsCancellationRequested)
				{
					VideoFrame? videoFrame = stream.TryReadNextFrame(step);
					if (videoFrame is null) break;

					if (stream.Position >= stretch.End - halfFrame)
					{
						video.Recycle(videoFrame);
						break;
					}

					ComposedPreviewFrame? composed = ComposeCurrent(video, videoFrame, stream.Position);
					if (composed is null) break;

					// Backpressure, not held under _lock - once the channel is full this waits for the
					// consumer to drain a slot, which can legitimately take a while (that's the pacing
					// working as intended), and _lock is needed by SetLayout/scrub/etc. in the meantime.
					var playTime = playStart + (stream.Position - stretch.Start).TotalSeconds;
					await writer.WriteAsync(new PlaybackFrame(composed, playTime, startsStretch), ct);
					startsStretch = false;
				}

				if (stream.Error is { } error)
				{
					writer.TryComplete();
					return error;
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

		return null;
	}

	/// <summary>
	///     Frames decoded per frame shown: sped up, only every Nth is converted and shown, so the screen stays at the
	///     source's frame rate, at most ~60 a second, instead of asking for 240 converted frames a second at 4x.
	/// </summary>
	internal static int FrameStep(double rate, double fps)
	{
		return Math.Max(1, (int)Math.Ceiling(rate * fps / 60 - 1e-9));
	}

	/// <summary>
	///     What Play covers from a position: up to the end (of the loop range, or of the recording), with cuts
	///     (SetOutputTimeline) only the kept pieces - skipped the way the render leaves them out. Looping, the same range
	///     from its start follows over and over; a position outside the loop range starts at its start.
	/// </summary>
	internal static PlaybackPlan PlanPlayback(OutputTimeline? timeline, TimeSpan from, TimeSpan duration, bool loop, TimeRange? loopRange)
	{
		var start = loop && loopRange is { } range ? Math.Max(0, range.StartSeconds) : 0;
		var end = loop && loopRange is { } limit ? Math.Min(limit.EndSeconds, duration.TotalSeconds) : duration.TotalSeconds;
		var first = from.TotalSeconds;
		if (loop && (first < start || first >= end)) first = start;

		return new PlaybackPlan(Stretches(timeline, first, end), loop ? Stretches(timeline, start, end) : []);
	}

	private static List<PlaybackStretch> Stretches(OutputTimeline? timeline, double from, double end)
	{
		if (timeline is null) return end > from ? [new PlaybackStretch(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(end))] : [];

		List<PlaybackStretch> stretches = [];
		var position = from;
		while (timeline.NextKeptStretch(position) is { } kept && kept.Start < end)
		{
			var stretchEnd = Math.Min(kept.End, end);
			if (stretchEnd > kept.Start) stretches.Add(new PlaybackStretch(TimeSpan.FromSeconds(kept.Start), TimeSpan.FromSeconds(stretchEnd)));
			position = kept.End;
		}

		return stretches;
	}

	internal sealed record PlaybackStretch(TimeSpan Start, TimeSpan End);

	/// <summary>First what's left from the starting position, then - looping - Repeat over and over.</summary>
	internal sealed record PlaybackPlan(IReadOnlyList<PlaybackStretch> First, IReadOnlyList<PlaybackStretch> Repeat)
	{
		public IEnumerable<PlaybackStretch> Stretches()
		{
			foreach (PlaybackStretch stretch in First) yield return stretch;
			if (Repeat.Count == 0) yield break;

			while (true)
				foreach (PlaybackStretch stretch in Repeat)
					yield return stretch;
		}
	}

	/// <summary>PlayTime: seconds on the play timeline (PlaybackClock) - the stretches back to back, from 0.</summary>
	private readonly record struct PlaybackFrame(ComposedPreviewFrame Composed, double PlayTime, bool StartsStretch);

	/// <summary>
	///     Pushes the stretches' audio back to back, AudioQueueSeconds ahead of the device - the same joins the render
	///     makes, so the cut parts are skipped in the sound too - tempo-changed at any speed but 1x. Tells the clock how
	///     much went out, and when it's done (or failed, which hands the clock to its stopwatch).
	/// </summary>
	private static async Task FeedAudioAsync(LibavAudioSource source, AudioOutput output, PlaybackClock clock,
		IEnumerable<PlaybackStretch> stretches, double rate, CancellationToken ct)
	{
		AudioTempo? tempo = null;
		try
		{
			if (rate != 1) tempo = new AudioTempo(rate, source.SampleRate, source.Channels);

			foreach (PlaybackStretch stretch in stretches)
			{
				source.Seek(stretch.Start.TotalSeconds);
				while (true)
				{
					ct.ThrowIfCancellationRequested();
					while (output.QueuedSeconds > AudioQueueSeconds) await Task.Delay(10, ct);
					if (!PushNext(stretch.End.TotalSeconds)) break;
				}
			}
		}
		catch (InvalidOperationException ex)
		{
			AppLogger.Warn(ex, "Preview audio stopped");
		}
		finally
		{
			tempo?.Dispose();
			clock.AudioFinished();
		}

		bool PushNext(double end)
		{
			ReadOnlySpan<float> samples = source.Read(end);
			if (samples.IsEmpty) return false;

			if (tempo is not null) samples = tempo.Process(samples);
			output.Push(samples);
			clock.AddPushed(samples.Length / source.Channels);
			return true;
		}
	}

	/// <summary>Decodes outside _lock (a seek can take a while), then composes under it.</summary>
	private ComposedPreviewFrame? DecodeAndCompose(LibavVideoSource video, TimeSpan position, SeekAccuracy accuracy, CancellationToken ct)
	{
		VideoFrame? videoFrame = video.GetFrame(position, accuracy, ct);
		return videoFrame is null ? null : ComposeCurrent(video, videoFrame, position);
	}

	/// <summary>Composes with whatever renderer/telemetry is current - null (the frame recycled) once the preview was closed or reopened.</summary>
	private ComposedPreviewFrame? ComposeCurrent(LibavVideoSource video, VideoFrame videoFrame, TimeSpan position)
	{
		lock (_lock)
		{
			if (ReferenceEquals(_video, video) && _renderer is { } renderer && _derivedFrames is { } frames)
				return Compose(renderer, frames, videoFrame, position);
		}

		video.Recycle(videoFrame);
		return null;
	}

	private void Publish(ComposedPreviewFrame frame)
	{
		FrameReady?.Invoke(frame);
		_composedBuffers.Return(frame.Bgra);
	}

	private ComposedPreviewFrame? Compose(OverlayRenderer renderer, IReadOnlyList<DerivedFrame> frames,
		VideoFrame videoFrame, TimeSpan position)
	{
		// The previous frame is only ever read here, under _lock - once replaced, nothing references it.
		if (_lastVideoFrame is { } previous && !ReferenceEquals(previous, videoFrame)) _video?.Recycle(previous);
		_lastVideoFrame = videoFrame;
		_lastPosition = position;

		if (frames.Count == 0) return null;

		byte[]? overlay = null;
		if (_showOverlay && OutputSeconds(position) is { } seconds)
		{
			DerivedFrame frame = TelemetryProcessor.FindNearest(frames, seconds);
			var overlaySize = renderer.FrameBufferSize(videoFrame.Width, videoFrame.Height);
			if (_overlayBuffer?.Length != overlaySize) _overlayBuffer = new byte[overlaySize];
			renderer.RenderInto(frame, _overlayBuffer, videoFrame.Width, videoFrame.Height, true);
			overlay = _overlayBuffer;
		}

		var composed = _composedBuffers.Rent(videoFrame.Width * videoFrame.Height * 4);
		PreviewCompositor.Compose(composed, videoFrame.Width, videoFrame.Height, videoFrame.Bgra,
			videoFrame.Stride, overlay, videoFrame.Width, videoFrame.Height);

		return new ComposedPreviewFrame(position, composed);
	}

	/// <summary>Preview positions are on the recording's timeline, the telemetry on the output's (see SetOutputTimeline) - null where cut out.</summary>
	private double? OutputSeconds(TimeSpan position)
	{
		return _outputTimeline is { } timeline ? timeline.ToOutputSeconds(position.TotalSeconds) : position.TotalSeconds;
	}
}
