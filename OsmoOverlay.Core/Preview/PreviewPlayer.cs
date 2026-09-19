using System.Diagnostics;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using MapMosaicKey = (string Url, int Zoom, double MaxZoomOutFactor);

namespace OsmoOverlay.Core.Preview;

public sealed record ComposedPreviewFrame(TimeSpan Position, byte[] Bgra);

public sealed class PreviewPlayer : IDisposable
{
	private const int ScrubDebounceMs = 80;

	private readonly Lock _lock = new();
	private IReadOnlyList<DerivedFrame>? _derivedFrames;
	private bool _hasContainerTime;
	private bool _hasGpsFix;
	private bool _hasGpsTimestamp;
	private TimeSpan _lastPosition;
	private VideoFrame? _lastVideoFrame;
	private CancellationTokenSource? _mapPrepareCts;
	private CancellationTokenSource? _playbackCts;
	private OverlayRenderer? _renderer;
	private bool _resumeAfterScrub;
	private CancellationTokenSource? _scrubCts;
	private VideoFrameSource? _video;

	public bool IsPlaying => _playbackCts is not null;
	public TimeSpan Duration => _video?.Duration ?? TimeSpan.Zero;

	public void Dispose()
	{
		Close();
	}

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
		var smoothGps = OverlaySettingsStore.Load().SmoothGpsMotion;
		List<DerivedFrame> derivedFrames = TelemetryProcessor.Process(rawFrames, smoothGps);
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
		var showWatermark = OverlaySettingsStore.Load().ShowWatermark;
		var renderer = new OverlayRenderer(summary.Video.Width, summary.Video.Height,
			derivedFrames[0].Raw.AltitudeMeters, layout, derivedFrames, summary.Telemetry?.MaxSpeedKmh ?? 0, showWatermark,
			summary.CameraModel, summary.ContainerRecordingStartUtc);

		if (layout.Any(e => e is { Type: OverlayElementType.MapWidget, Visible: true }))
			await renderer.PrepareMapAsync((fetched, total) =>
				Message?.Invoke($"Fetching map tiles: {fetched}/{total}"));

		_video = video;
		_renderer = renderer;

		ComposedPreviewFrame? first =
			await Task.Run(() => DecodeSeekCore(video, renderer, derivedFrames, TimeSpan.Zero, CancellationToken.None));
		if (first is not null) FrameReady?.Invoke(first);
	}

	public void Close()
	{
		_playbackCts?.Cancel();
		_playbackCts = null;
		_scrubCts?.Cancel();
		_scrubCts = null;
		_mapPrepareCts?.Cancel();
		_mapPrepareCts = null;
		_resumeAfterScrub = false;

		lock (_lock)
		{
			_video = null;
			_renderer?.Dispose();
			_renderer = null;
			_lastVideoFrame = null;
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
				if (composed is not null) FrameReady?.Invoke(composed);
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
			if (composed is not null) FrameReady?.Invoke(composed);
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
			if (composed is not null) FrameReady?.Invoke(composed);
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

		if (composed is not null && !ct.IsCancellationRequested) FrameReady?.Invoke(composed);
	}

	private async Task RunPlaybackAsync(VideoFrameSource video, OverlayRenderer renderer,
		IReadOnlyList<DerivedFrame> frames, TimeSpan startPosition, CancellationTokenSource ownCts)
	{
		CancellationToken ct = ownCts.Token;

		try
		{
			using VideoPlaybackStream stream = await Task.Run(() => video.OpenPlaybackStream(startPosition), ct);
			// See HighResolutionTimer - default Windows timer resolution otherwise makes the Task.Delay
			// below overshoot by several ms per frame, which is most of this loop's entire real-time
			// budget on a demanding (e.g. 4K60) source.
			using var highResTimer = new HighResolutionTimer();

			// Started only once frames can actually flow, not before - OpenPlaybackStream spawns ffmpeg
			// and waits for the process/pipe to be ready, which alone can take a real chunk of time
			// (hwaccel init especially). Starting the clock any earlier would count that startup delay
			// as "playback already behind real-time", dropping a burst of otherwise-fine early frames
			// as stale before the very first one is even shown.
			var sw = Stopwatch.StartNew();

			while (!ct.IsCancellationRequested)
			{
				TimeSpan dueBy = startPosition + sw.Elapsed;
				(DecodeStatus status, ComposedPreviewFrame? composed) =
					await Task.Run(() => DecodeNextCore(stream, renderer, frames, dueBy), ct);

				if (status == DecodeStatus.EndOfStream)
				{
					if (stream.Position < video.Duration - TimeSpan.FromSeconds(1))
					{
						var stderr = await stream.StderrTask;
						if (!string.IsNullOrWhiteSpace(stderr)) Message?.Invoke($"ffmpeg stopped early: {stderr}");
					}

					break;
				}

				if (status == DecodeStatus.Stale) continue;

				if (composed is null || composed.Position >= video.Duration) break;

				TimeSpan delay = composed.Position - startPosition - sw.Elapsed;
				if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);

				FrameReady?.Invoke(composed);
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

	private ComposedPreviewFrame? DecodeSeekCore(VideoFrameSource video, OverlayRenderer renderer,
		IReadOnlyList<DerivedFrame> frames, TimeSpan position, CancellationToken ct)
	{
		lock (_lock)
		{
			VideoFrame? videoFrame = video.GetFrame(position, ct);
			return videoFrame is null ? null : Compose(renderer, frames, videoFrame, position);
		}
	}

	private (DecodeStatus Status, ComposedPreviewFrame? Frame) DecodeNextCore(VideoPlaybackStream stream,
		OverlayRenderer renderer, IReadOnlyList<DerivedFrame> frames, TimeSpan staleBefore)
	{
		lock (_lock)
		{
			VideoFrame? videoFrame = stream.TryReadNextFrame();
			if (videoFrame is null) return (DecodeStatus.EndOfStream, null);

			TimeSpan position = stream.Position;
			if (position < staleBefore) return (DecodeStatus.Stale, null);

			return (DecodeStatus.Ready, Compose(renderer, frames, videoFrame, position));
		}
	}

	private ComposedPreviewFrame? Compose(OverlayRenderer renderer, IReadOnlyList<DerivedFrame> frames,
		VideoFrame videoFrame, TimeSpan position)
	{
		_lastVideoFrame = videoFrame;
		_lastPosition = position;

		if (frames.Count == 0) return null;

		DerivedFrame frame = TelemetryProcessor.FindNearest(frames, position.TotalSeconds);
		var overlayBytes = renderer.Render(frame, videoFrame.Width, videoFrame.Height);
		var composed = PreviewCompositor.Compose(videoFrame.Width, videoFrame.Height, videoFrame.Bgra,
			videoFrame.Stride, overlayBytes, videoFrame.Width, videoFrame.Height);

		return new ComposedPreviewFrame(position, composed);
	}

	private enum DecodeStatus
	{
		EndOfStream,
		Stale,
		Ready
	}
}
