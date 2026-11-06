using System.Runtime.InteropServices;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     The picture behind a track viewed with no recording (TrackOnlyRecording): the chosen map (TrackMapSettings) over the
///     track, in memory only (Esri's terms forbid storing it). Following, its view stays on the position and widens with the
///     speed (TrackCamera); else it shows the whole track. The track, the aircraft or a dot for the position are drawn onto it,
///     as the picture is all there is for it to be seen on. The provider's credit is the overlay's (OverlayRenderer.BackdropMap).
///     It holds the map and the route as it was built; the frames come with each draw, so a change of smoothing or speed
///     correction reaches the marker and the camera without a new map, but not the line (a new backdrop is needed for it).
/// </summary>
public sealed class TrackBackdrop : IDisposable
{
	// Tiles around the track's own, so a view following its edge still has map in it.
	private const int FollowPaddingTiles = 3;
	private const int MaxTrailPoints = 4000;
	private static readonly SKSamplingOptions MapSampling = new(SKFilterMode.Linear, SKMipmapMode.None);
	private static readonly SKColor TrailPassedOutline = new(0, 0, 0, 170);
	private static readonly SKColor TrailPassedLine = new(0xFF, 0xB0, 0x20);
	private static readonly SKColor TrailAheadOutline = new(0, 0, 0, 60);
	private static readonly SKColor TrailAheadLine = new(0xFF, 0xB0, 0x20, 105);

	private readonly Lock _lock = new();
	// Drawn once per frame at 30 fps, so kept - only what depends on the picture's size is set at each draw.
	private readonly SKPaint _imagePaint = new() { IsAntialias = true };
	private readonly SKPaint _trailOutline = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _trailLine = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _dotRing = new() { Color = SKColors.White, IsAntialias = true };
	private readonly SKPaint _dotFill = new() { Color = new SKColor(0x2F, 0x80, 0xFF), IsAntialias = true };
	private readonly DroneModel _drone = new();
	private readonly RouteMapMosaic _mosaic;
	private readonly TrackMapSettings _options;
	private readonly int _baseWidth;
	// The route as the map's pixels, its stretches (a break is where a gap starts) and when each point was; the whole of it is the
	// dim line ahead, the part up to the time asked for is drawn over it.
	private readonly SKPoint[] _trailPoints = [];
	private readonly bool[] _trailBreaks = [];
	private readonly double[] _trailTimes = [];
	private readonly SKPath? _trail;
	// With the view fixed, the map and the line ahead are the same in every frame of a size: drawn once, copied after.
	private byte[]? _still;
	private int _stillWidth, _stillHeight;
	private IReadOnlyList<DerivedFrame>? _referenceFrames;
	private double _referenceKmh;
	private double[,]? _rotorPhases;
	private bool _disposed;
	private bool _surfaceFailureLogged;

	internal TrackBackdrop(RouteMapMosaic mosaic, TrackMapSettings options, int baseWidth, IReadOnlyList<DerivedFrame> route)
	{
		_mosaic = mosaic;
		_baseWidth = baseWidth;
		_options = options;
		if (!options.ShowTrail) return;

		// A stretch with no fix is a break in the line, not a straight one across it.
		int step = Math.Max(1, route.Count / MaxTrailPoints);
		List<SKPoint> points = [];
		List<bool> breaks = [];
		List<double> times = [];
		for (int i = 0; i < route.Count; i++)
		{
			bool restart = i == 0 || route[i].Raw.StartsAfterGap;
			if (i % step != 0 && !restart) continue;

			points.Add(mosaic.GetPixel(route[i].Raw.Latitude, route[i].Raw.Longitude));
			breaks.Add(restart);
			times.Add(route[i].Raw.SampleTimeSeconds);
		}

		_trailPoints = [.. points];
		_trailBreaks = [.. breaks];
		_trailTimes = [.. times];
		_trail = TrailPath(_trailPoints.Length, null);
	}

	/// <summary>
	///     The line through the first `count` points of the route, carried on to `tip` (the position now) when there is one. With
	///     `visible`, only the stretches that touch it are in the path: a following view shows a small part of a long route, and
	///     stroking all of it in every frame was most of the drawing time. What shows is the same - a stretch is cut only where
	///     it is wholly outside, and the rest keeps its joins.
	/// </summary>
	private SKPath TrailPath(int count, SKPoint? tip, SKRect? visible = null)
	{
		using var builder = new SKPathBuilder();
		bool open = false;
		for (int i = 0; i < count; i++)
		{
			SKPoint point = _trailPoints[i];
			if (_trailBreaks[i])
			{
				if (visible is { } view && !view.Contains(point))
				{
					open = false;
					continue;
				}

				builder.MoveTo(point);
				open = true;
				continue;
			}

			SKPoint previous = _trailPoints[i - 1];
			if (visible is { } window && !window.IntersectsWith(SKRect.Create(Math.Min(previous.X, point.X), Math.Min(previous.Y, point.Y),
				    Math.Abs(point.X - previous.X), Math.Abs(point.Y - previous.Y))))
			{
				open = false;
				continue;
			}

			if (!open) builder.MoveTo(previous);
			builder.LineTo(point);
			open = true;
		}

		if (tip is { } end && count > 0)
		{
			if (!open) builder.MoveTo(_trailPoints[count - 1]);
			builder.LineTo(end);
		}

		return builder.Detach();
	}

	/// <summary>How many points of the route are at or before a time.</summary>
	private int PassedPoints(double seconds)
	{
		int low = 0, high = _trailTimes.Length;
		while (low < high)
		{
			int middle = (low + high) / 2;
			if (_trailTimes[middle] <= seconds) low = middle + 1;
			else high = middle;
		}

		return low;
	}

	/// <summary>Null when there is nothing to show: no fix in the track, no key for the provider, or no tile could be fetched.</summary>
	public static async Task<TrackBackdrop?> BuildAsync(IReadOnlyList<DerivedFrame> frames, OverlaySettings settings, TrackOnlyFormat format, CancellationToken ct = default)
	{
		var options = TrackMapSettings.From(settings);
		var sources = MapSources.From(settings);
		if (sources.MissesApiKey(options.ProviderId))
		{
			AppLogger.Warn(string.Format(CoreStrings.Map_ApiKeyMissing, sources.NameOf(options.ProviderId)));
			return null;
		}

		// The processed frames, not the file's points: the repaired ones, so a GPS spike neither stretches the map nor draws into the line.
		List<DerivedFrame> route = [.. frames.Where(f => f.Raw.HasGpsFix && double.IsFinite(f.Raw.Latitude) && double.IsFinite(f.Raw.Longitude))];
		if (route.Count == 0) return null;
		List<(double Lat, double Lon)> points = [.. route.Select(f => (f.Raw.Latitude, f.Raw.Longitude))];

		try
		{
			string? labels = sources.LabelsUrlTemplate(options.ProviderId, showLabels: options.ShowLabels);
			RouteMapMosaic? mosaic = await RouteMapMosaic.BuildAsync(points, sources.UrlTemplate(options.ProviderId), options.MaxZoom,
				options.Follow ? FollowPaddingTiles : 0, ct, null, options.Follow ? null : format.Width / (double)format.Height,
				sources.CacheMaxAge(options.ProviderId), labels, options.Follow ? null : (format.Width / 2.0, format.Height / 2.0));
			return mosaic is null ? null : new TrackBackdrop(mosaic, options, format.Width, route);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "The track's map backdrop couldn't be prepared - the picture stays black");
			return null;
		}
	}

	/// <summary>
	///     The picture at a time on the track's own clock, as opaque BGRA over the whole of `bgra`, drawn into it in place:
	///     the map, the track, then the position - the aircraft where the log has its attitude, else a dot.
	///     `frames` are the track's processed ones (ExternalTrackPlayback.DerivedFrames).
	/// </summary>
	public unsafe void DrawInto(byte[] bgra, int width, int height, double trackSeconds, IReadOnlyList<DerivedFrame> frames)
	{
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		lock (_lock)
		{
			if (_disposed) return;

			if (!ReferenceEquals(_referenceFrames, frames))
			{
				_referenceFrames = frames;
				_referenceKmh = TrackCamera.ReferenceKmh(frames);
				_rotorPhases = null;
			}

			SKRect window = Window(frames, trackSeconds, width, height);
			float scale = width / window.Width;
			bool still = !_options.Follow && EnsureStill(width, height, window, scale);
			if (still) Buffer.BlockCopy(_still!, 0, bgra, 0, width * height * 4);

			fixed (byte* pixels = bgra)
			{
				using var surface = SKSurface.Create(info, (IntPtr)pixels, width * 4);
				if (surface is null)
				{
					if (!_surfaceFailureLogged)
					{
						_surfaceFailureLogged = true;
						AppLogger.Warn("The track's backdrop couldn't draw onto the frame - frames are left black");
					}

					MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, width * height * 4)).Fill(0xFF000000);
					return;
				}

				SKCanvas canvas = surface.Canvas;
				if (!still) DrawBase(canvas, window, scale, width, height);

				TelemetryFrame? fix = TrackCamera.FixAt(frames, trackSeconds);
				SKPoint? tip = fix is null ? null : _mosaic.GetPixel(fix.Latitude, fix.Longitude);
				if (_trail is not null) DrawPassedTrail(canvas, window, scale, height, trackSeconds, tip);

				if (fix is not null)
				{
					SKPoint point = Scaled(tip!.Value, window, scale);
					if (fix.DroneAttitude is { } attitude)
					{
						Span<RotorSpin.RotorPose> rotors = stackalloc RotorSpin.RotorPose[RotorSpin.Count];
						_rotorPhases ??= RotorSpin.Phases(frames, _referenceKmh);
						RotorSpin.At(frames, _rotorPhases, trackSeconds, _referenceKmh, rotors);
						_drone.Draw(canvas, point, height / 20f, attitude, rotors, beam: true);
					}
					else DrawDot(canvas, point, height);
				}
			}
		}
	}

	/// <summary>What is the same whatever the time: the map, and the whole route as the dim line ahead.</summary>
	private void DrawBase(SKCanvas canvas, SKRect window, float scale, int width, int height)
	{
		canvas.Clear(SKColors.Black);
		canvas.DrawImage(_mosaic.Image, window, new SKRect(0, 0, width, height), MapSampling, _imagePaint);
		if (_trail is null) return;

		if (!_options.Follow)
		{
			StrokeTrail(canvas, _trail, window, scale, height, TrailAheadOutline, TrailAheadLine);
			return;
		}

		using SKPath ahead = TrailPath(_trailPoints.Length, null, Visible(window, scale, height));
		StrokeTrail(canvas, ahead, window, scale, height, TrailAheadOutline, TrailAheadLine);
	}

	/// <summary>The window in the map's pixels with a margin for the line's width, outside which no stroke can reach the picture.</summary>
	private static SKRect Visible(SKRect window, float scale, int height)
	{
		float margin = StrokeWidth(scale, height) * 2;
		return SKRect.Inflate(window, margin, margin);
	}

	private static float StrokeWidth(float scale, int height)
	{
		return Math.Max(3, height / 270f) / scale;
	}

	/// <summary>Draws the base once for a size (the view being fixed) into a buffer of its own; false when it couldn't be.</summary>
	private unsafe bool EnsureStill(int width, int height, SKRect window, float scale)
	{
		if (_still is not null && _stillWidth == width && _stillHeight == height) return true;

		byte[] buffer = new byte[width * height * 4];
		fixed (byte* pixels = buffer)
		{
			using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)pixels, width * 4);
			if (surface is null) return false;

			DrawBase(surface.Canvas, window, scale, width, height);
		}

		(_still, _stillWidth, _stillHeight) = (buffer, width, height);
		return true;
	}

	/// <summary>The part of the map in the picture, in the map's pixels - the whole view, or following, the camera's window kept inside it.</summary>
	private SKRect Window(IReadOnlyList<DerivedFrame> frames, double trackSeconds, int width, int height)
	{
		SKRect view = _mosaic.View;
		if (!_options.Follow || TrackCamera.At(frames, trackSeconds, _referenceKmh) is not { } shot) return view;

		double aspect = width / (double)height;
		double windowWidth = Math.Min(_baseWidth * (1 + (TrackCamera.MaxZoomOut - 1) * shot.Spread), Math.Min(view.Width, view.Height * aspect));
		double windowHeight = windowWidth / aspect;
		SKPoint center = _mosaic.GetPixel(shot.Latitude, shot.Longitude);
		return SKRect.Create((float)Math.Clamp(center.X - windowWidth / 2, view.Left, view.Right - windowWidth),
			(float)Math.Clamp(center.Y - windowHeight / 2, view.Top, view.Bottom - windowHeight), (float)windowWidth, (float)windowHeight);
	}

	private static SKPoint Scaled(SKPoint pixel, SKRect window, float scale)
	{
		return new SKPoint((pixel.X - window.Left) * scale, (pixel.Y - window.Top) * scale);
	}

	private void DrawPassedTrail(SKCanvas canvas, SKRect window, float scale, int height, double trackSeconds, SKPoint? tip)
	{
		int passed = PassedPoints(trackSeconds);
		if (passed == 0) return;

		using SKPath path = TrailPath(passed, tip, _options.Follow ? Visible(window, scale, height) : null);
		StrokeTrail(canvas, path, window, scale, height, TrailPassedOutline, TrailPassedLine);
	}

	private void StrokeTrail(SKCanvas canvas, SKPath path, SKRect window, float scale, int height, SKColor outline, SKColor line)
	{
		float stroke = StrokeWidth(scale, height);
		_trailOutline.StrokeWidth = stroke * 2;
		_trailOutline.Color = outline;
		_trailLine.StrokeWidth = stroke;
		_trailLine.Color = line;
		canvas.Save();
		canvas.Scale(scale);
		canvas.Translate(-window.Left, -window.Top);
		canvas.DrawPath(path, _trailOutline);
		canvas.DrawPath(path, _trailLine);
		canvas.Restore();
	}

	private void DrawDot(SKCanvas canvas, SKPoint point, int height)
	{
		float radius = Math.Max(6, height / 120f);
		canvas.DrawCircle(point, radius * 1.5f, _dotRing);
		canvas.DrawCircle(point, radius, _dotFill);
	}

	/// <summary>The map this picture shows, for its credit: drawn by the overlay (OverlayRenderer.BackdropMap), not here.</summary>
	public BackdropMap Map => new(_options.ProviderId, _options.ShowLabels);

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			_trail?.Dispose();
			_still = null;
			_drone.Dispose();
			foreach (SKPaint paint in new[] { _imagePaint, _trailOutline, _trailLine, _dotRing, _dotFill })
				paint.Dispose();
			_mosaic.Dispose();
		}
	}
}
