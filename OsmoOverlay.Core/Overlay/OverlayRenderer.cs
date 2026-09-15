using System.Globalization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

public sealed class OverlayRenderer : IDisposable
{
	private const double TrailMinStepMeters = 3.0;
	private const double KmhToMph = 0.621371;
	private const double MetersToFeet = 3.28084;
	private const double MilesInMeters = 1609.344;
	private const string DefaultDateFormat = "dd/MM/yyyy  HH:mm:ss";

	// Attribution watermark: appears at full opacity from the first frame (no fade-in), holds until
	// WatermarkFadeOutStartSeconds, then fades out over the remainder of WatermarkDurationSeconds.
	private const double WatermarkFadeOutStartSeconds = 5.0;
	private const double WatermarkDurationSeconds = 6.0;
	private const float WatermarkBottomMargin = 110f;
	private const float WatermarkLineGap = 46f;

	private static readonly SKColor White = SKColors.White;
	private static readonly SKColor Accent = new(70, 190, 255);
	private static readonly SKColor TrailColor = new(70, 220, 110);
	private static readonly SKColor SunColor = new(255, 175, 45);
	private static readonly SKColor Shadow = new(0, 0, 0, 225);
	private static readonly SKColor PanelFill = new(0, 0, 0, 55);

	private static readonly string WatermarkVersion = FormatVersion(typeof(OverlayRenderer).Assembly.GetName().Version);

	private readonly IReadOnlyList<DerivedFrame> _allFrames;
	private readonly SKFont _dateFont;
	private readonly int _height;

	private readonly SKTypeface _hudTypeface;
	private readonly SKFont _labelFont;
	private readonly double _observedMaxSpeedKmh;
	private readonly float _scale;
	private readonly SKFont _smallFont;
	private readonly SKFont _speedFont;
	private readonly SKFont _speedUnitFont;
	private readonly double _startAltitude;
	private readonly List<(double East, double North, double Lat, double Lon)> _trail = [];
	private readonly SKFont _unitFont;
	private readonly SKFont _valueFont;
	private readonly SKFont _watermarkSubtitleFont;
	private readonly SKFont _watermarkTitleFont;

	private readonly int _width;
	private (double East, double North)? _lastTrailPoint;
	private RouteMapMosaic? _mapMosaic;
	private (string Url, int Zoom)? _preparedMapKey;
	private int _trailCacheIndex = -1;

	public OverlayRenderer(int width, int height, double startAltitude, IReadOnlyList<OverlayElement> layout,
		IReadOnlyList<DerivedFrame> allFrames, double observedMaxSpeedKmh = 0, bool showWatermark = true)
	{
		_width = width;
		_height = height;
		_startAltitude = startAltitude;
		Layout = layout;
		_allFrames = allFrames;
		_observedMaxSpeedKmh = observedMaxSpeedKmh;
		_scale = OverlayElementBounds.GetScale(width, height);
		ShowWatermark = showWatermark;

		_hudTypeface = CreateHudTypeface();

		_dateFont = new SKFont(_hudTypeface, 46);
		_labelFont = new SKFont(_hudTypeface, 30);
		_valueFont = new SKFont(_hudTypeface, 95);
		_unitFont = new SKFont(_hudTypeface, 46);
		_smallFont = new SKFont(_hudTypeface, 36);
		_speedFont = new SKFont(_hudTypeface, 115);
		_speedUnitFont = new SKFont(_hudTypeface, 38);
		_watermarkTitleFont = new SKFont(_hudTypeface, 40);
		_watermarkSubtitleFont = new SKFont(_hudTypeface, 30);
	}

	/// <summary>Mutable so the GUI editor can reposition/toggle elements without rebuilding fonts.</summary>
	public IReadOnlyList<OverlayElement> Layout { get; set; }

	/// <summary>Mutable so the GUI can toggle it live from Settings without recreating the renderer.</summary>
	public bool ShowWatermark { get; set; }

	public void Dispose()
	{
		_hudTypeface.Dispose();
		_dateFont.Dispose();
		_labelFont.Dispose();
		_valueFont.Dispose();
		_unitFont.Dispose();
		_smallFont.Dispose();
		_speedFont.Dispose();
		_speedUnitFont.Dispose();
		_watermarkTitleFont.Dispose();
		_watermarkSubtitleFont.Dispose();
		_mapMosaic?.Dispose();
	}

	/// <summary>
	///     Fetches (or loads from disk cache) every map tile the route needs and stitches them into one
	///     in-memory mosaic, so DrawMapWidget can just crop/pan a window per frame instead of hitting
	///     the network on every one of potentially tens of thousands of frames. No-ops (no network call
	///     at all) when Layout has no MapWidget - most renders never touch the network. Best-effort: a
	///     failure (offline, unreachable tile server) leaves _mapMosaic null and MapWidget draws a
	///     placeholder instead of failing the render.
	/// </summary>
	public async Task PrepareMapAsync(CancellationToken ct = default)
	{
		ApplyMapMosaic(await BuildMapMosaicAsync(ct));
	}

	/// <summary>
	///     The fetch-only half of PrepareMapAsync, split out so a live-preview caller (PreviewPlayer)
	///     can run the actual network I/O without holding whatever lock serializes calls into Render -
	///     network fetches can take seconds, and Render must stay usable (scrubbing, playback) for other
	///     frames in the meantime. ApplyMapMosaic is the only part that touches renderer state, so it's
	///     the only part that needs to happen inside that lock.
	/// </summary>
	public async Task<RouteMapMosaic?> BuildMapMosaicAsync(CancellationToken ct = default)
	{
		if (Layout.FirstOrDefault(e => e.Type == OverlayElementType.MapWidget) is not { } mapElement) return null;

		List<(double Lat, double Lon)> points = _allFrames.Select(f => (f.Raw.Latitude, f.Raw.Longitude)).ToList();
		var urlTemplate = mapElement.MapTileUrlTemplate ?? MapTileFetcher.DefaultUrlTemplate;
		_preparedMapKey = (urlTemplate, mapElement.MapZoom);

		try
		{
			return await RouteMapMosaic.BuildAsync(points, urlTemplate, mapElement.MapZoom, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "Map widget: failed to prepare the route map - the widget will show a placeholder.");
			return null;
		}
	}

	public void ApplyMapMosaic(RouteMapMosaic? mosaic)
	{
		_mapMosaic?.Dispose();
		_mapMosaic = mosaic;
	}

	/// <summary>
	///     True when layout has a MapWidget whose tile URL/zoom differ from whatever PrepareMapAsync
	///     last actually fetched - lets a live-preview caller (PreviewPlayer) know a fresh call is
	///     worth making after an edit, without ever re-fetching on every unrelated layout change (e.g.
	///     dragging some other widget).
	/// </summary>
	public bool NeedsMapPrepare(IReadOnlyList<OverlayElement> layout)
	{
		if (layout.FirstOrDefault(e => e.Type == OverlayElementType.MapWidget) is not { } mapElement) return false;
		(string Url, int Zoom) key = (mapElement.MapTileUrlTemplate ?? MapTileFetcher.DefaultUrlTemplate, mapElement.MapZoom);
		return _preparedMapKey != key;
	}

	/// <summary>
	///     Rounds the recording's actual max speed up to the next 10 km/h so the gauge scale matches
	///     this ride instead of a fixed 60 km/h that's meaningless for a walk or absurdly low for a car.
	///     A 20 km/h floor keeps the needle from pinning near full-scale on a near-stationary clip.
	/// </summary>
	private static double ComputeGaugeMaxSpeed(double observedSpeed)
	{
		var rounded = Math.Ceiling(Math.Max(observedSpeed, 1) / 10.0) * 10.0;
		return Math.Max(rounded, 20.0);
	}

	/// <summary>
	///     Same "round up to a nice number" rule as ComputeGaugeMaxSpeed, but computed per unit system
	///     at draw time instead of once in km/h - converting an already-rounded km/h max into mph would
	///     produce an ugly non-round number (e.g. 60 km/h -&gt; 37.28 mph).
	/// </summary>
	private double GaugeMaxSpeed(UnitSystem units)
	{
		var observed = units == UnitSystem.Imperial ? _observedMaxSpeedKmh * KmhToMph : _observedMaxSpeedKmh;
		return ComputeGaugeMaxSpeed(observed);
	}

	private static SKTypeface CreateHudTypeface()
	{
		var style = new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
		string[] candidates = OperatingSystem.IsWindows()
			? ["Segoe UI"]
			: OperatingSystem.IsMacOS()
				? ["Helvetica Neue", "Arial"]
				: ["Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"];

		foreach (var family in candidates)
		{
			SKTypeface? typeface = SKFontManager.Default.MatchFamily(family, style);
			if (typeface is not null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
				return typeface;
			typeface?.Dispose();
		}

		return SKTypeface.FromFamilyName(null, style);
	}

	public byte[] Render(DerivedFrame frame, int? outputWidth = null, int? outputHeight = null)
	{
		UpdateTrail(frame);

		var outW = outputWidth ?? _width;
		var outH = outputHeight ?? _height;

		using var bitmap = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		canvas.Scale(outW / (float)_width, outH / (float)_height);

		foreach (OverlayElement element in Layout)
		{
			if (!element.Visible) continue;

			switch (element.Type)
			{
				case OverlayElementType.DateTimeText:
					DrawDateTime(canvas, frame, element);
					break;
				case OverlayElementType.Elevation:
					DrawElevation(canvas, frame, element);
					break;
				case OverlayElementType.Gradient:
					DrawGradient(canvas, frame, element);
					break;
				case OverlayElementType.Distance:
					DrawDistance(canvas, frame, element);
					break;
				case OverlayElementType.Compass:
					DrawCompass(canvas, frame, element.X, element.Y);
					break;
				case OverlayElementType.SunWidget:
					DrawSunWidget(canvas, frame, element.X, element.Y);
					break;
				case OverlayElementType.PitchGauge:
					DrawPitchGauge(canvas, element.X, element.Y, frame.PitchDegrees);
					break;
				case OverlayElementType.MapWidget:
					DrawMapWidget(canvas, frame, element);
					break;
				case OverlayElementType.SpeedGauge:
					DrawSpeedGauge(canvas, element, frame.SpeedKmh);
					break;
			}
		}

		if (ShowWatermark) DrawWatermark(canvas, frame.Raw.SampleTimeSeconds);

		return bitmap.Bytes;
	}

	/// <summary>
	///     The compass trail is meant to show the whole route driven so far, not just points the
	///     renderer happened to see. Scrubbing/seeking calls Render() out of chronological order, so
	///     naively appending "the current point" every call produced a scrambled path once the preview
	///     jumped around. Sequential progress (real playback/export) still extends the cached trail in
	///     O(1); any jump rebuilds it once from the start of the video - cheap since it only runs on
	///     the jump itself, not on every subsequent frame.
	/// </summary>
	private void UpdateTrail(DerivedFrame frame)
	{
		if (_allFrames.Count == 0) return;

		var index = FindFrameIndex(_allFrames, frame.Raw.SampleTimeSeconds);
		if (index == _trailCacheIndex + 1)
		{
			AppendTrailPoint(_allFrames[index]);
		}
		else if (index != _trailCacheIndex)
		{
			_trail.Clear();
			_lastTrailPoint = null;
			for (var i = 0; i <= index; i++)
				AppendTrailPoint(_allFrames[i]);
		}

		_trailCacheIndex = index;
	}

	private void AppendTrailPoint(DerivedFrame frame)
	{
		(double LocalEastMeters, double LocalNorthMeters) point = (frame.LocalEastMeters, frame.LocalNorthMeters);
		if (_lastTrailPoint is not { } last || Distance(last, point) >= TrailMinStepMeters)
		{
			_trail.Add((point.LocalEastMeters, point.LocalNorthMeters, frame.Raw.Latitude, frame.Raw.Longitude));
			_lastTrailPoint = point;
		}
	}

	private static int FindFrameIndex(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		var lo = 0;
		var hi = frames.Count - 1;
		while (lo < hi)
		{
			var mid = (lo + hi) / 2;
			if (frames[mid].Raw.SampleTimeSeconds < seconds) lo = mid + 1;
			else hi = mid;
		}

		return lo;
	}

	private static double Distance((double East, double North) a, (double East, double North) b)
	{
		double dx = a.East - b.East, dy = a.North - b.North;
		return Math.Sqrt(dx * dx + dy * dy);
	}

	private void DrawDateTime(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		CultureInfo culture = element.Locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(element.Locale);
		var dateText = frame.Raw.GpsTimestamp is { } utc
			? utc.ToLocalFromUtc().ToString(element.DateFormat ?? DefaultDateFormat, culture)
			: "--";
		DrawOutlined(canvas, dateText, 0, 0, _dateFont, White);

		canvas.Restore();
	}

	private void DrawElevation(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		var meters = frame.Raw.AltitudeMeters - _startAltitude;
		var (value, unit) = element.Units == UnitSystem.Imperial
			? (F(meters * MetersToFeet, "0"), "FT")
			: (F(meters, "0"), "M");
		DrawStat(canvas, element.Label ?? "ELEVATION", value, unit);
		canvas.Restore();
	}

	private void DrawGradient(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		DrawStat(canvas, element.Label ?? "GRADIENT", F(frame.GradientPercent, "0"), "%");
		canvas.Restore();
	}

	private void DrawDistance(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		var (distanceValue, distanceUnit) = FormatDistance(frame.CumulativeDistanceMeters, element.Units);
		DrawStat(canvas, element.Label ?? "TOTAL DISTANCE", distanceValue, distanceUnit);
		canvas.Restore();
	}

	private static (string Value, string Unit) FormatDistance(double meters, UnitSystem units)
	{
		if (units == UnitSystem.Imperial)
			return meters >= MilesInMeters
				? (F(meters / MilesInMeters, "0.00"), "MI")
				: (F(meters * MetersToFeet, "0.00"), "FT");

		return meters >= 1000
			? (F(meters / 1000.0, "0.00"), "KM")
			: (F(meters, "0.00"), "M");
	}

	private void DrawStat(SKCanvas canvas, string label, string value, string unit)
	{
		DrawOutlined(canvas, label.ToUpperInvariant(), 0, 0, _labelFont, White);
		DrawOutlined(canvas, value, 0, 90, _valueFont, Accent);
		var valueWidth = _valueFont.MeasureText(value);
		DrawOutlined(canvas, unit, valueWidth + 12, 90, _unitFont, White);
	}

	private void DrawCompass(SKCanvas canvas, DerivedFrame frame, float cx, float cy)
	{
		canvas.Save();
		canvas.Translate(cx, cy);
		canvas.Scale(_scale, _scale);
		cx = 0;
		cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.CompassRadius);

		using var fill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, fill);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, ring);

		DrawTrail(canvas, cx, cy, frame);
		DrawHeadingArrow(canvas, cx, cy, frame.HeadingDegrees);

		DrawOutlined(canvas, "N", cx, cy - OverlayElementBounds.CompassRadius + 46, _labelFont, White,
			SKTextAlign.Center);

		var headingText =
			$"{F(AngleMath.NormalizeDegrees(frame.HeadingDegrees), "0")}°{CardinalDirection(frame.HeadingDegrees)}";
		DrawOutlined(canvas, headingText, cx + OverlayElementBounds.CompassRadius * 0.55f,
			cy + OverlayElementBounds.CompassRadius * 0.7f, _labelFont, White, SKTextAlign.Right);

		canvas.Restore();
	}

	private void DrawTrail(SKCanvas canvas, float cx, float cy, DerivedFrame frame)
	{
		if (_trail.Count < 2) return;

		(double East, double North) currentPos = (frame.LocalEastMeters, frame.LocalNorthMeters);
		var maxDist = 5.0;
		foreach ((double East, double North, double Lat, double Lon) p in _trail)
		{
			var dist = Distance((p.East, p.North), currentPos);
			if (dist > maxDist) maxDist = dist;
		}

		var scale = OverlayElementBounds.CompassRadius * 0.82 / maxDist;

		var builder = new SKPathBuilder();
		var started = false;
		foreach ((double East, double North, double Lat, double Lon) p in _trail)
		{
			var px = cx + (float)((p.East - frame.LocalEastMeters) * scale);
			var py = cy - (float)((p.North - frame.LocalNorthMeters) * scale);
			if (!started)
			{
				builder.MoveTo(px, py);
				started = true;
			}
			else
			{
				builder.LineTo(px, py);
			}
		}

		using SKPath path = builder.Detach();
		using var trailPaint = new SKPaint
		{
			Color = TrailColor, IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = 4.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
		};
		canvas.DrawPath(path, trailPaint);
	}

	/// <summary>
	///     A small live map centered on the current position, north-up - panning frame to frame rather
	///     than showing the whole route at once (unlike the Compass trail), since RouteMapMosaic only
	///     covers the route's bounding box at a fixed zoom and each frame just crops a window of it.
	/// </summary>
	private void DrawMapWidget(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		const float radius = OverlayElementBounds.MapRadius;

		DrawPanelShadow(canvas, 0, 0, radius);

		if (_mapMosaic is null)
		{
			using var placeholderFill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
			canvas.DrawCircle(0, 0, radius, placeholderFill);
			DrawOutlined(canvas, "MAP", 0, -10, _labelFont, White, SKTextAlign.Center);
			DrawOutlined(canvas, "UNAVAILABLE", 0, 24, _smallFont, White, SKTextAlign.Center);
		}
		else
		{
			canvas.Save();
			var clipBuilder = new SKPathBuilder();
			clipBuilder.AddCircle(0, 0, radius);
			using (SKPath clipPath = clipBuilder.Detach())
			{
				canvas.ClipPath(clipPath, antialias: true);
			}

			SKPoint center = _mapMosaic.GetPixel(frame.Raw.Latitude, frame.Raw.Longitude);
			var src = SKRect.Create(center.X - radius, center.Y - radius, radius * 2, radius * 2);
			var dest = SKRect.Create(-radius, -radius, radius * 2, radius * 2);
			canvas.DrawBitmap(_mapMosaic.Bitmap, src, dest, SKSamplingOptions.Default);

			DrawMapTrail(canvas, center);

			canvas.Restore();

			using var dotOutline = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
			using var dotFill = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
			canvas.DrawCircle(0, 0, 9, dotOutline);
			canvas.DrawCircle(0, 0, 6, dotFill);

			DrawOutlined(canvas, element.MapAttribution ?? MapTileFetcher.DefaultAttribution, 0, radius - 16,
				_smallFont, White, SKTextAlign.Center);
		}

		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(0, 0, radius, ring);

		canvas.Restore();
	}

	/// <summary>Same route trail as the Compass, redrawn in the map's Web Mercator pixel space so it lines up with the tiles.</summary>
	private void DrawMapTrail(SKCanvas canvas, SKPoint center)
	{
		if (_trail.Count < 2 || _mapMosaic is null) return;

		var builder = new SKPathBuilder();
		var started = false;
		foreach ((double East, double North, double Lat, double Lon) p in _trail)
		{
			SKPoint pixel = _mapMosaic.GetPixel(p.Lat, p.Lon);
			var px = pixel.X - center.X;
			var py = pixel.Y - center.Y;
			if (!started)
			{
				builder.MoveTo(px, py);
				started = true;
			}
			else
			{
				builder.LineTo(px, py);
			}
		}

		using SKPath path = builder.Detach();
		using var trailPaint = new SKPaint
		{
			Color = TrailColor, IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = 4.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
		};
		canvas.DrawPath(path, trailPaint);
	}

	private void DrawHeadingArrow(SKCanvas canvas, float cx, float cy, double headingDegrees)
	{
		var rad = AngleMath.DegToRad(headingDegrees);
		float len = 42;
		var tipX = cx + (float)(Math.Sin(rad) * len);
		var tipY = cy - (float)(Math.Cos(rad) * len);
		var leftX = cx + (float)(Math.Sin(rad + 2.5) * len * 0.55);
		var leftY = cy - (float)(Math.Cos(rad + 2.5) * len * 0.55);
		var rightX = cx + (float)(Math.Sin(rad - 2.5) * len * 0.55);
		var rightY = cy - (float)(Math.Cos(rad - 2.5) * len * 0.55);

		var builder = new SKPathBuilder();
		builder.MoveTo(tipX, tipY);
		builder.LineTo(leftX, leftY);
		builder.LineTo(cx, cy);
		builder.LineTo(rightX, rightY);
		builder.Close();

		using SKPath path = builder.Detach();
		using var fill = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var outline = new SKPaint
			{ Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
		canvas.DrawPath(path, fill);
		canvas.DrawPath(path, outline);
	}

	private void DrawSunWidget(SKCanvas canvas, DerivedFrame frame, float cx, float cy)
	{
		canvas.Save();
		canvas.Translate(cx, cy);
		canvas.Scale(_scale, _scale);
		cx = 0;
		cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.SunRadius);

		using var fill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, fill);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, ring);

		var relativeAzimuthRad = AngleMath.DegToRad(frame.Sun.AzimuthDegrees - frame.HeadingDegrees);
		var elevationClamped = Math.Clamp(frame.Sun.ElevationDegrees, -20, 90);
		var radiusFactor = 1.0 - (elevationClamped + 20) / 110.0;

		var dotX = cx + (float)(Math.Sin(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);
		var dotY = cy - (float)(Math.Cos(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);

		using var sunPaint = new SKPaint { Color = SunColor, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var sunOutline = new SKPaint
			{ Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
		float sunDotRadius = frame.Sun.ElevationDegrees > 0 ? 16 : 10;
		canvas.DrawCircle(dotX, dotY, sunDotRadius, sunPaint);
		canvas.DrawCircle(dotX, dotY, sunDotRadius, sunOutline);

		var gText = $"{F(frame.SmoothedGForce, "0.0")}G";
		DrawOutlined(canvas, gText, cx, cy + OverlayElementBounds.SunRadius + 56, _labelFont, White,
			SKTextAlign.Center);

		canvas.Restore();
	}

	private void DrawPitchGauge(SKCanvas canvas, float cx, float cy, double pitchDegrees)
	{
		canvas.Save();
		canvas.Translate(cx, cy);
		canvas.Scale(_scale, _scale);
		cx = 0;
		cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.PitchRadius);
		using var fill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawCircle(cx, cy, OverlayElementBounds.PitchRadius, fill);

		using var ringPaint = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5
		};
		canvas.DrawCircle(cx, cy, OverlayElementBounds.PitchRadius, ringPaint);

		// AccelY-derived roll saturates at +-90 (see TelemetryProcessor), so doubling it here maps the
		// full physical range onto the full 360 degree ring - a level camera sits at top, and either
		// tilt direction sweeps all the way around to meet at the bottom for a full 90 degree roll.
		var clamped = Math.Clamp(pitchDegrees, -90, 90);
		var angleDeg = 270 + clamped * 2;
		var angleRad = AngleMath.DegToRad(angleDeg);
		var dotX = cx + (float)(Math.Cos(angleRad) * OverlayElementBounds.PitchRadius);
		var dotY = cy + (float)(Math.Sin(angleRad) * OverlayElementBounds.PitchRadius);

		using var dotPaint = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawCircle(dotX, dotY, 12, dotPaint);

		DrawOutlined(canvas, $"{F(pitchDegrees, "0")}°", cx, cy + 16, _labelFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	private void DrawSpeedGauge(SKCanvas canvas, OverlayElement element, double speedKmh)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		float cx = 0;
		float cy = 0;

		var imperial = element.Units == UnitSystem.Imperial;
		var displaySpeed = imperial ? speedKmh * KmhToMph : speedKmh;
		var maxDisplaySpeed = GaugeMaxSpeed(element.Units);

		var radius = OverlayElementBounds.SpeedRadius;
		var rect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
		const float startAngle = 135f;
		const float sweep = 270f;

		DrawPanelShadow(canvas, cx, cy, radius - 4);

		using var dialFill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var dialRing = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(cx, cy, radius - 4, dialFill);
		canvas.DrawCircle(cx, cy, radius - 4, dialRing);

		DrawGaugeBand(canvas, rect, startAngle, sweep * 0.45f, new SKColor(70, 200, 90));
		DrawGaugeBand(canvas, rect, startAngle + sweep * 0.45f, sweep * 0.25f, new SKColor(230, 200, 60));
		DrawGaugeBand(canvas, rect, startAngle + sweep * 0.70f, sweep * 0.18f, new SKColor(235, 140, 50));
		DrawGaugeBand(canvas, rect, startAngle + sweep * 0.88f, sweep * 0.12f, new SKColor(220, 60, 60));

		var clamped = Math.Clamp(displaySpeed, 0, maxDisplaySpeed);
		var needleAngleDeg = startAngle + sweep * (clamped / maxDisplaySpeed);
		var needleRad = AngleMath.DegToRad(needleAngleDeg);
		var needleX = cx + (float)(Math.Cos(needleRad) * (radius - 34));
		var needleY = cy + (float)(Math.Sin(needleRad) * (radius - 34));

		using var needlePaint = new SKPaint
		{
			Color = White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6,
			StrokeCap = SKStrokeCap.Round
		};
		canvas.DrawLine(cx, cy, needleX, needleY, needlePaint);

		using var hubOutline = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var hubFill = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawCircle(cx, cy, 9, hubOutline);
		canvas.DrawCircle(cx, cy, 6, hubFill);

		var speedText = F(displaySpeed, "0");
		var textWidth = _speedFont.MeasureText(speedText);
		DrawOutlined(canvas, speedText, cx - textWidth / 2, cy + radius - 90, _speedFont, White);
		DrawOutlined(canvas, imperial ? "MPH" : "KM/H", cx, cy + radius - 30, _speedUnitFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	/// <summary>
	///     Soft blurred disc drawn behind a round panel/gauge, offset slightly down, so it reads as a
	///     drop shadow lifting the widget off the video instead of floating flat on top of it.
	/// </summary>
	private static void DrawPanelShadow(SKCanvas canvas, float cx, float cy, float radius)
	{
		using var shadowPaint = new SKPaint
		{
			Color = new SKColor(0, 0, 0, 120), IsAntialias = true, Style = SKPaintStyle.Fill,
			MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius * 0.12f)
		};
		canvas.DrawCircle(cx, cy + radius * 0.06f, radius * 0.97f, shadowPaint);
	}

	private static void DrawGaugeBand(SKCanvas canvas, SKRect rect, float startAngle, float sweep, SKColor color)
	{
		using var paint = new SKPaint
		{
			Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = 20, StrokeCap = SKStrokeCap.Butt
		};
		canvas.DrawArc(rect, startAngle, sweep, false, paint);
	}

	private void DrawOutlined(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color,
		SKTextAlign align = SKTextAlign.Left, float opacity = 1f)
	{
		var dropOffset = font.Size * 0.045f;
		using var dropShadowPaint = new SKPaint
		{
			Color = new SKColor(0, 0, 0, (byte)(130 * opacity)), IsAntialias = true, Style = SKPaintStyle.Fill,
			MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.04f)
		};
		canvas.DrawText(text, x + dropOffset, y + dropOffset, align, font, dropShadowPaint);

		using var strokePaint = new SKPaint
		{
			Color = Shadow.WithAlpha((byte)(Shadow.Alpha * opacity)), IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = font.Size * 0.045f
		};
		using var fillPaint = new SKPaint
			{ Color = color.WithAlpha((byte)(color.Alpha * opacity)), IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawText(text, x, y, align, font, strokePaint);
		canvas.DrawText(text, x, y, align, font, fillPaint);
	}

	/// <summary>
	///     Bottom-center attribution watermark: full opacity from the first frame, then fades out (see
	///     WatermarkAlpha) starting at WatermarkFadeOutStartSeconds rather than cutting off abruptly.
	///     Drawn in the same 4K-reference/_scale space as every other widget so it scales consistently
	///     across resolutions.
	/// </summary>
	private void DrawWatermark(SKCanvas canvas, double sampleTimeSeconds)
	{
		var alpha = WatermarkAlpha(sampleTimeSeconds);
		if (alpha <= 0f) return;

		canvas.Save();
		canvas.Translate(_width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, "Made with OsmoOverlay", 0, -WatermarkLineGap, _watermarkTitleFont, White,
			SKTextAlign.Center, alpha);
		DrawOutlined(canvas, $"github.com/sefinek/osmo-overlay  •  v{WatermarkVersion}", 0, 0, _watermarkSubtitleFont,
			Accent, SKTextAlign.Center, alpha);

		canvas.Restore();
	}

	private static float WatermarkAlpha(double sampleTimeSeconds)
	{
		if (sampleTimeSeconds < 0 || sampleTimeSeconds >= WatermarkDurationSeconds) return 0f;
		if (sampleTimeSeconds < WatermarkFadeOutStartSeconds) return 1f;

		var fadeOutDuration = WatermarkDurationSeconds - WatermarkFadeOutStartSeconds;
		return (float)(1.0 - (sampleTimeSeconds - WatermarkFadeOutStartSeconds) / fadeOutDuration);
	}

	private static string FormatVersion(Version? version)
	{
		return version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
	}

	private static string F(double value, string format)
	{
		return value.ToString(format, CultureInfo.InvariantCulture);
	}

	private static string CardinalDirection(double heading)
	{
		string[] names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
		var index = (int)Math.Round(AngleMath.NormalizeDegrees(heading) / 45.0) % 8;
		return names[index];
	}
}
