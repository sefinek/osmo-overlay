using System.Diagnostics;
using System.Threading.Channels;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using MapMosaicKey = (string Url, int Zoom, double MaxZoomOutFactor);

namespace OsmoOverlay.Core.Preview;

public sealed record ComposedPreviewFrame(TimeSpan Position, byte[] Bgra);

public sealed class PreviewPlayer : IDisposable
{
	private const int ScrubDebounceMs = 80;

	// How many decoded+composed frames the background producer is allowed to run ahead of what
	// RunPlaybackAsync is currently pacing out - enough to absorb a transient decode hiccup (a slow
	// disk read, a GC pause, the first frames after a fresh ffmpeg process starts) without it
	// reaching the screen as a stutter, without buffering so much that a Pause feels laggy or memory
	// use grows needlessly (each buffered frame is a full preview-resolution BGRA copy).
	private const int PlaybackPrefetchFrames = 3;

	// Composed frames in flight: the playback channel, the one being composed and the one being published.
	private readonly FrameBufferPool _composedBuffers = new(PlaybackPrefetchFrames + 2);
	private readonly Lock _lock = new();
	private IReadOnlyList<DerivedFrame>? _derivedFrames;
	private bool _hasContainerTime;
	private bool _hasGpsFix;
	private bool _hasGpsTimestamp;
	private TimeSpan _lastPosition;
	private VideoFrame? _lastVideoFrame;
	// Overlay scratch buffer reused across every Compose call - always accessed under _lock.
	private byte[]? _overlayBuffer;
	private CancellationTokenSource? _mapPrepareCts;
	private CancellationTokenSource? _playbackCts;
	private OverlayRenderer? _renderer;
	private bool _resumeAfterScrub;
	private CancellationTokenSource? _routeIntroPrepareCts;
	private CancellationTokenSource? _scrubCts;
	private VideoFrameSource? _video;

	public bool IsPlaying => _playbackCts is not null;
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

		if (summary.TelemetryFrames is not { Count: > 0 } rawFrames)
			throw new InvalidOperationException("File has no telemetry to preview.");

		// Recomputed here rather than reusing summary.DerivedFrames so a live SmoothGpsMotion toggle
		// is picked up on every open; kept as _derivedFrames so every call below uses this same
		// smoothed list instead of falling back to the cached, unsmoothed one.
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<DerivedFrame> derivedFrames = TelemetryProcessor.Process(rawFrames, settings.SmoothGpsMotion);
		_derivedFrames = derivedFrames;
		_hasGpsFix = TelemetryProcessor.HasAnyGpsFix(rawFrames);
		_hasGpsTimestamp = TelemetryProcessor.HasAnyGpsTimestamp(rawFrames);
		_hasContainerTime = summary.ContainerRecordingStartUtc is not null;

		List<PlaybackSegment> segments =
		[
			.. summary.InputPaths
				.Zip(summary.SegmentDurationsSeconds, (path, duration) => new PlaybackSegment(path, duration))
		];

		// Spawning ffmpeg and decoding the first frame are both blocking; awaiting Task.Run (rather
		// than running the whole method inside one) lets the continuation - and the FrameReady
		// event it raises - resume on the caller's thread (the UI thread), same as RunPlaybackAsync.
		VideoFrameSource video = await Task.Run(() => VideoFrameSource.Open(segments,
			summary.Video.Fps, previewWidth, previewHeight));
		(List<OverlayPreset> presets, var activeId) = OverlayPresetStore.Load(summary.Video.Width, summary.Video.Height);
		IReadOnlyList<OverlayElement> layout =
			OverlayDataRequirements.ApplyAvailability(presets.First(p => p.Id == activeId).Elements, _hasGpsFix,
				_hasGpsTimestamp, _hasContainerTime);
		var renderer = new OverlayRenderer(summary.Video.Width, summary.Video.Height,
			derivedFrames[0].Raw.AltitudeMeters, layout, derivedFrames, summary.Telemetry?.MaxSpeedKmh ?? 0,
			settings.ShowWatermark, summary.CameraModel, summary.ContainerRecordingStartUtc,
			settings.MapTileUrlTemplate, settings.MapAttribution, settings.MapShowAttribution, settings.MapApiKey,
			RouteIntroSettings.ForRecording(settings, _hasGpsFix));

		_video = video;
		_renderer = renderer;

		// Map/RouteIntro tiles are fetched in the background (see PrepareMapInBackgroundAsync/
		// PrepareRouteIntroInBackgroundAsync below) instead of being awaited here - both widgets are on
		// by default, and awaiting a possibly-slow or unreachable tile server before showing even the
		// first frame made opening ANY file hang on network I/O with no way to tell "still fetching" from
		// "frozen". The first frame now shows immediately (Map/RouteIntro drawing their normal "no data
		// yet" placeholder - see DrawMapWidget/DrawRouteIntroMap), then recomposes once tiles land, same
		// UX SetLayout already gives a live MapWidget toggle.
		ComposedPreviewFrame? first =
			await Task.Run(() => DecodeSeekCore(video, renderer, derivedFrames, TimeSpan.Zero, CancellationToken.None));
		if (first is not null) Publish(first);

		if (layout.Any(e => e is MapWidgetElement { Visible: true }))
		{
			_mapPrepareCts?.Cancel();
			var mapCts = new CancellationTokenSource();
			_mapPrepareCts = mapCts;
			_ = PrepareMapInBackgroundAsync(renderer, mapCts.Token);
		}

		if (settings.ShowRouteIntro && _hasGpsFix)
		{
			_routeIntroPrepareCts?.Cancel();
			var routeIntroCts = new CancellationTokenSource();
			_routeIntroPrepareCts = routeIntroCts;
			_ = PrepareRouteIntroInBackgroundAsync(renderer, routeIntroCts.Token);
		}
	}

	public void Close()
	{
		_playbackCts?.Cancel();
		_playbackCts = null;
		_scrubCts?.Cancel();
		_scrubCts = null;
		_mapPrepareCts?.Cancel();
		_mapPrepareCts = null;
		_routeIntroPrepareCts?.Cancel();
		_routeIntroPrepareCts = null;
		_resumeAfterScrub = false;

		lock (_lock)
		{
			_video = null;
			_renderer?.Dispose();
			_renderer = null;
			_lastVideoFrame = null;
			_overlayBuffer = null;
		}

		_derivedFrames = null;
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

		_scrubCts?.Cancel();

		var cts = new CancellationTokenSource();
		_playbackCts = cts;
		_ = RunPlaybackAsync(_video, _renderer, _derivedFrames, fromPosition, cts);
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

	public void EndScrubDrag(TimeSpan currentPosition)
	{
		if (!_resumeAfterScrub) return;
		_resumeAfterScrub = false;

		if (_playbackCts is not null) return;
		Play(currentPosition);
	}

	/// <summary>
	///     Swaps the live layout (drag/visibility edits from the GUI) and, if a frame is already
	///     cached, instantly recomposes it - no ffmpeg round-trip, so dragging stays smooth.
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

	public async Task RequestSeekAsync(TimeSpan position)
	{
		if (_video is null || _renderer is null || _derivedFrames is null) return;
		VideoFrameSource video = _video;
		OverlayRenderer renderer = _renderer;
		IReadOnlyList<DerivedFrame> frames = _derivedFrames;

		_scrubCts?.Cancel();
		var cts = new CancellationTokenSource();
		_scrubCts = cts;
		CancellationToken ct = cts.Token;

		// A newer seek superseding this one is expected, frequent behavior while dragging the
		// slider, not an exceptional one - cancellation here is a plain cooperative check (see
		// VideoFrameSource.GetFrame) rather than a thrown OperationCanceledException.
		await Task.Delay(ScrubDebounceMs);
		if (ct.IsCancellationRequested) return;

		ComposedPreviewFrame? composed;
		try
		{
			composed = await Task.Run(() => DecodeSeekCore(video, renderer, frames, position, ct));
		}
		catch (Exception ex)
		{
			Message?.Invoke($"Preview seek failed: {ex.Message}");
			return;
		}

		if (composed is not null && !ct.IsCancellationRequested) Publish(composed);
	}

	/// <summary>
	///     Decode/compose and pacing/display are two separate concerns run on two separate threads,
	///     joined by a small bounded channel (ProduceFramesAsync is the writer, the loop below is the
	///     sole reader): the producer decodes flat-out, ahead of what's currently on screen, so a
	///     transient decode hiccup (the first frames after a fresh ffmpeg process, a slow disk read, a
	///     GC pause) gets absorbed by frames already sitting in the channel instead of reaching the
	///     screen as a stutter. This replaced an earlier single-threaded decode-then-wait-then-display
	///     loop that had no way to hide a slow frame, plus a "Stale" catch-up mechanism that silently
	///     dropped a whole burst of frames after any slow open/seek - both are gone now; backpressure
	///     from the channel's bounded capacity is what keeps the producer from running away, and
	///     nothing needs to be discarded to "catch up" from a slow start.
	/// </summary>
	private async Task RunPlaybackAsync(VideoFrameSource video, OverlayRenderer renderer,
		IReadOnlyList<DerivedFrame> frames, TimeSpan startPosition, CancellationTokenSource ownCts)
	{
		CancellationToken ct = ownCts.Token;

		try
		{
			using VideoPlaybackStream stream = await Task.Run(() => video.OpenPlaybackStream(startPosition, ct), ct);
			// See HighResolutionTimer - default Windows timer resolution otherwise makes the Task.Delay
			// below overshoot by several ms per frame, which is most of this loop's entire real-time
			// budget on a demanding (e.g. 4K60) source.
			using var highResTimer = new HighResolutionTimer();

			var channel = Channel.CreateBounded<ComposedPreviewFrame>(
				new BoundedChannelOptions(PlaybackPrefetchFrames) { SingleReader = true, SingleWriter = true });

			// Linked, not just `ct` directly: if the consumer loop below stops for a reason that has
			// nothing to do with `ct` (e.g. a FrameReady subscriber throws), the producer can otherwise
			// be left blocked forever on a full channel nobody is draining anymore - awaiting it in the
			// consumer's finally would then hang too. Cancelling this in that finally, unconditionally,
			// guarantees the producer can always be unblocked regardless of why the consumer stopped.
			using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			Task producer = Task.Run(
				() => ProduceFramesAsync(stream, renderer, frames, video.Duration, channel.Writer, producerCts.Token),
				producerCts.Token);

			try
			{
				// Not started until the first frame actually arrives - opening the stream and an
				// accurate -ss seek into the middle of a segment can each take a real chunk of
				// wall-clock time on their own, and none of that should be charged against the very
				// first delay calculation below.
				Stopwatch? sw = null;
				TimeSpan clockBase = startPosition;

				await foreach (ComposedPreviewFrame composed in channel.Reader.ReadAllAsync(ct))
				{
					if (sw is null)
					{
						sw = Stopwatch.StartNew();
						clockBase = composed.Position;
					}
					else
					{
						TimeSpan delay = composed.Position - clockBase - sw.Elapsed;
						if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
					}

					Publish(composed);
				}
			}
			finally
			{
				// Must finish before `stream` is disposed below (`using`) - the producer may still
				// be mid-read on it otherwise, racing the process kill/dispose against that read.
				// Any real failure was already observed via the channel (ReadAllAsync rethrows it),
				// so a second throw here would only be a duplicate.
				producerCts.Cancel();
				try
				{
					await producer;
				}
				catch
				{
				}
			}

			if (stream.Position < video.Duration - TimeSpan.FromSeconds(1))
			{
				var stderr = await stream.StderrTask;
				if (!string.IsNullOrWhiteSpace(stderr)) Message?.Invoke($"ffmpeg stopped early: {stderr}");
			}
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

	private async Task ProduceFramesAsync(VideoPlaybackStream stream, OverlayRenderer renderer,
		IReadOnlyList<DerivedFrame> frames, TimeSpan duration, ChannelWriter<ComposedPreviewFrame> writer,
		CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				ComposedPreviewFrame? composed;
				lock (_lock)
				{
					VideoFrame? videoFrame = stream.TryReadNextFrame();
					composed = videoFrame is null ? null : Compose(renderer, frames, videoFrame, stream.Position);
				}

				if (composed is null || composed.Position >= duration) break;

				// Backpressure, not held under _lock - once the channel is full this waits for the
				// consumer to drain a slot, which can legitimately take a while (that's the pacing
				// working as intended), and _lock is needed by SetLayout/scrub/etc. in the meantime.
				await writer.WriteAsync(composed, ct);
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

	private ComposedPreviewFrame? DecodeSeekCore(VideoFrameSource video, OverlayRenderer renderer,
		IReadOnlyList<DerivedFrame> frames, TimeSpan position, CancellationToken ct)
	{
		lock (_lock)
		{
			VideoFrame? videoFrame = video.GetFrame(position, ct);
			return videoFrame is null ? null : Compose(renderer, frames, videoFrame, position);
		}
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

		DerivedFrame frame = TelemetryProcessor.FindNearest(frames, position.TotalSeconds);
		var overlaySize = renderer.FrameBufferSize(videoFrame.Width, videoFrame.Height);
		if (_overlayBuffer?.Length != overlaySize) _overlayBuffer = new byte[overlaySize];
		renderer.RenderInto(frame, _overlayBuffer, videoFrame.Width, videoFrame.Height, true);
		var composed = _composedBuffers.Rent(videoFrame.Width * videoFrame.Height * 4);
		PreviewCompositor.Compose(composed, videoFrame.Width, videoFrame.Height, videoFrame.Bgra,
			videoFrame.Stride, _overlayBuffer, videoFrame.Width, videoFrame.Height);

		return new ComposedPreviewFrame(position, composed);
	}
}
