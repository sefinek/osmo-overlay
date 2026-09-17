using System.Globalization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;
using MapMosaicKey = (string Url, int Zoom, double MaxZoomOutFactor);

namespace OsmoOverlay.Core.Overlay;

public sealed class OverlayRenderer : IDisposable
{
	private const double TrailMinStepMeters = 3.0;
	private const double KmhToMph = 0.621371;
	private const double MetersToFeet = 3.28084;
	private const double MilesInMeters = 1609.344;
	private const string DefaultDateFormat = "dd/MM/yyyy  HH:mm:ss";

	// Watermark holds at full opacity from frame 0, then fades out. When the Map widget is visible,
	// its required OSM credit follows as its own second slide (see MapAttributionSlideAlpha) rather
	// than being crammed into this block, so the two read as a short sequence, not a cluttered stack.
	private const double WatermarkFadeOutStartSeconds = 5.0;
	private const double WatermarkDurationSeconds = 6.0;
	private const double MapAttributionSlideDurationSeconds = 4.0;
	private const float WatermarkBottomMargin = 110f;
	private const float WatermarkLineGap = 46f;

	// User-configurable per widget (OverlayElement.MapDynamicZoomMaxFactor) - unlike the Compass's
	// freely-rescalable vector trail, map tiles can't zoom out losslessly forever, and how far a given
	// route needs to zoom out varies too much (a short walk vs. a long highway drive) for one fixed
	// constant. MapTrailFitFraction leaves breathing room instead of letting the trail graze the crop's
	// edge; MapZoomSmoothingSeconds keeps the crop window from visibly "breathing" as the trail grows.
	public const double MapDynamicZoomMaxFactorDefault = 5.0;
	public const double MapDynamicZoomMaxFactorMin = 1.0;
	public const double MapDynamicZoomMaxFactorMax = 15.0;
	private const double MapTrailFitFraction = 0.7;
	private const double MapZoomSmoothingSeconds = 2.5;

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
	private double? _lastMapZoomTimeSeconds;
	private (double East, double North)? _lastTrailPoint;
	private RouteMapMosaic? _mapMosaic;
	private double _mapZoomFactor = 1.0;
	private MapMosaicKey? _preparedMapKey;
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
	///     Fetches every map tile the route needs and stitches them into one in-memory mosaic, so
	///     DrawMapWidget just crops/pans a window per frame instead of hitting the network every frame.
	///     No-ops when Layout has no visible MapWidget. Best-effort: a failure leaves _mapMosaic null
	///     and MapWidget draws a placeholder instead of failing the render.
	/// </summary>
	public async Task PrepareMapAsync(Action<int, int>? onTileProgress = null, CancellationToken ct = default)
	{
		(RouteMapMosaic? mosaic, MapMosaicKey? key) = await BuildMapMosaicAsync(onTileProgress, ct);
		ApplyMapMosaic(mosaic, key);
	}

	/// <summary>
	///     The fetch-only half of PrepareMapAsync, split out so a live-preview caller (PreviewPlayer)
	///     can run the network I/O without holding the lock that serializes calls into Render - only
	///     ApplyMapMosaic touches renderer state, so only it needs that lock. Pass the returned key back
	///     into ApplyMapMosaic so a fetch superseded by a newer one (e.g. rapid zoom changes) gets
	///     discarded instead of applied.
	/// </summary>
	public async Task<(RouteMapMosaic? Mosaic, MapMosaicKey? Key)> BuildMapMosaicAsync(
		Action<int, int>? onTileProgress = null, CancellationToken ct = default)
	{
		// Visible, not just present: every preset always carries a MapWidget entry (off by default), so
		// checking existence alone would fetch and cache tiles for a widget nobody turned on.
		if (Layout.FirstOrDefault(e => e is { Type: OverlayElementType.MapWidget, Visible: true }) is not { } mapElement)
			return (null, null);

		List<(double Lat, double Lon)> points = [.. _allFrames.Select(f => (f.Raw.Latitude, f.Raw.Longitude))];
		var urlTemplate = ResolveUrlTemplate(mapElement);
		var maxFactor = ClampZoomOutFactor(mapElement.MapDynamicZoomMaxFactor);
		MapMosaicKey key = (urlTemplate, mapElement.MapZoom, maxFactor);
		_preparedMapKey = key;

		// Fetched with enough tile margin for the widget's own configured zoom-out ceiling (not just
		// the static 1x window), so a fresh fetch is never short on margin - and MaxZoomOutFactor being
		// part of the key means bumping this setting always triggers exactly such a fresh fetch.
		var paddingTiles = (int)Math.Ceiling(maxFactor);

		try
		{
			return (await RouteMapMosaic.BuildAsync(points, urlTemplate, mapElement.MapZoom, paddingTiles, ct, onTileProgress), key);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "Map widget: failed to prepare the route map - the widget will show a placeholder.");
			return (null, key);
		}
	}

	/// <summary>
	///     Applies a mosaic built by BuildMapMosaicAsync, but only if `forKey` still matches the most
	///     recently requested key (_preparedMapKey) - guards against two overlapping fetches (e.g.
	///     rapid zoom changes) where a slower, older one finishes after a newer one already has. A
	///     superseded mosaic is disposed instead of applied.
	/// </summary>
	public void ApplyMapMosaic(RouteMapMosaic? mosaic, MapMosaicKey? forKey)
	{
		if (forKey is not null && forKey != _preparedMapKey)
		{
			mosaic?.Dispose();
			return;
		}

		_mapMosaic?.Dispose();
		_mapMosaic = mosaic;
	}

	/// <summary>
	///     True when layout has a visible MapWidget whose tile URL/zoom differ from whatever
	///     PrepareMapAsync last fetched - lets a live-preview caller know a fresh fetch is worth making,
	///     without re-fetching on every unrelated layout change (e.g. dragging some other widget).
	/// </summary>
	public bool NeedsMapPrepare(IReadOnlyList<OverlayElement> layout)
	{
		if (layout.FirstOrDefault(e => e is { Type: OverlayElementType.MapWidget, Visible: true }) is not { } mapElement)
			return false;
		var maxFactor = ClampZoomOutFactor(mapElement.MapDynamicZoomMaxFactor);
		MapMosaicKey key = (ResolveUrlTemplate(mapElement), mapElement.MapZoom, maxFactor);
		return _preparedMapKey != key;
	}

	/// <summary>
	///     The tile URL template actually used for fetching: the widget's own template (or the default
	///     source) with a literal "{api_key}" placeholder filled from MapApiKey - a no-op for templates
	///     without that placeholder. Baking the key into the URL means an API key edit alone already
	///     changes this string, so NeedsMapPrepare/BuildMapMosaicAsync's key comparisons catch it for free.
	/// </summary>
	private static string ResolveUrlTemplate(OverlayElement mapElement)
	{
		var template = mapElement.MapTileUrlTemplate ?? MapTileFetcher.OpenStreetMapUrlTemplate;
		return template.Replace("{api_key}", mapElement.MapApiKey ?? "");
	}

	/// <summary>Keeps a hand-edited or out-of-range preset value from pushing the crop/fetch math outside sane bounds.</summary>
	private static double ClampZoomOutFactor(double maxFactor)
	{
		return Math.Clamp(maxFactor, MapDynamicZoomMaxFactorMin, MapDynamicZoomMaxFactorMax);
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

		string? mapAttribution = null;

		foreach (OverlayElement element in Layout)
		{
			if (!element.Visible) continue;

			switch (element.Type)
			{
				case OverlayElementType.DateTimeText:
					DrawDateTime(canvas, frame, element);
					break;
				case OverlayElementType.UtcTimeText:
					DrawUtcTime(canvas, frame, element);
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
					DrawCompass(canvas, frame, element);
					break;
				case OverlayElementType.SunWidget:
					DrawSunWidget(canvas, frame, element.X, element.Y);
					break;
				case OverlayElementType.PitchGauge:
					DrawPitchGauge(canvas, element.X, element.Y, frame.PitchDegrees);
					break;
				case OverlayElementType.MapWidget:
					DrawMapWidget(canvas, frame, element);
					if (element.MapShowAttribution) mapAttribution = element.MapAttribution ?? MapTileFetcher.OpenStreetMapAttribution;
					break;
				case OverlayElementType.SpeedGauge:
					DrawSpeedGauge(canvas, element, frame.SpeedKmh);
					break;
			}
		}

		if (ShowWatermark)
		{
			DrawWatermark(canvas, frame.Raw.SampleTimeSeconds);
			if (mapAttribution is not null) DrawMapAttributionSlide(canvas, frame.Raw.SampleTimeSeconds, mapAttribution);
		}
		else if (mapAttribution is not null)
		{
			DrawMapAttributionOnly(canvas, mapAttribution);
		}

		return bitmap.Bytes;
	}

	/// <summary>
	///     The trail shows the whole route driven so far. Scrubbing/seeking calls Render() out of
	///     chronological order, so naively appending "the current point" every call would scramble the
	///     path once the preview jumps around - sequential progress extends the cached trail in O(1);
	///     a jump rebuilds it once from the start instead.
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
		DrawTimeText(canvas, frame, element, true);
	}

	private void DrawUtcTime(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		DrawTimeText(canvas, frame, element, false);
	}

	/// <summary>Shared by DateTimeText (local time) and UtcTimeText (raw UTC, no conversion) - both use the same DateFormat/Locale fields.</summary>
	private void DrawTimeText(SKCanvas canvas, DerivedFrame frame, OverlayElement element, bool toLocal)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		string dateText;
		if (frame.Raw.GpsTimestamp is not { } utc)
		{
			dateText = "--";
		}
		else
		{
			DateTime shown = toLocal ? utc.ToLocalFromUtc() : utc;
			try
			{
				CultureInfo culture = element.Locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(element.Locale);
				dateText = shown.ToString(element.DateFormat ?? DefaultDateFormat, culture);
			}
			catch (Exception ex) when (ex is CultureNotFoundException or FormatException)
			{
				// A hand-edited/shared preset can carry an invalid Locale or DateFormat string - fail
				// soft (fall back to the default) instead of throwing out of Render and aborting the
				// whole export, same "one bad user-editable field" policy as the map widget's
				// placeholder fallback.
				AppLogger.Warn(ex,
					$"Time widget: invalid Locale/DateFormat ('{element.Locale}' / '{element.DateFormat}') - using default.");
				dateText = shown.ToString(DefaultDateFormat, CultureInfo.CurrentCulture);
			}

			if (!toLocal) dateText += "  UTC";
		}

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

	private void DrawCompass(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		const float cx = 0;
		const float cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.CompassRadius);

		using var fill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, fill);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, ring);

		DrawTrail(canvas, cx, cy, frame, element);
		DrawTrailMarker(canvas, cx, cy, frame.HeadingDegrees, element.TrailUseArrow);

		DrawOutlined(canvas, "N", cx, cy - OverlayElementBounds.CompassRadius + 46, _labelFont, White,
			SKTextAlign.Center);

		var headingText =
			$"{F(AngleMath.NormalizeDegrees(frame.HeadingDegrees), "0")}°{CardinalDirection(frame.HeadingDegrees)}";
		DrawOutlined(canvas, headingText, cx + OverlayElementBounds.CompassRadius * 0.55f,
			cy + OverlayElementBounds.CompassRadius * 0.7f, _labelFont, White, SKTextAlign.Right);

		canvas.Restore();
	}

	private void DrawTrail(SKCanvas canvas, float cx, float cy, DerivedFrame frame, OverlayElement element)
	{
		if (_trail.Count < 2) return;

		(double East, double North) currentPos = (frame.LocalEastMeters, frame.LocalNorthMeters);
		var maxDist = _trail.Select(p => Distance((p.East, p.North), currentPos)).Prepend(5.0).Max();

		var scale = OverlayElementBounds.CompassRadius * 0.82 / maxDist;

		var builder = new SKPathBuilder();
		var started = false;
		foreach (var (east, north, _, _) in _trail)
		{
			var px = cx + (float)((east - frame.LocalEastMeters) * scale);
			var py = cy - (float)((north - frame.LocalNorthMeters) * scale);
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
			Color = ResolveTrailColor(element.TrailColor), IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = element.TrailWidth, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
		};
		canvas.DrawPath(path, trailPaint);
	}

	/// <summary>
	///     How much wider an area DrawMapWidget should crop than its static 1x baseline, so the trail
	///     stays inside the widget's circular window - mirrors DrawTrail's approach for the Compass
	///     (farthest trail point sets the required radius), but clamped to MapDynamicZoomMaxFactor since
	///     map tiles, unlike the Compass's vector trail, can't rescale losslessly forever. Smoothed over
	///     MapZoomSmoothingSeconds so it doesn't visibly "breathe" - except right after a seek/scrub,
	///     where it snaps straight to the target instead of smoothing from a stale value.
	/// </summary>
	private double GetMapZoomFactor(DerivedFrame frame, SKPoint center, List<SKPoint> trailPixels, double maxFactor)
	{
		const float radius = OverlayElementBounds.MapRadius;

		double maxDistSq = 0;
		foreach (SKPoint pixel in trailPixels)
		{
			double dx = pixel.X - center.X, dy = pixel.Y - center.Y;
			maxDistSq = Math.Max(maxDistSq, dx * dx + dy * dy);
		}

		// The farthest trail point should land at MapTrailFitFraction of the crop radius, not right at
		// its edge, so it stays comfortably inside the circle instead of grazing the rim.
		var requiredRadius = Math.Sqrt(maxDistSq) / MapTrailFitFraction;
		var target = Math.Clamp(requiredRadius / radius, 1.0, ClampZoomOutFactor(maxFactor));

		var seconds = frame.Raw.SampleTimeSeconds;
		if (_lastMapZoomTimeSeconds is not { } lastSeconds || seconds <= lastSeconds || seconds - lastSeconds > 2.0)
		{
			_mapZoomFactor = target;
		}
		else
		{
			var alpha = 1.0 - Math.Exp(-(seconds - lastSeconds) / MapZoomSmoothingSeconds);
			_mapZoomFactor += (target - _mapZoomFactor) * alpha;
		}

		_lastMapZoomTimeSeconds = seconds;
		return _mapZoomFactor;
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
			// Computed once and shared with GetMapZoomFactor (when dynamic zoom needs it) and the trail
			// draw below, instead of both re-running the same Web Mercator projection per trail point.
			List<SKPoint> trailPixels = GetTrailPixels();

			var zoomFactor = element.MapDynamicZoom
				? GetMapZoomFactor(frame, center, trailPixels, element.MapDynamicZoomMaxFactor)
				: 1.0;
			var cropRadius = radius * (float)zoomFactor;

			var src = SKRect.Create(center.X - cropRadius, center.Y - cropRadius, cropRadius * 2, cropRadius * 2);
			var dest = SKRect.Create(-radius, -radius, radius * 2, radius * 2);
			canvas.DrawBitmap(_mapMosaic.Bitmap, src, dest, SKSamplingOptions.Default);

			DrawMapTrail(canvas, center, radius / cropRadius, trailPixels, ResolveTrailColor(element.TrailColor),
				element.TrailWidth);

			canvas.Restore();

			// Same marker choice/paint as the Compass (DrawTrailMarker) by default - both widgets are
			// north-up, so "pointing in the direction of travel" means the same thing in both - but each
			// widget's TrailUseArrow is its own independent setting, so they can be styled differently.
			DrawTrailMarker(canvas, 0, 0, frame.HeadingDegrees, element.TrailUseArrow);

			// Required OSM attribution is no longer crammed inside this small circle (it read poorly
			// over busy map tiles) - Render() draws it bottom-center instead, see DrawWatermark/
			// DrawMapAttributionOnly.
		}

		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(0, 0, radius, ring);

		canvas.Restore();
	}

	/// <summary>
	///     Projects the whole accumulated trail into the current mosaic's Web Mercator pixel space
	///     once per frame, so GetMapZoomFactor and DrawMapTrail can share the result instead of each
	///     re-running the same (mildly expensive - Math.Log/Math.Tan per point) projection separately
	///     over what can be a many-thousand-point trail by the end of a long recording.
	/// </summary>
	private List<SKPoint> GetTrailPixels()
	{
		var pixels = new List<SKPoint>(_trail.Count);
		pixels.AddRange(_trail.Select(p => _mapMosaic!.GetPixel(p.Lat, p.Lon)));
		return pixels;
	}

	/// <summary>
	///     Same route trail as the Compass, redrawn in the map's Web Mercator pixel space so it lines
	///     up with the tiles. mapScale converts mosaic pixels to the on-screen widget space - 1.0 when
	///     the crop window is drawn 1:1 (dynamic zoom off or at its baseline), smaller when
	///     DrawMapWidget crops a wider area than the widget's fixed on-screen radius to zoom out.
	/// </summary>
	private static void DrawMapTrail(SKCanvas canvas, SKPoint center, float mapScale, List<SKPoint> trailPixels,
		SKColor color, float width)
	{
		if (trailPixels.Count < 2) return;

		var builder = new SKPathBuilder();
		var started = false;
		foreach (SKPoint pixel in trailPixels)
		{
			var px = (pixel.X - center.X) * mapScale;
			var py = (pixel.Y - center.Y) * mapScale;
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
			Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
		};
		canvas.DrawPath(path, trailPaint);
	}

	/// <summary>Hex string (e.g. "#46DC6E") from OverlayElement.TrailColor, or the built-in green when null/unparsable - fails soft, same policy as the time widgets' Locale/DateFormat.</summary>
	private static SKColor ResolveTrailColor(string? hex)
	{
		return !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out SKColor parsed) ? parsed : TrailColor;
	}

	/// <summary>Heading arrow (matches the driving direction, north-up) when useArrow, the older static dot otherwise - shared by Compass and MapWidget so the two draw identically for whichever style each picks.</summary>
	private void DrawTrailMarker(SKCanvas canvas, float cx, float cy, double headingDegrees, bool useArrow)
	{
		if (useArrow)
			DrawHeadingArrow(canvas, cx, cy, headingDegrees);
		else
			DrawTrailDot(canvas, cx, cy);
	}

	private static void DrawTrailDot(SKCanvas canvas, float cx, float cy)
	{
		using var dotOutline = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var dotFill = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawCircle(cx, cy, 9, dotOutline);
		canvas.DrawCircle(cx, cy, 6, dotFill);
	}

	private static void DrawHeadingArrow(SKCanvas canvas, float cx, float cy, double headingDegrees)
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

	private static void DrawOutlined(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color,
		SKTextAlign align = SKTextAlign.Left, float opacity = 1f)
	{
		var dropOffset = font.Size * 0.045f;
		using var dropShadowPaint = new SKPaint();
		dropShadowPaint.Color = new SKColor(0, 0, 0, (byte)(130 * opacity));
		dropShadowPaint.IsAntialias = true;
		dropShadowPaint.Style = SKPaintStyle.Fill;
		dropShadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.04f);
		canvas.DrawText(text, x + dropOffset, y + dropOffset, align, font, dropShadowPaint);

		using var strokePaint = new SKPaint();
		strokePaint.Color = Shadow.WithAlpha((byte)(Shadow.Alpha * opacity));
		strokePaint.IsAntialias = true;
		strokePaint.Style = SKPaintStyle.Stroke;
		strokePaint.StrokeWidth = font.Size * 0.045f;
		using var fillPaint = new SKPaint();
		fillPaint.Color = color.WithAlpha((byte)(color.Alpha * opacity));
		fillPaint.IsAntialias = true;
		fillPaint.Style = SKPaintStyle.Fill;
		canvas.DrawText(text, x, y, align, font, strokePaint);
		canvas.DrawText(text, x, y, align, font, fillPaint);
	}

	/// <summary>
	///     Bottom-center attribution watermark, full opacity from frame 0 then fading out (see
	///     WatermarkAlpha). When the Map widget is also visible, DrawMapAttributionSlide follows as a
	///     separate second slide - see DrawMapAttributionOnly for when this watermark is off but the
	///     map's mandatory credit isn't.
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

	/// <summary>The map's required OSM credit, shown as its own short slide right after the watermark's (see MapAttributionSlideAlpha).</summary>
	private void DrawMapAttributionSlide(SKCanvas canvas, double sampleTimeSeconds, string mapAttribution)
	{
		var alpha = MapAttributionSlideAlpha(sampleTimeSeconds);
		if (alpha <= 0f) return;

		canvas.Save();
		canvas.Translate(_width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, mapAttribution, 0, 0, _smallFont, White, SKTextAlign.Center, alpha);

		canvas.Restore();
	}

	/// <summary>
	///     OSM's tile usage policy requires visible attribution whenever its tiles are shown, so unlike
	///     the optional "Made with OsmoOverlay" watermark, this can't fade out or be turned off - drawn
	///     at full opacity for the whole video when the watermark itself is disabled.
	/// </summary>
	private void DrawMapAttributionOnly(SKCanvas canvas, string mapAttribution)
	{
		canvas.Save();
		canvas.Translate(_width / 2f, _height - WatermarkBottomMargin * _scale);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, mapAttribution, 0, 0, _smallFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	/// <summary>No fade-in - it's on screen from the very first frame, so an instant appearance reads as the video simply starting, not as something popping in.</summary>
	private static float WatermarkAlpha(double sampleTimeSeconds)
	{
		return FadeAlpha(sampleTimeSeconds, 0, 0, WatermarkFadeOutStartSeconds, WatermarkDurationSeconds);
	}

	/// <summary>
	///     Unlike the watermark, this slide appears mid-video (right after the watermark's own slide
	///     ends) rather than at frame 0, so it gets a short fade-in too - popping in abruptly here would
	///     read as a glitch rather than an intentional second slide.
	/// </summary>
	private static float MapAttributionSlideAlpha(double sampleTimeSeconds)
	{
		var start = WatermarkDurationSeconds;
		var fadeInEnd = start + 1.0;
		var end = start + MapAttributionSlideDurationSeconds;
		var fadeOutStart = end - 1.0;
		return FadeAlpha(sampleTimeSeconds, start, fadeInEnd, fadeOutStart, end);
	}

	/// <summary>
	///     Linear fade in from `fadeInStart` to `fadeInEnd`, full opacity until `fadeOutStart`, then a
	///     linear fade down to 0 by `fadeOutEnd`. Pass `fadeInStart == fadeInEnd` for an instant
	///     appearance with no fade-in at all (used by the watermark, which starts at frame 0).
	/// </summary>
	private static float FadeAlpha(double sampleTimeSeconds, double fadeInStart, double fadeInEnd, double fadeOutStart,
		double fadeOutEnd)
	{
		if (sampleTimeSeconds < fadeInStart || sampleTimeSeconds >= fadeOutEnd) return 0f;
		if (sampleTimeSeconds < fadeInEnd) return (float)((sampleTimeSeconds - fadeInStart) / (fadeInEnd - fadeInStart));
		if (sampleTimeSeconds < fadeOutStart) return 1f;

		return (float)(1.0 - (sampleTimeSeconds - fadeOutStart) / (fadeOutEnd - fadeOutStart));
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
