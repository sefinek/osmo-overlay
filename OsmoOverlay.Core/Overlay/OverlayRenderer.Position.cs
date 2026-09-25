using System.Runtime.InteropServices;
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

	private static readonly string[] CardinalNames = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

	private readonly List<TrailPoint> _trail = [];
	// The trail's bounding box in local meters, grown with every point AppendTrailPoint keeps.
	private double _trailMinEast, _trailMaxEast, _trailMinNorth, _trailMaxNorth;
	// GetTrailPixels' projection of _trail into _trailPixelsMosaic's pixel space.
	private readonly List<SKPoint> _trailPixels = [];
	private RouteMapMosaic? _trailPixelsMosaic;
	private int _trailCacheIndex = -1;

	// The trail's drawn routes, extended as the trail grows (SyncRoute) - the compass's in local meters, the map's in
	// its mosaic's pixels. Indexed [colorBySpeed ? 1 : 0]: every Compass/MapWidget instance picks that on its own.
	private readonly RouteGeometry?[] _compassRoutes = new RouteGeometry?[2];
	private readonly RouteGeometry?[] _mapRoutes = new RouteGeometry?[2];

	/// <summary>AfterCut: the first point after a part cut out of the render - the route joins it per RouteAcrossCuts.</summary>
	private readonly record struct TrailPoint(double East, double North, double Lat, double Lon, double SpeedKmh, bool AfterCut);

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
		if (Layout.OfType<MapWidgetElement>().FirstOrDefault(e => e.Visible) is not { } mapElement)
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
			// Task.Run: called from the GUI thread by PreviewPlayer, and without it every tile decode/draw
			// continuation would resume there. The key above is still set synchronously on purpose -
			// NeedsMapPrepare reads it right after this returns its Task. onTileProgress therefore fires
			// on a thread-pool thread.
			return (await Task.Run(() => RouteMapMosaic.BuildAsync(points, urlTemplate, mapElement.MapZoom, paddingTiles, ct, onTileProgress), ct), key);
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
		if (layout.OfType<MapWidgetElement>().FirstOrDefault(e => e.Visible) is not { } mapElement)
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
	///     The trail shows the whole route driven so far. Scrubbing/seeking calls RenderInto() out of
	///     chronological order, so naively appending "the current point" every call would scramble the
	///     path once the preview jumps around - sequential progress extends the cached trail in O(1);
	///     a jump rebuilds it once from the start instead.
	/// </summary>
	private void UpdateTrail(DerivedFrame frame)
	{
		if (_allFrames.Count == 0) return;

		var index = TelemetryProcessor.FindIndex(_allFrames, frame.Raw.SampleTimeSeconds);
		if (index == _trailCacheIndex + 1)
		{
			AppendTrailPoint(_allFrames[index]);
		}
		else if (index != _trailCacheIndex)
		{
			_trail.Clear();
			for (var i = 0; i <= index; i++)
				AppendTrailPoint(_allFrames[i]);
		}

		_trailCacheIndex = index;
	}

	private void AppendTrailPoint(DerivedFrame frame)
	{
		double east = frame.LocalEastMeters, north = frame.LocalNorthMeters;
		// The first point after a cut is always kept, however close - it's where the route resumes.
		if (!frame.StartsAfterCut && _trail.Count > 0)
		{
			TrailPoint last = _trail[^1];
			double dx = east - last.East, dy = north - last.North;
			if (dx * dx + dy * dy < TrailMinStepMeters * TrailMinStepMeters) return;
		}

		if (_trail.Count == 0)
		{
			(_trailMinEast, _trailMaxEast, _trailMinNorth, _trailMaxNorth) = (east, east, north, north);
		}
		else
		{
			_trailMinEast = Math.Min(_trailMinEast, east);
			_trailMaxEast = Math.Max(_trailMaxEast, east);
			_trailMinNorth = Math.Min(_trailMinNorth, north);
			_trailMaxNorth = Math.Max(_trailMaxNorth, north);
		}

		_trail.Add(new TrailPoint(east, north, frame.Raw.Latitude, frame.Raw.Longitude, frame.SpeedKmh, frame.StartsAfterCut));
	}

	private void DrawCompass(SKCanvas canvas, DerivedFrame frame, CompassElement element)
	{
		const float radius = OverlayElementBounds.CompassRadius;

		DrawPanelShadow(canvas, 0, 0, radius);

		canvas.DrawCircle(0, 0, radius, _panelFillPaint);
		canvas.DrawCircle(0, 0, radius, _ringStroke3White160);

		SKPoint marker = DrawTrail(canvas, frame, element);
		DrawTrailMarker(canvas, marker.X, marker.Y, frame.HeadingDegrees, element.TrailUseArrow);

		DrawOutlined(canvas, "N", 0, -radius + 46, _labelFont, White, SKTextAlign.Center);

		var headingText = $"{F(AngleMath.NormalizeDegrees(frame.HeadingDegrees), "0")}°{CardinalDirection(frame.HeadingDegrees)}";
		DrawOutlined(canvas, headingText, radius * 0.55f, radius * 0.7f, _labelFont, White, SKTextAlign.Right);
	}

	/// <summary>
	///     Fits the whole trail plus the current position into the dial: centered on their bounding box, not on the
	///     current position, so a route that went off to one side fills the dial instead of half of it. The marker
	///     therefore moves around the dial; the fit radius leaves room for the heading arrow inside the rim.
	///     Returns where the marker goes.
	/// </summary>
	private SKPoint DrawTrail(SKCanvas canvas, DerivedFrame frame, TrailOverlayElement element)
	{
		if (_trail.Count < 2) return SKPoint.Empty;

		double east = frame.LocalEastMeters, north = frame.LocalNorthMeters;
		var centerEast = (Math.Min(_trailMinEast, east) + Math.Max(_trailMaxEast, east)) / 2;
		var centerNorth = (Math.Min(_trailMinNorth, north) + Math.Max(_trailMaxNorth, north)) / 2;

		var maxDistSq = Math.Max(5.0 * 5.0, DistanceSq(east, north, centerEast, centerNorth));
		foreach (TrailPoint p in CollectionsMarshal.AsSpan(_trail))
			maxDistSq = Math.Max(maxDistSq, DistanceSq(p.East, p.North, centerEast, centerNorth));

		var scale = (OverlayElementBounds.CompassRadius - 50) / Math.Sqrt(maxDistSq);
		// North up: local meters (y growing north) onto the dial (y growing down).
		var toDial = SKMatrix.CreateScaleTranslation((float)scale, (float)-scale, (float)(-centerEast * scale),
			(float)(centerNorth * scale));
		DrawTrailRoute(canvas, _compassRoutes, null, toDial, element);
		return new SKPoint((float)((east - centerEast) * scale), (float)(-(north - centerNorth) * scale));
	}

	private static double DistanceSq(double x1, double y1, double x2, double y2)
	{
		double dx = x1 - x2, dy = y1 - y2;
		return dx * dx + dy * dy;
	}

	/// <param name="pixels">The trail in the map mosaic's pixels (GetTrailPixels), or null for the compass's local meters.</param>
	private void DrawTrailRoute(SKCanvas canvas, RouteGeometry?[] routes, List<SKPoint>? pixels, SKMatrix toCanvas,
		TrailOverlayElement element)
	{
		var devicePixelsPerUnit = RouteGeometry.UniformScale(canvas.TotalMatrix) * RouteGeometry.UniformScale(toCanvas);
		RouteGeometry route = SyncRoute(routes, pixels, element.TrailColorBySpeed, 0.5 / devicePixelsPerUnit);
		DrawRoute(canvas, route, toCanvas, ResolveTrailColor(element.TrailColor), element.TrailWidth);
	}

	/// <summary>
	///     Extends the cached route by the trail points added since it was last drawn. The trail is always the same
	///     deterministic function of the frames up to the current one (see UpdateTrail), so a longer trail only
	///     ever extends a shorter one - only a shorter trail (a seek backwards), another RouteAcrossCuts or another
	///     level of detail starts it over. A new map mosaic drops the map's routes in GetTrailPixels.
	///     `wantedStep` (half a device pixel, in the route's units) becomes a power of two the route keeps while
	///     the wanted step stays within [step, 4 * step) - the dial's and the dynamic map zoom's scale drift a little
	///     every frame, and rebuilding the route each time it crossed a boundary would cost more than it saves.
	/// </summary>
	private RouteGeometry SyncRoute(RouteGeometry?[] routes, List<SKPoint>? pixels, bool colorBySpeed, double wantedStep)
	{
		ref RouteGeometry? route = ref routes[colorBySpeed ? 1 : 0];
		var step = route?.MinStep ?? 0;
		if (!(wantedStep >= step && wantedStep < step * 4) && double.IsFinite(wantedStep) && wantedStep > 0)
			step = (float)Math.Pow(2, Math.Floor(Math.Log2(wantedStep)));

		if (route is not null && (route.Join != RouteAcrossCuts || route.Count > _trail.Count || route.MinStep != step))
		{
			route.Dispose();
			route = null;
		}

		route ??= new RouteGeometry(RouteAcrossCuts, colorBySpeed, _trailSpeedScaleKmh, step);
		for (var i = route.Count; i < _trail.Count; i++)
		{
			TrailPoint p = _trail[i];
			route.Add(pixels?[i] ?? new SKPoint((float)p.East, (float)p.North), p.SpeedKmh, p.AfterCut);
		}

		return route;
	}

	private static void DisposeRoutes(RouteGeometry?[] routes)
	{
		for (var i = 0; i < routes.Length; i++)
		{
			routes[i]?.Dispose();
			routes[i] = null;
		}
	}

	/// <summary>
	///     Where a route colored by speed turns full red: the recording's 98th-percentile speed rather than its
	///     max, so one GPS speed spike can't push the whole rest of the route into green. The 5 km/h floor keeps
	///     a clip spent standing around from lighting up red at walking pace.
	/// </summary>
	private static double ComputeTrailSpeedScale(IReadOnlyList<DerivedFrame> frames)
	{
		if (frames.Count == 0) return 5;
		double[] speeds = [.. frames.Select(f => f.SpeedKmh).Where(double.IsFinite)];
		if (speeds.Length == 0) return 5;
		Array.Sort(speeds);
		return Math.Max(speeds[(int)((speeds.Length - 1) * 0.98)], 5);
	}

	/// <summary>
	///     The one place every drawn route (compass trail, map widget, route intro) goes through - see RouteGeometry
	///     for how it's built and how a cut is crossed. `width` is in canvas pixels whatever `toCanvas` scales by.
	/// </summary>
	private void DrawRoute(SKCanvas canvas, RouteGeometry route, SKMatrix toCanvas, SKColor color, float width)
	{
		_routeDashPaint.PathEffect = GetDashEffect(width);
		route.Draw(canvas, toCanvas, color, width, _routeStrokePaint, _routeDashPaint);
	}

	/// <summary>The dashes of a join across a cut at a route width - cached like the blur filters, a layout uses only a few widths.</summary>
	private SKPathEffect GetDashEffect(float width)
	{
		if (_dashEffects.TryGetValue(width, out SKPathEffect? effect)) return effect;

		effect = SKPathEffect.CreateDash([Math.Max(6, width * 3), Math.Max(5, width * 2.5f)], 0);
		_dashEffects[width] = effect;
		return effect;
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
	private void DrawMapWidget(SKCanvas canvas, DerivedFrame frame, MapWidgetElement element)
	{
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
			canvas.ClipRoundRect(new SKRoundRect(SKRect.Create(-radius, -radius, radius * 2, radius * 2), radius), antialias: true);

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
			canvas.DrawImage(_mapMosaic.Image, src, dest, SKSamplingOptions.Default);

			var mapScale = radius / cropRadius;
			var toWidget = SKMatrix.CreateScaleTranslation(mapScale, mapScale, -center.X * mapScale, -center.Y * mapScale);
			DrawTrailRoute(canvas, _mapRoutes, trailPixels, toWidget, element);

			canvas.Restore();

			// Same marker choice/paint as the Compass (DrawTrailMarker) by default - both widgets are
			// north-up, so "pointing in the direction of travel" means the same thing in both - but each
			// widget's TrailUseArrow is its own independent setting, so they can be styled differently.
			DrawTrailMarker(canvas, 0, 0, frame.HeadingDegrees, element.TrailUseArrow);

			// The required OSM attribution isn't drawn inside this small circle (unreadable over busy map
			// tiles) - DrawFrame draws it bottom-center instead, see DrawWatermark/DrawMapAttributionOnly.
		}

		canvas.DrawCircle(0, 0, radius, _ringStroke3White160);
	}

	/// <summary>
	///     Projects the whole accumulated trail into the current mosaic's Web Mercator pixel space
	///     once per frame, so GetMapZoomFactor and the trail drawing can share the result instead of each
	///     re-running the same (mildly expensive - Math.Log/Math.Tan per point) projection separately
	///     over what can be a many-thousand-point trail by the end of a long recording.
	/// </summary>
	private List<SKPoint> GetTrailPixels()
	{
		// Incremental: _trail is always (re)built from frame 0 by the same deterministic steps (see
		// UpdateTrail), so a longer trail only ever extends a shorter one - already projected points stay
		// valid, and only a trail that got shorter (a seek backwards) or a different mosaic invalidates them.
		if (!ReferenceEquals(_trailPixelsMosaic, _mapMosaic))
		{
			_trailPixels.Clear();
			_trailPixelsMosaic = _mapMosaic;
			// Another mosaic places the same points at other pixels.
			DisposeRoutes(_mapRoutes);
		}
		else if (_trailPixels.Count > _trail.Count)
		{
			_trailPixels.RemoveRange(_trail.Count, _trailPixels.Count - _trail.Count);
		}

		for (var i = _trailPixels.Count; i < _trail.Count; i++)
			_trailPixels.Add(_mapMosaic!.GetPixel(_trail[i].Lat, _trail[i].Lon));
		return _trailPixels;
	}

	/// <summary>Hex string (e.g. "#46DC6E") from OverlayElement.TrailColor, or the built-in green when null/unparsable - fails soft, same policy as the time widgets' Locale/DateFormat.</summary>
	private static SKColor ResolveTrailColor(string? hex)
	{
		return ResolveColor(hex, TrailColor);
	}

	/// <summary>Heading arrow (matches the driving direction, north-up) when useArrow, a static dot otherwise - shared by Compass and MapWidget so the two draw identically for whichever style each picks.</summary>
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
		canvas.Save();
		canvas.Translate(cx, cy);
		// Clockwise from north, the way a heading turns on screen (y grows down).
		canvas.RotateDegrees((float)headingDegrees);
		canvas.DrawPath(_headingArrow, _dotFillAccent);
		canvas.DrawPath(_headingArrow, _blackStroke3);
		canvas.Restore();
	}

	/// <summary>The heading arrow pointing north around (0, 0), built once - DrawHeadingArrow rotates it.</summary>
	private static SKPath CreateHeadingArrow()
	{
		const double length = 42;
		const double wingAngle = 2.5;
		const double wingLength = length * 0.55;

		using var builder = new SKPathBuilder();
		builder.MoveTo(0, (float)-length);
		builder.LineTo((float)(Math.Sin(wingAngle) * wingLength), (float)(-Math.Cos(wingAngle) * wingLength));
		builder.LineTo(0, 0);
		builder.LineTo((float)(Math.Sin(-wingAngle) * wingLength), (float)(-Math.Cos(-wingAngle) * wingLength));
		builder.Close();
		return builder.Detach();
	}

	private static string CardinalDirection(double heading)
	{
		var index = (int)Math.Round(AngleMath.NormalizeDegrees(heading) / 45.0) % 8;
		return CardinalNames[index];
	}
}
