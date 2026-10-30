using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     The picture behind a track viewed with no recording (TrackOnlyRecording): the chosen map (TrackMapSettings) over the
///     track, in memory only (Esri's terms forbid storing it). Following, its view stays on the position and widens with the
///     speed (TrackCamera); else it shows the whole track. The track, the aircraft or a dot for the position and the provider's
///     credit are drawn onto it, as the picture is all there is for them to be seen on. It holds the map and the route only -
///     the frames come with each draw, so a change of smoothing or speed correction reaches it without a new map.
/// </summary>
public sealed class TrackBackdrop : IDisposable
{
	// Tiles around the track's own, so a view following its edge still has map in it.
	private const int FollowPaddingTiles = 3;
	private const int MaxTrailPoints = 4000;
	// A rotor's blades are drawn as far back as a camera's exposure would smear them, in this many copies.
	private const double Exposure = 1 / 30.0;
	private const int Ghosts = 6;

	private static readonly SKSamplingOptions MapSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
	private static readonly SKColor FrontRotor = new(0xFF, 0x6A, 0x3D, 210);
	private static readonly SKColor RearRotor = new(0xE8, 0xE8, 0xEC, 190);
	private static readonly SKColor TrailPassedOutline = new(0, 0, 0, 170);
	private static readonly SKColor TrailPassedLine = new(0xFF, 0xB0, 0x20);
	private static readonly SKColor TrailAheadOutline = new(0, 0, 0, 60);
	private static readonly SKColor TrailAheadLine = new(0xFF, 0xB0, 0x20, 105);
	private static readonly SKColor Hub = new(0x22, 0x22, 0x26);
	private static readonly SKColor Blade = new(0x18, 0x18, 0x1C);

	private readonly Lock _lock = new();
	// Drawn once per frame at 30 fps, so kept - only what depends on the picture's size is set at each draw.
	private readonly SKPaint _imagePaint = new() { IsAntialias = true };
	private readonly SKPaint _trailOutline = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _trailLine = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _dotRing = new() { Color = SKColors.White, IsAntialias = true };
	private readonly SKPaint _dotFill = new() { Color = new SKColor(0x2F, 0x80, 0xFF), IsAntialias = true };
	private readonly SKPaint _droneOutline = new() { Color = new SKColor(0, 0, 0, 200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _droneArm = new() { Color = new SKColor(0x30, 0x30, 0x34), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _droneFill = new() { IsAntialias = true };
	private readonly SKPaint _droneRim = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke };
	private readonly SKPaint _rotorFill = new() { IsAntialias = true };
	private readonly SKPaint _rotorRing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
	private readonly SKPaint _blade = new() { IsAntialias = true };
	private readonly SKPaint _shadowPaint = new() { Color = new SKColor(0, 0, 0, 120), IsAntialias = true };
	private readonly SKPaint _beamPaint = new() { IsAntialias = true };
	private readonly SKPaint _text = new() { Color = SKColors.White, IsAntialias = true };
	private readonly SKPaint _creditBack = new() { Color = new SKColor(0, 0, 0, 140) };
	private readonly SKFont _font = new();
	private readonly SKPath _nose = NoseOf(1);
	private readonly SKPath _beam = BeamOf();
	private readonly SKShader _beamShader = SKShader.CreateLinearGradient(new SKPoint(0, -0.3f), new SKPoint(0, -3f),
		[new SKColor(255, 255, 255, 70), new SKColor(255, 255, 255, 0)], SKShaderTileMode.Clamp);
	private readonly RouteMapMosaic _mosaic;
	private readonly string[] _creditLines;
	private readonly TrackMapSettings _options;
	private readonly int _baseWidth;
	// The route as the map's pixels, its stretches (a break is where a gap starts) and when each point was; the whole of it is the
	// dim line ahead, the part up to the time asked for is drawn over it.
	private readonly SKPoint[] _trailPoints = [];
	private readonly bool[] _trailBreaks = [];
	private readonly double[] _trailTimes = [];
	private readonly SKPath? _trail;
	// With the view fixed, the map, the line ahead and the credit are the same in every frame of a size: drawn once, copied after.
	private byte[]? _still;
	private int _stillWidth, _stillHeight;
	private IReadOnlyList<DerivedFrame>? _referenceFrames;
	private double _referenceKmh;
	private double[,]? _rotorPhases;
	private float _shadowSize;
	private bool _disposed;

	internal TrackBackdrop(RouteMapMosaic mosaic, string? attribution, TrackMapSettings options, int baseWidth, IReadOnlyList<DerivedFrame> route)
	{
		_mosaic = mosaic;
		_baseWidth = baseWidth;
		_beamPaint.Shader = _beamShader;
		_creditLines = string.IsNullOrEmpty(attribution) ? [] : attribution.Split('\n');
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

	/// <summary>The line through the first `count` points of the route, carried on to `tip` (the position now) when there is one.</summary>
	private SKPath TrailPath(int count, SKPoint? tip)
	{
		using var builder = new SKPathBuilder();
		for (int i = 0; i < count; i++)
		{
			if (_trailBreaks[i]) builder.MoveTo(_trailPoints[i]);
			else builder.LineTo(_trailPoints[i]);
		}

		if (tip is { } point && count > 0) builder.LineTo(point);
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

	private static SKPath BeamOf()
	{
		using var builder = new SKPathBuilder();
		builder.MoveTo(0, -0.3f);
		builder.LineTo(-0.85f, -3f);
		builder.LineTo(0.85f, -3f);
		builder.Close();
		return builder.Detach();
	}

	private static SKPath NoseOf(float size)
	{
		using var builder = new SKPathBuilder();
		builder.MoveTo(0, -size * 0.3f);
		builder.LineTo(size * 0.16f, size * 0.08f);
		builder.LineTo(-size * 0.16f, size * 0.08f);
		builder.Close();
		return builder.Detach();
	}

	/// <summary>Null when there is nothing to show: no fix in the track, no key for the provider, or no tile could be fetched.</summary>
	public static async Task<TrackBackdrop?> BuildAsync(IReadOnlyList<DerivedFrame> frames, OverlaySettings settings, TrackOnlyFormat format, CancellationToken ct = default)
	{
		TrackMapSettings options = TrackMapSettings.From(settings);
		MapSources sources = MapSources.From(settings);
		if (sources.MissesApiKey(options.ProviderId))
		{
			AppLogger.Warn(string.Format(Localization.CoreStrings.Map_ApiKeyMissing, sources.NameOf(options.ProviderId)));
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
			return mosaic is null ? null : new TrackBackdrop(mosaic, sources.Attribution(options.ProviderId, showLabels: options.ShowLabels), options, format.Width, route);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "The track's map backdrop couldn't be prepared - the picture stays black");
			return null;
		}
	}

	/// <summary>
	///     The picture at a time on the track's own clock, as opaque BGRA over the whole of `bgra`, drawn into it in place:
	///     the map, the track, then the position - the aircraft where the log has its attitude, else a dot - and the credit.
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
				using SKSurface? surface = SKSurface.Create(info, (IntPtr)pixels, width * 4);
				if (surface is null) return;

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
						DrawDrone(canvas, point, height / 20f, attitude, rotors);
					}
					else DrawDot(canvas, point, height);
				}
			}
		}
	}

	/// <summary>What is the same whatever the time: the map, the whole route as the dim line ahead, and the credit.</summary>
	private void DrawBase(SKCanvas canvas, SKRect window, float scale, int width, int height)
	{
		canvas.Clear(SKColors.Black);
		canvas.DrawImage(_mosaic.Image, window, new SKRect(0, 0, width, height), MapSampling, _imagePaint);
		if (_trail is not null) StrokeTrail(canvas, _trail, window, scale, height, TrailAheadOutline, TrailAheadLine);
		if (_creditLines.Length > 0) DrawCredit(canvas, width, height);
	}

	/// <summary>Draws the base once for a size (the view being fixed) into a buffer of its own; false when it couldn't be.</summary>
	private unsafe bool EnsureStill(int width, int height, SKRect window, float scale)
	{
		if (_still is not null && _stillWidth == width && _stillHeight == height) return true;

		byte[] buffer = new byte[width * height * 4];
		fixed (byte* pixels = buffer)
		{
			using SKSurface? surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)pixels, width * 4);
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

		using SKPath path = TrailPath(passed, tip);
		StrokeTrail(canvas, path, window, scale, height, TrailPassedOutline, TrailPassedLine);
	}

	private void StrokeTrail(SKCanvas canvas, SKPath path, SKRect window, float scale, int height, SKColor outline, SKColor line)
	{
		float stroke = Math.Max(3, height / 270f) / scale;
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

	/// <summary>
	///     The aircraft from above, turned to its heading (yaw, clockwise from north, the map's north being up), with a soft shadow
	///     and the camera's view ahead of it. A tilt foreshortens it as it would be seen (a bank narrows it, a pitch shortens it) and
	///     the raised side's rotors show brighter. Each rotor turns as far as its pose says (RotorSpin), the diagonal pairs the opposite
	///     ways round, and smears behind its blades like an exposure of a thirtieth of a second - the faster, the longer, the fainter
	///     the blades and the fuller the disc. Front rotors are orange, rear ones light, so the nose reads at a glance.
	/// </summary>
	private void DrawDrone(SKCanvas canvas, SKPoint center, float size, DroneAttitude attitude, ReadOnlySpan<RotorSpin.RotorPose> rotors)
	{
		float reach = size * 0.82f, rotorRadius = size * 0.46f;
		_droneOutline.StrokeWidth = size / 4.5f;
		_droneArm.StrokeWidth = size / 9f;
		_droneRim.StrokeWidth = Math.Max(1.5f, size / 24);
		_rotorRing.StrokeWidth = Math.Max(1.5f, size / 16);
		if (_shadowSize != size)
		{
			_shadowSize = size;
			_shadowPaint.MaskFilter?.Dispose();
			_shadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.16f);
		}

		canvas.Save();
		canvas.Translate(center);
		canvas.RotateDegrees((float)attitude.YawDegrees);

		canvas.Save();
		canvas.Scale(size);
		canvas.DrawPath(_beam, _beamPaint);
		canvas.Restore();

		// What the tilt does to the view from above, within what the eye reads as a lean rather than a different shape.
		float narrowing = (float)Math.Cos(AngleMath.DegToRad(Math.Clamp(attitude.RollDegrees, -55, 55)));
		float shortening = (float)Math.Cos(AngleMath.DegToRad(Math.Clamp(attitude.PitchDegrees, -55, 55)));
		canvas.Scale(narrowing, shortening);

		// The light is fixed on the screen, so the shadow is turned back by the heading.
		double yaw = AngleMath.DegToRad(attitude.YawDegrees);
		float shadowX = size * 0.12f, shadowY = size * 0.16f;
		canvas.Save();
		canvas.Translate((float)(shadowX * Math.Cos(yaw) + shadowY * Math.Sin(yaw)) / narrowing, (float)(-shadowX * Math.Sin(yaw) + shadowY * Math.Cos(yaw)) / shortening);
		foreach ((int x, int y) in RotorSpin.Corners) canvas.DrawCircle(x * reach, -y * reach, rotorRadius * 0.95f, _shadowPaint);
		canvas.DrawRoundRect(-size * 0.27f, -size * 0.38f, size * 0.54f, size * 0.76f, size * 0.2f, size * 0.2f, _shadowPaint);
		canvas.Restore();

		foreach ((int x, int y) in RotorSpin.Corners)
		{
			var end = new SKPoint(x * reach, -y * reach);
			canvas.DrawLine(0, 0, end.X, end.Y, _droneOutline);
			canvas.DrawLine(0, 0, end.X, end.Y, _droneArm);
		}

		float bladeLength = rotorRadius * 0.92f, bladeWidth = size * 0.1f;
		for (int i = 0; i < RotorSpin.Count; i++)
		{
			(int x, int y) = RotorSpin.Corners[i];
			SKColor color = y > 0 ? FrontRotor : RearRotor;
			double raised = Math.Clamp((attitude.PitchDegrees * y - attitude.RollDegrees * x) * 4, -70, 70);
			float cx = x * reach, cy = -y * reach;
			int direction = x * y > 0 ? 1 : -1;
			double pace = Math.Clamp(rotors[i].RevolutionsPerSecond / RotorSpin.TopRevolutionsPerSecond, 0, 1.3);

			_rotorFill.Color = color.WithAlpha((byte)(24 + 70 * Math.Min(pace, 1)));
			canvas.DrawCircle(cx, cy, rotorRadius, _rotorFill);
			_rotorRing.Color = color.WithAlpha((byte)Math.Clamp(190 + raised, 90, 255));
			canvas.DrawCircle(cx, cy, rotorRadius, _rotorRing);

			canvas.Save();
			canvas.Translate(cx, cy);
			double angle = rotors[i].Revolutions * 360 * direction;
			double smear = rotors[i].RevolutionsPerSecond * 360 * Exposure;
			int ghosts = smear > 3 ? Ghosts : 1;
			for (int g = ghosts - 1; g >= 0; g--)
			{
				double behind = ghosts == 1 ? 0 : smear * g / (ghosts - 1);
				double fade = ghosts == 1 ? 1 : 1 - 0.8 * g / (ghosts - 1);
				_blade.Color = Blade.WithAlpha((byte)((235 - 110 * Math.Min(pace, 1)) * fade));
				canvas.Save();
				canvas.RotateDegrees((float)(angle - behind * direction));
				canvas.DrawRoundRect(-bladeLength, -bladeWidth / 2, bladeLength * 2, bladeWidth, bladeWidth / 2, bladeWidth / 2, _blade);
				canvas.Restore();
			}

			canvas.Restore();
			_droneFill.Color = Hub;
			canvas.DrawCircle(cx, cy, rotorRadius * 0.2f, _droneFill);
		}

		_droneFill.Color = Hub;
		var body = SKRect.Create(-size * 0.27f, -size * 0.38f, size * 0.54f, size * 0.76f);
		canvas.DrawRoundRect(body, size * 0.2f, size * 0.2f, _droneFill);
		canvas.DrawRoundRect(body, size * 0.2f, size * 0.2f, _droneRim);
		_droneFill.Color = SKColors.White;
		canvas.Save();
		canvas.Scale(size);
		canvas.DrawPath(_nose, _droneFill);
		canvas.Restore();
		canvas.Restore();
	}

	private void DrawCredit(SKCanvas canvas, int width, int height)
	{
		_font.Size = Math.Max(9, height / 80f);
		float line = _font.Spacing, pad = _font.Size / 2;
		float boxWidth = _creditLines.Max(l => _font.MeasureText(l)) + pad * 2, boxHeight = line * _creditLines.Length + pad;
		canvas.DrawRect(width - boxWidth, height - boxHeight, boxWidth, boxHeight, _creditBack);
		for (int i = 0; i < _creditLines.Length; i++)
			canvas.DrawText(_creditLines[i], width - boxWidth + pad, height - boxHeight + pad / 2 + line * (i + 1) - _font.Metrics.Descent, SKTextAlign.Left, _font, _text);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			_trail?.Dispose();
			_still = null;
			_nose.Dispose();
			_beam.Dispose();
			_beamShader.Dispose();
			_font.Dispose();
			_shadowPaint.MaskFilter?.Dispose();
			foreach (SKPaint paint in new[] { _imagePaint, _trailOutline, _trailLine, _dotRing, _dotFill, _droneOutline, _droneArm, _droneFill, _droneRim, _rotorFill, _rotorRing, _blade, _shadowPaint, _beamPaint, _text, _creditBack })
				paint.Dispose();
			_mosaic.Dispose();
		}
	}
}
