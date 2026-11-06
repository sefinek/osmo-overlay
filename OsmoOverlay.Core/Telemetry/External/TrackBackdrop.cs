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
	// No mipmaps: Skia builds them for a raster image by the hundreds of MB and drops them from its small cache again, so every frame
	// paid for it (33 ms against 6 at 1080p). Half and quarter size copies (_levels) do the same job once.
	private static readonly SKSamplingOptions MapSampling = new(SKFilterMode.Linear, SKMipmapMode.None);
	private const int FollowLevels = 3;
	// The line is a soft dark edge, the color (a gradient from one point's speed to the next, so it fades instead of stepping) and a thin light core.
	private static readonly SKColor TrailEdge = new(0, 0, 0, 110);
	private static readonly SKColor TrailCore = new(255, 255, 255, 70);
	private const float EdgeWidthFactor = 1.55f, CoreWidthFactor = 0.3f;
	private const byte TrailPassedAlpha = 255, TrailAheadAlpha = 120;

	private readonly Lock _lock = new();
	// Drawn once per frame at 30 fps, so kept - only what depends on the picture's size is set at each draw.
	private readonly SKPaint _imagePaint = new() { IsAntialias = true };
	private readonly SKPaint _trailOutline = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _trailLine = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _layerPaint = new();
	private readonly SKPaint _dotRing = new() { Color = SKColors.White, IsAntialias = true };
	private readonly SKPaint _dotFill = new() { Color = new SKColor(0x2F, 0x80, 0xFF), IsAntialias = true };
	private readonly DroneModel _drone = new();
	private readonly RouteMapMosaic _mosaic;
	// The map at full, half and quarter size, for a following view that is zoomed out (see MapSampling); only the first is the mosaic's own.
	private readonly SKImage[] _levels;
	private readonly TrackMapSettings _options;
	private readonly int _baseWidth;
	// The route as the map's pixels, its stretches (a break is where a gap starts) and when each point was; the whole of it is the
	// dim line ahead, the part up to the time asked for is drawn over it.
	private readonly SKPoint[] _trailPoints = [];
	private readonly bool[] _trailBreaks = [];
	private readonly double[] _trailTimes = [];
	// The color of the line at each point (by its smoothed speed); the stretches blend from one point's to the next's.
	private readonly SKColor[] _trailColors = [];
	private readonly SKColor _baseColor;
	private readonly SKColor[] _gradientColors = new SKColor[2];
	// All of the route - for a fixed view, where it is the same in every frame.
	private readonly TrailShape? _trail;
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
		_levels = options.Follow ? HalvedImages(mosaic.Image, FollowLevels) : [mosaic.Image];
		_baseColor = SKColor.TryParse(options.TrailColor, out SKColor parsed) ? parsed : SKColor.Parse(OverlayRenderer.DefaultTrailColorHex);
		if (!options.ShowTrail) return;

		// A stretch with no fix is a break in the line, not a straight one across it.
		int step = Math.Max(1, route.Count / MaxTrailPoints);
		List<SKPoint> points = [];
		List<bool> breaks = [];
		List<double> times = [];
		List<double> speeds = [];
		for (int i = 0; i < route.Count; i++)
		{
			bool restart = i == 0 || route[i].Raw.StartsAfterGap;
			if (i % step != 0 && !restart) continue;

			points.Add(mosaic.GetPixel(route[i].Raw.Latitude, route[i].Raw.Longitude));
			breaks.Add(restart);
			times.Add(route[i].Raw.SampleTimeSeconds);
			speeds.Add(double.IsFinite(route[i].SpeedKmh) ? route[i].SpeedKmh : 0);
		}

		_trailPoints = [.. points];
		_trailBreaks = [.. breaks];
		_trailTimes = [.. times];
		if (options.ColorBySpeed)
		{
			double scaleKmh = TrackCamera.ReferenceKmh(route);
			_trailColors = [.. SmoothedSpeeds(speeds, breaks).Select(v => SpeedColorScale.At(_baseColor, v / scaleKmh))];
		}

		_trail = TrailPaths(_trailPoints.Length, null);
	}

	/// <summary>A moving average over the neighbouring points of a stretch, so GPS speed noise doesn't flip the line between colors every few points.</summary>
	private static double[] SmoothedSpeeds(List<double> speeds, List<bool> breaks)
	{
		const int Radius = 3;
		double[] result = new double[speeds.Count];
		for (int i = 0; i < speeds.Count; i++)
		{
			double sum = speeds[i];
			int count = 1;
			for (int j = i - 1; j >= Math.Max(0, i - Radius) && !breaks[j + 1]; j--) (sum, count) = (sum + speeds[j], count + 1);
			for (int j = i + 1; j <= Math.Min(speeds.Count - 1, i + Radius) && !breaks[j]; j++) (sum, count) = (sum + speeds[j], count + 1);
			result[i] = sum / count;
		}

		return result;
	}

	private static SKImage[] HalvedImages(SKImage full, int count)
	{
		var levels = new SKImage[count];
		levels[0] = full;
		for (int i = 1; i < count; i++)
		{
			SKImage previous = levels[i - 1];
			var info = new SKImageInfo(Math.Max(1, previous.Width / 2), Math.Max(1, previous.Height / 2), SKColorType.Bgra8888, SKAlphaType.Premul);
			using var surface = SKSurface.Create(info);
			surface.Canvas.DrawImage(previous, new SKRect(0, 0, info.Width, info.Height), MapSampling);
			levels[i] = surface.Snapshot();
		}

		return levels;
	}

	private readonly record struct TrailSegment(SKPoint From, SKPoint To, SKColor FromColor, SKColor ToColor);

	/// <summary>A route to stroke: the whole of it as one path (the edge, and the line when it has one color) and, colored by speed, its stretches with the colors at their ends.</summary>
	private sealed class TrailShape(SKPath path, List<TrailSegment> segments) : IDisposable
	{
		public SKPath Path { get; } = path;
		public List<TrailSegment> Segments { get; } = segments;

		public void Dispose()
		{
			Path.Dispose();
		}
	}

	/// <summary>
	///     The line through the first `count` points of the route from `from`, carried on to `tip` (the position now) when there is one. With
	///     `visible`, only the stretches that touch it are in the shape: a following view shows a small part of a long route, and
	///     stroking all of it in every frame was most of the drawing time. What shows is the same - a stretch is cut only where
	///     it is wholly outside, and the rest keeps its joins. Points closer than `minGap` to the last one kept are left out.
	/// </summary>
	private TrailShape TrailPaths(int count, SKPoint? tip, SKRect? visible = null, int from = 0, float minGap = 0)
	{
		bool bySpeed = _trailColors.Length > 0;
		List<TrailSegment> segments = [];
		using var builder = new SKPathBuilder();
		// Where the stretch being drawn ends (a point's index), so one that carries on isn't started again.
		int end = -1;
		SKPoint last = default;
		SKColor lastColor = default;
		float minGapSquared = minGap * minGap;

		for (int i = Math.Max(from, 1); i < count; i++)
		{
			if (_trailBreaks[i]) continue;

			SKPoint point = _trailPoints[i], previous = _trailPoints[i - 1];
			if (visible is { } window && !window.IntersectsWith(SKRect.Create(Math.Min(previous.X, point.X), Math.Min(previous.Y, point.Y),
				    Math.Abs(point.X - previous.X), Math.Abs(point.Y - previous.Y))))
				continue;

			if (end != i - 1)
			{
				builder.MoveTo(previous);
				(last, lastColor) = (previous, bySpeed ? _trailColors[i - 1] : default);
			}

			end = i;
			// Points closer than a pixel of the picture add vertices to the stroke and nothing to what is seen.
			if (i < count - 1 && SKPoint.DistanceSquared(point, last) < minGapSquared) continue;

			builder.LineTo(point);
			if (bySpeed) segments.Add(new TrailSegment(last, point, lastColor, _trailColors[i]));
			(last, lastColor) = (point, bySpeed ? _trailColors[i] : default);
		}

		if (tip is { } now && count > 0)
		{
			SKPoint start = _trailPoints[count - 1];
			if (end != count - 1) builder.MoveTo(start);
			builder.LineTo(now);
			if (bySpeed) segments.Add(new TrailSegment(start, now, _trailColors[count - 1], _trailColors[count - 1]));
		}

		return new TrailShape(builder.Detach(), segments);
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
				if (!still) DrawBase(canvas, window, scale, width, height, _options.Follow ? Math.Max(0, PassedPoints(trackSeconds) - 1) : 0);

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
						_drone.Draw(canvas, point, height / 20f * _options.MarkerScale, attitude, rotors, beam: true);
					}
					else DrawDot(canvas, point, height);
				}
			}
		}
	}

	/// <summary>The map and the dim line ahead: all of the route with the view fixed (the same at any time), only what is still to come while following.</summary>
	private void DrawBase(SKCanvas canvas, SKRect window, float scale, int width, int height, int aheadFrom = 0)
	{
		canvas.Clear(SKColors.Black);
		int level = scale >= 0.5f ? 0 : Math.Min(_levels.Length - 1, (int)Math.Floor(Math.Log2(1.0 / scale)));
		float factor = 1f / (1 << level);
		var source = new SKRect(window.Left * factor, window.Top * factor, window.Right * factor, window.Bottom * factor);
		canvas.DrawImage(_levels[level], source, new SKRect(0, 0, width, height), MapSampling, _imagePaint);
		if (_trail is null || !_options.ShowAhead) return;

		if (!_options.Follow)
		{
			StrokeTrail(canvas, _trail, window, scale, height, TrailAheadAlpha);
			return;
		}

		using TrailShape ahead = TrailPaths(_trailPoints.Length, null, Visible(window, scale, height), aheadFrom, 1 / scale);
		StrokeTrail(canvas, ahead, window, scale, height, TrailAheadAlpha);
	}

	/// <summary>The window in the map's pixels with a margin for the line's width, outside which no stroke can reach the picture.</summary>
	private SKRect Visible(SKRect window, float scale, int height)
	{
		float margin = StrokeWidth(scale, height) * 2;
		return SKRect.Inflate(window, margin, margin);
	}

	/// <summary>The line's width in the map's pixels: its own (of a 1080-pixel-high picture) in the picture's, whatever the zoom.</summary>
	private float StrokeWidth(float scale, int height)
	{
		return _options.TrailWidth * height / 1080f / scale;
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

		using TrailShape shape = TrailPaths(passed, tip, _options.Follow ? Visible(window, scale, height) : null, 0, 1 / scale);
		StrokeTrail(canvas, shape, window, scale, height, TrailPassedAlpha);
	}

	private const int SolidColorTolerance = 4;

	private static bool IsNearlyOneColor(SKColor a, SKColor b)
	{
		return Math.Abs(a.Red - b.Red) <= SolidColorTolerance && Math.Abs(a.Green - b.Green) <= SolidColorTolerance && Math.Abs(a.Blue - b.Blue) <= SolidColorTolerance;
	}

	private static SKColor Average(SKColor a, SKColor b)
	{
		return new SKColor((byte)((a.Red + b.Red) / 2), (byte)((a.Green + b.Green) / 2), (byte)((a.Blue + b.Blue) / 2), (byte)((a.Alpha + b.Alpha) / 2));
	}

	private void StrokeTrail(SKCanvas canvas, TrailShape shape, SKRect window, float scale, int height, byte lineAlpha)
	{
		float stroke = StrokeWidth(scale, height);
		canvas.Save();
		canvas.Scale(scale);
		canvas.Translate(-window.Left, -window.Top);

		// A part is drawn opaque into a layer and made translucent as a whole: round caps of neighbouring stretches overlap,
		// and translucent strokes would add up there into dots along the line.
		bool layered = lineAlpha < 255;
		if (layered)
		{
			_layerPaint.Color = SKColors.White.WithAlpha(lineAlpha);
			canvas.SaveLayer(_layerPaint);
		}

		_trailOutline.StrokeWidth = stroke * EdgeWidthFactor;
		_trailOutline.Color = TrailEdge;
		canvas.DrawPath(shape.Path, _trailOutline);

		_trailLine.StrokeWidth = stroke;
		if (shape.Segments.Count == 0)
		{
			_trailLine.Color = _baseColor;
			canvas.DrawPath(shape.Path, _trailLine);
		}
		else
		{
				// In the route's order, so where it crosses itself the later stretch is on top. A stretch whose ends are about one color is
				// drawn plain: a gradient shader per stretch is the costly part, and most stretches of a smoothed route are that close.
				foreach (TrailSegment segment in shape.Segments)
				{
					if (IsNearlyOneColor(segment.FromColor, segment.ToColor) || segment.From == segment.To)
					{
						_trailLine.Color = Average(segment.FromColor, segment.ToColor);
						canvas.DrawLine(segment.From, segment.To, _trailLine);
						continue;
					}

					_gradientColors[0] = segment.FromColor;
					_gradientColors[1] = segment.ToColor;
					using SKShader shader = SKShader.CreateLinearGradient(segment.From, segment.To, _gradientColors, SKShaderTileMode.Clamp);
					_trailLine.Shader = shader;
					canvas.DrawLine(segment.From, segment.To, _trailLine);
					_trailLine.Shader = null;
				}
		}

		_trailOutline.StrokeWidth = stroke * CoreWidthFactor;
		_trailOutline.Color = TrailCore;
		canvas.DrawPath(shape.Path, _trailOutline);

		if (layered) canvas.Restore();
		canvas.Restore();
	}

	private void DrawDot(SKCanvas canvas, SKPoint point, int height)
	{
		float radius = Math.Max(6, height / 120f) * _options.MarkerScale;
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
			for (int i = 1; i < _levels.Length; i++) _levels[i].Dispose();
			_still = null;
			_drone.Dispose();
			foreach (SKPaint paint in new[] { _imagePaint, _trailOutline, _trailLine, _layerPaint, _dotRing, _dotFill })
				paint.Dispose();
			_mosaic.Dispose();
		}
	}
}
