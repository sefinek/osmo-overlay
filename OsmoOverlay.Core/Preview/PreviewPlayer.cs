using System.Diagnostics;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Preview;

public sealed record ComposedPreviewFrame(TimeSpan Position, byte[] Bgra);

public sealed class PreviewPlayer : IDisposable
{
	private const int ScrubDebounceMs = 80;

	private readonly Lock _lock = new();
	private TimeSpan _lastPosition;
	private VideoFrame? _lastVideoFrame;
	private CancellationTokenSource? _playbackCts;
	private OverlayRenderer? _renderer;
	private bool _resumeAfterScrub;
	private CancellationTokenSource? _scrubCts;
	private FileSummary? _summary;
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

		if (summary.DerivedFrames is not { Count: > 0 } derivedFrames)
			throw new InvalidOperationException("File has no telemetry to preview.");

		_summary = summary;

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
		IReadOnlyList<OverlayElement> layout = presets.First(p => p.Id == activeId).Elements;
		var showWatermark = OverlaySettingsStore.Load().ShowWatermark;
		var renderer = new OverlayRenderer(summary.Video.Width, summary.Video.Height,
			derivedFrames[0].Raw.AltitudeMeters, layout, derivedFrames, summary.Telemetry?.MaxSpeedKmh ?? 0, showWatermark);

		_video = video;
		_renderer = renderer;

		ComposedPreviewFrame? first =
			await Task.Run(() => DecodeSeekCore(video, renderer, summary, TimeSpan.Zero, CancellationToken.None));
		if (first is not null) FrameReady?.Invoke(first);
	}

	public void Close()
	{
		_playbackCts?.Cancel();
		_playbackCts = null;
		_scrubCts?.Cancel();
		_scrubCts = null;
		_resumeAfterScrub = false;

		lock (_lock)
		{
			_video = null;
			_renderer?.Dispose();
			_renderer = null;
			_lastVideoFrame = null;
		}

		_summary = null;
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
		if (_video is null || _renderer is null || _summary is null) return;

		_scrubCts?.Cancel();

		var cts = new CancellationTokenSource();
		_playbackCts = cts;
		_ = RunPlaybackAsync(_video, _renderer, _summary, fromPosition, cts);
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
		lock (_lock)
		{
			if (_renderer is null) return;
			_renderer.Layout = layout;

			if (_lastVideoFrame is not { } videoFrame || _summary is null) return;
			ComposedPreviewFrame? composed = Compose(_renderer, _summary, videoFrame, _lastPosition);
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

			if (_lastVideoFrame is not { } videoFrame || _summary is null) return;
			ComposedPreviewFrame? composed = Compose(_renderer, _summary, videoFrame, _lastPosition);
			if (composed is not null) FrameReady?.Invoke(composed);
		}
	}

	public async Task RequestSeekAsync(TimeSpan position)
	{
		if (_video is null || _renderer is null || _summary is null) return;
		VideoFrameSource video = _video;
		OverlayRenderer renderer = _renderer;
		FileSummary summary = _summary;

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
			composed = await Task.Run(() => DecodeSeekCore(video, renderer, summary, position, ct));
		}
		catch (Exception ex)
		{
			Message?.Invoke($"Preview seek failed: {ex.Message}");
			return;
		}

		if (composed is not null && !ct.IsCancellationRequested) FrameReady?.Invoke(composed);
	}

	private async Task RunPlaybackAsync(VideoFrameSource video, OverlayRenderer renderer, FileSummary summary,
		TimeSpan startPosition, CancellationTokenSource ownCts)
	{
		CancellationToken ct = ownCts.Token;
		var sw = Stopwatch.StartNew();

		try
		{
			using VideoPlaybackStream stream = await Task.Run(() => video.OpenPlaybackStream(startPosition), ct);

			while (!ct.IsCancellationRequested)
			{
				TimeSpan dueBy = startPosition + sw.Elapsed;
				(DecodeStatus status, ComposedPreviewFrame? composed) =
					await Task.Run(() => DecodeNextCore(stream, renderer, summary, dueBy), ct);

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
		FileSummary summary, TimeSpan position, CancellationToken ct)
	{
		lock (_lock)
		{
			VideoFrame? videoFrame = video.GetFrame(position, ct);
			return videoFrame is null ? null : Compose(renderer, summary, videoFrame, position);
		}
	}

	private (DecodeStatus Status, ComposedPreviewFrame? Frame) DecodeNextCore(VideoPlaybackStream stream,
		OverlayRenderer renderer, FileSummary summary, TimeSpan staleBefore)
	{
		lock (_lock)
		{
			VideoFrame? videoFrame = stream.TryReadNextFrame();
			if (videoFrame is null) return (DecodeStatus.EndOfStream, null);

			TimeSpan position = stream.Position;
			if (position < staleBefore) return (DecodeStatus.Stale, null);

			return (DecodeStatus.Ready, Compose(renderer, summary, videoFrame, position));
		}
	}

	private ComposedPreviewFrame? Compose(OverlayRenderer renderer, FileSummary summary, VideoFrame videoFrame,
		TimeSpan position)
	{
		_lastVideoFrame = videoFrame;
		_lastPosition = position;

		if (summary.DerivedFrames is not { Count: > 0 } frames) return null;

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
