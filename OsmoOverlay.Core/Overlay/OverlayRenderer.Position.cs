using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;
using MapMosaicKey = (string Url, int Zoom, double MaxZoomOutFactor);

namespace OsmoOverlay.Core.Overlay;

/// <summary>Compass and MapWidget - the two position/heading-driven widgets, and the route trail they both share.</summary>
public sealed partial class OverlayRenderer
{
	private const double TrailMinStepMeters = 3.0;

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
	private readonly ResettableEma _mapZoomEma = new();
	private RouteMapMosaic? _mapMosaic;
	private MapMosaicKey? _preparedMapKey;

	private readonly List<(double East, double North, double Lat, double Lon)> _trail = [];
	private (double East, double North)? _lastTrailPoint;
	private int _trailCacheIndex = -1;

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
		var urlTemplate = ResolveUrlTemplate();
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
		MapMosaicKey key = (ResolveUrlTemplate(), mapElement.MapZoom, maxFactor);
		return _preparedMapKey != key;
	}

	/// <summary>
	///     The tile URL template actually used for fetching: the global MapTileUrlTemplate (or the
	///     default source) with a literal "{api_key}" placeholder filled from the global MapApiKey - a
	///     no-op for templates without that placeholder. Baking the key into the URL means an API key
	///     edit alone already changes this string, so NeedsMapPrepare/BuildMapMosaicAsync's key
	///     comparisons catch it for free. Shared by DrawMapWidget's mosaic and the route-intro overview
	///     mosaic - both draw from the same configured tile source.
	/// </summary>
	private string ResolveUrlTemplate()
	{
		var template = MapTileUrlTemplate ?? MapTileFetcher.OpenStreetMapUrlTemplate;
		return template.Replace("{api_key}", MapApiKey ?? "");
	}

	/// <summary>Keeps a hand-edited or out-of-range preset value from pushing the crop/fetch math outside sane bounds.</summary>
	private static double ClampZoomOutFactor(double maxFactor)
	{
		return Math.Clamp(maxFactor, MapDynamicZoomMaxFactorMin, MapDynamicZoomMaxFactorMax);
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

	private void DrawCompass(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		const float cx = 0;
		const float cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.CompassRadius);

		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, _panelFillPaint);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, _ringStroke3White160);

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
		var maxDist = 5.0;
		foreach (var p in _trail)
		{
			var dist = Distance((p.East, p.North), currentPos);
			if (dist > maxDist) maxDist = dist;
		}

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

		return _mapZoomEma.Update(frame.Raw.SampleTimeSeconds, target, MapZoomSmoothingSeconds);
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
			canvas.DrawCircle(0, 0, radius, _panelFillPaint);
			DrawOutlined(canvas, "MAP", 0, -10, _labelFont, White, SKTextAlign.Center);
			DrawOutlined(canvas, "UNAVAILABLE", 0, 24, _smallFont, White, SKTextAlign.Center);
		}
		else
		{
			canvas.Save();
			var clipBuilder = new SKPathBuilder();
			clipBuilder.AddCircle(0, 0, radius);
			using (SKPath clipPath = clipBuilder.Detach()) canvas.ClipPath(clipPath, antialias: true);

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

		canvas.DrawCircle(0, 0, radius, _ringStroke3White160);

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
		foreach (var p in _trail)
			pixels.Add(_mapMosaic!.GetPixel(p.Lat, p.Lon));
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

	private void DrawTrailDot(SKCanvas canvas, float cx, float cy)
	{
		canvas.DrawCircle(cx, cy, 9, _dotOutlineBlackFill);
		canvas.DrawCircle(cx, cy, 6, _dotFillAccent);
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
		canvas.DrawPath(path, _dotFillAccent);
		canvas.DrawPath(path, _blackStroke3);
	}

	private static string CardinalDirection(double heading)
	{
		string[] names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
		var index = (int)Math.Round(AngleMath.NormalizeDegrees(heading) / 45.0) % 8;
		return names[index];
	}
}
