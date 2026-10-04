using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Puts the overlay on the preview's video frames, the way a render of the current cuts will come out: inside kept
///     pieces from telemetry mapped onto the output's timeline (OutputTimeline - route intro at the first kept frame,
///     distance counting only kept parts), cut-out moments as the plain video. Owns the renderer, which isn't
///     thread-safe, so everything that touches it takes the one lock - the playback compose thread, seeks on the
///     thread pool and the UI thread's live edits.
///     Two ways a frame is composed: playback draws the overlay straight onto the decoded frame (ComposeInPlace -
///     nothing copied), a still keeps the decoded frame clean (ShowStill) so a layout edit, a map landing or a
///     setting can put the overlay on it again (Recompose) without decoding anything.
/// </summary>
internal sealed class OverlayCompositor : IDisposable
{
	private readonly Lock _lock = new();
	private readonly FrameBufferPool _pool;
	private readonly IReadOnlyList<DerivedFrame> _recordingFrames;
	private readonly OverlayAvailability _availability;
	private readonly double _recordingDurationSeconds;
	private IReadOnlyList<DerivedFrame> _frames;
	private OutputTimeline? _timeline;
	private bool _showOverlay;
	private VideoFrame? _still;
	private TimeSpan _stillPosition;
	private bool _disposed;
	// MeasureElement's results for the frame measured last - elements are immutable records, so an edited widget is a new
	// key; anything changing how the renderer draws (Change, SetTimeline) empties it.
	private readonly Dictionary<OverlayElement, SKRect?> _measured = new(ReferenceEqualityComparer.Instance);
	private DerivedFrame? _measuredFrame;

	/// <param name="recordingFrames">Telemetry on the recording's own timeline - mapped per the output timeline from here on.</param>
	/// <param name="recordingDurationSeconds">The whole recording's length - the output's while nothing is cut.</param>
	public OverlayCompositor(Func<IReadOnlyList<DerivedFrame>, OverlayRenderer> createRenderer, IReadOnlyList<DerivedFrame> recordingFrames,
		double recordingDurationSeconds, OverlayAvailability availability, OutputTimeline? timeline, bool showOverlay, FrameBufferPool pool)
	{
		_recordingFrames = recordingFrames;
		_recordingDurationSeconds = recordingDurationSeconds;
		_availability = availability;
		_timeline = timeline;
		_showOverlay = showOverlay;
		_pool = pool;
		_frames = MapToOutput(timeline);
		Renderer = createRenderer(_frames);
		Renderer.OutputDurationSeconds = OutputDuration(timeline);
	}

	/// <summary>
	///     For the map fetches, which read the layout and telemetry on the UI thread before going off to the network:
	///     only the UI thread changes those, and only through this class. Anything else goes through Change.
	/// </summary>
	public OverlayRenderer Renderer { get; }

	/// <summary>Whether a snapshot (DrawOverlayOnto) draws the shadows - as a render does: unless they're off everywhere.</summary>
	public bool SnapshotShadows { get; set; } = true;

	/// <summary>The same filter every layout reaching the renderer goes through - a widget this file can't feed stays off.</summary>
	public IReadOnlyList<OverlayElement> Filter(IReadOnlyList<OverlayElement> layout)
	{
		return _availability.Apply(layout);
	}

	/// <summary>Playback started: the still no longer shows what's on screen, so nothing is composed from it until the next one.</summary>
	public void ClearStill()
	{
		lock (_lock)
		{
			if (_still is { } still) _pool.Return(still.Bgra);
			_still = null;
		}
	}

	/// <summary>The scale top a speed gauge without its own MaxSpeed gets for this recording (OverlayRenderer.AutoGaugeMaxSpeed).</summary>
	public double AutoGaugeMaxSpeed(UnitSystem units)
	{
		lock (_lock)
		{
			return Renderer.AutoGaugeMaxSpeed(units);
		}
	}

	/// <summary>See OverlayRenderer.SpeedGateSeconds.</summary>
	public double? SpeedGateSeconds(OverlayElement element)
	{
		lock (_lock)
		{
			return Renderer.SpeedGateSeconds(element);
		}
	}

	/// <summary>Changes the renderer (layout, watermark, route joins, a map mosaic) and composes the still again.</summary>
	public ComposedPreviewFrame? Change(Action<OverlayRenderer> change)
	{
		lock (_lock)
		{
			if (_disposed) return null;

			change(Renderer);
			_measured.Clear();
			return RecomposeLocked();
		}
	}

	public ComposedPreviewFrame? SetShowOverlay(bool show)
	{
		lock (_lock)
		{
			_showOverlay = show;
			return _disposed ? null : RecomposeLocked();
		}
	}

	/// <summary>New cuts: the renderer takes the newly mapped telemetry (stats like total distance change with them).</summary>
	public ComposedPreviewFrame? SetTimeline(OutputTimeline? timeline)
	{
		lock (_lock)
		{
			if (_disposed) return null;

			_timeline = timeline;
			_frames = MapToOutput(timeline);
			Renderer.SetFrames(_frames, _frames[0].Raw.AltitudeMeters, TelemetryProcessor.Summarize(_frames).MaxSpeedKmh);
			Renderer.OutputDurationSeconds = OutputDuration(timeline);
			_measured.Clear();
			return RecomposeLocked();
		}
	}

	/// <summary>A decoded frame to show paused: kept clean for Recompose, composed into a copy. Null (frame recycled) once disposed.</summary>
	public ComposedPreviewFrame? ShowStill(VideoFrame frame, TimeSpan position)
	{
		lock (_lock)
		{
			if (_disposed)
			{
				_pool.Return(frame.Bgra);
				return null;
			}

			if (_still is { } previous && !ReferenceEquals(previous, frame)) _pool.Return(previous.Bgra);
			_still = frame;
			_stillPosition = position;
			return RecomposeLocked();
		}
	}

	/// <summary>Playback: the overlay drawn onto the decoded frame itself, which becomes the composed frame. Null (recycled) once disposed.</summary>
	public ComposedPreviewFrame? ComposeInPlace(VideoFrame frame, TimeSpan position)
	{
		lock (_lock)
		{
			if (_disposed)
			{
				_pool.Return(frame.Bgra);
				return null;
			}

			DrawOverlayLocked(frame.Bgra, frame.Width, frame.Height, position);
			return new ComposedPreviewFrame(position, frame.Bgra, frame.Width, frame.Height) { Owner = _pool };
		}
	}

	/// <summary>
	///     The overlay onto a frame of any size (the full-resolution snapshot) - none where cut out or while hidden. With the
	///     shadows even when the preview leaves them out: a snapshot shows the frame as the render draws it (SnapshotShadows).
	/// </summary>
	public void DrawOverlayOnto(byte[] bgra, int width, int height, TimeSpan position)
	{
		lock (_lock)
		{
			if (_disposed) return;

			bool shadows = Renderer.DrawShadows;
			Renderer.DrawShadows = SnapshotShadows;
			try
			{
				DrawOverlayLocked(bgra, width, height, position);
			}
			finally
			{
				Renderer.DrawShadows = shadows;
			}
		}
	}

	/// <summary>
	///     What a widget draws at a position, around its anchor in reference pixels (OverlayRenderer.MeasureElement) - for
	///     the editor's hit-testing and outlines. A cut-out position measures the frame the output goes on with.
	/// </summary>
	public SKRect? MeasureElement(OverlayElement element, TimeSpan position)
	{
		lock (_lock)
		{
			if (_disposed) return null;

			double seconds = _timeline is { } timeline ? timeline.NearestOutputSeconds(position.TotalSeconds) : position.TotalSeconds;
			DerivedFrame frame = TelemetryProcessor.FindNearest(_frames, seconds);
			if (!ReferenceEquals(frame, _measuredFrame))
			{
				_measured.Clear();
				_measuredFrame = frame;
			}

			if (!_measured.TryGetValue(element, out SKRect? bounds)) _measured[element] = bounds = Renderer.MeasureElement(element, frame);
			return bounds;
		}
	}

	private ComposedPreviewFrame? RecomposeLocked()
	{
		if (_still is not { } still) return null;

		byte[] composed = _pool.Rent(still.Bgra.Length);
		Buffer.BlockCopy(still.Bgra, 0, composed, 0, still.Bgra.Length);
		DrawOverlayLocked(composed, still.Width, still.Height, _stillPosition);
		return new ComposedPreviewFrame(_stillPosition, composed, still.Width, still.Height) { Owner = _pool };
	}

	private void DrawOverlayLocked(byte[] bgra, int width, int height, TimeSpan position)
	{
		// The picture's fade into a cut comes first, so the overlay is drawn over it - as in the render.
		if (_timeline?.TransitionAt(position.TotalSeconds) is { } fade) CutTransitionFade.Apply(bgra, fade.Amount, fade.White);
		if (!_showOverlay || OutputSeconds(position) is not { } seconds) return;

		Renderer.RenderOnto(TelemetryProcessor.FindNearest(_frames, seconds), bgra, width, height);
	}

	/// <summary>Preview positions are on the recording's timeline, the telemetry on the output's - null where cut out.</summary>
	private double? OutputSeconds(TimeSpan position)
	{
		return _timeline is { } timeline ? timeline.ToOutputSeconds(position.TotalSeconds) : position.TotalSeconds;
	}

	private double OutputDuration(OutputTimeline? timeline)
	{
		return timeline?.DurationSeconds ?? _recordingDurationSeconds;
	}

	private IReadOnlyList<DerivedFrame> MapToOutput(OutputTimeline? timeline)
	{
		return timeline?.MapFrames(_recordingFrames) is { Count: > 0 } mapped ? mapped : _recordingFrames;
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			Renderer.Dispose();
			if (_still is { } still) _pool.Return(still.Bgra);
			_still = null;
		}
	}
}
