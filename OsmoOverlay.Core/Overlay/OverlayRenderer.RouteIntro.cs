using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The optional fullscreen "whole route" card shown for RouteIntro.DurationSeconds at the start
///     of the render - a static overview (the whole route mosaic drawn fit-to-rect, not panned like
///     DrawMapWidget) plus whichever trip stats RouteIntro.Stats turns on. Independent of MapWidget:
///     fetches its own mosaic instead of reusing MapWidget's close-up panning one, since this one is
///     drawn much larger (up to the whole card) and covers the whole route's bounding box rather than
///     a per-frame crop around the current position.
/// </summary>
public sealed partial class OverlayRenderer
{
	// Requests the highest zoom RouteMapMosaic supports; BuildAsync's own MaxTiles budget backs this
	// off automatically until the mosaic fits, so a short route still gets crisp close-in tiles while
	// a long one degrades gracefully instead of hardcoding one zoom that's too coarse for a short
	// route (visibly blurry once stretched to fill the card) or too many tiles for a long one.
	private const int RouteIntroMapZoomDefault = RouteMapMosaic.MaxZoom;

	// How long the crossfade into the normal HUD widgets takes, counted backwards from
	// RouteIntro.DurationSeconds (not appended after it) - so the setting still means "the card is
	// fully gone by this time", just with a soft cut instead of an instant one. Short enough that it
	// reads as a transition, not its own separate beat.
	private const double RouteIntroTransitionSeconds = 0.6;

	/// <summary>
	///     Where the crossfade into the normal HUD begins, on the video's own timeline - shared by
	///     DrawFrame (which draws the crossfade itself) and DrawWatermark/DrawMapAttributionSlide (whose
	///     own fade-out otherwise runs on fixed timing unrelated to RouteIntro.DurationSeconds - see
	///     WatermarkFadeOutEndSeconds), so both fades land on the same moment regardless of how long the
	///     card is configured to show for.
	/// </summary>
	private double RouteIntroTransitionStartSeconds => Math.Max(0, RouteIntro.DurationSeconds - RouteIntroTransitionSeconds);

	// One fixed offset for every two-column stat row (not a per-row width fit to that row's own
	// content) so the right column lines up the same way from row to row - sized for DATE's value
	// ("14.09.2026  09:08:11"), the longest thing that ever lands in a left slot, even though most
	// other rows leave more empty gap between columns than they strictly need to.
	private const float RouteIntroStatColumnOffset = 680f;

	private RouteMapMosaic? _routeIntroMosaic;
	private RouteIntroMosaicKey? _preparedRouteIntroKey;

	/// <summary>What the route overview's mosaic is fetched for: its tile server and the route's extent.</summary>
	public readonly record struct RouteIntroMosaicKey(string UrlTemplate, GeoBounds Route, string? LabelsUrlTemplate = null);

	private RouteIntroMosaicKey CurrentRouteIntroKey()
	{
		return new RouteIntroMosaicKey(MapSources.UrlTemplate(RouteIntro.MapProviderId),
			GeoBounds.Of(_allFrames.Select(f => (f.Raw.Latitude, f.Raw.Longitude))), MapSources.LabelsUrlTemplate(RouteIntro.MapProviderId));
	}

	/// <summary>
	///     The route overview's counterpart of NeedsMapPrepare - its provider, a key or the route's extent changed since the
	///     last fetch (one still running counts: a cut that leaves the route's extent as it was fetches nothing again).
	/// </summary>
	public bool NeedsRouteIntroPrepare()
	{
		return RouteIntro.Enabled && _preparedRouteIntroKey != CurrentRouteIntroKey();
	}
	private List<SKPoint>? _routeIntroTrailPixels;

	// The whole card (backdrop, map mosaic, route, stats) only depends on the recording as a whole, never
	// on the current frame - drawing it from scratch every frame (a full-frame translucent fill plus
	// resampling the entire mosaic) was by far the slowest part of a render. Rendered once per key and
	// then just blitted; any change to size/settings/mosaic changes the key and rebuilds it.
	private SKImage? _routeIntroCard;
	private RouteIntroCardKey? _routeIntroCardKey;

	private readonly record struct RouteIntroCardKey(int Width, int Height, RouteIntroSettings Settings, RouteMapMosaic? Mosaic, RouteJoin RouteAcrossCuts);

	/// <summary>See PrepareMapAsync (OverlayRenderer.Position.cs) - same shape, independent mosaic/key.</summary>
	public async Task PrepareRouteIntroMapAsync(Action<int, int>? onTileProgress = null, CancellationToken ct = default)
	{
		(RouteMapMosaic? mosaic, RouteIntroMosaicKey? key) = await BuildRouteIntroMosaicAsync(onTileProgress, ct);
		ApplyRouteIntroMapMosaic(mosaic, key);
	}

	/// <summary>See BuildMapMosaicAsync - same fetch-only/apply split so a live-preview caller can run the network I/O without holding the render lock.</summary>
	public async Task<(RouteMapMosaic? Mosaic, RouteIntroMosaicKey? Key)> BuildRouteIntroMosaicAsync(
		Action<int, int>? onTileProgress = null, CancellationToken ct = default)
	{
		if (!RouteIntro.Enabled) return (null, null);

		List<(double Lat, double Lon)> points = [.. _allFrames.Select(f => (f.Raw.Latitude, f.Raw.Longitude))];
		RouteIntroMosaicKey key = CurrentRouteIntroKey();
		string urlTemplate = key.UrlTemplate;
		_preparedRouteIntroKey = key;

		if (MapSources.MissesApiKey(RouteIntro.MapProviderId))
		{
			AppLogger.Warn(string.Format(CoreStrings.Map_ApiKeyMissing, MapSources.NameOf(RouteIntro.MapProviderId)));
			return (null, key);
		}

		SKRect mapRect = GetRouteIntroMapRect();
		double targetAspect = mapRect.Width / mapRect.Height;

		// paddingTiles=0 (not MapWidget's per-zoom-factor padding) - this mosaic is only ever drawn
		// fit-to-rect as a whole, never panned/cropped, so it needs no extra margin beyond the route's
		// own bounding box. Confirmed the hard way: a padding row can land on a tile the provider has
		// no real imagery for at this zoom (a solid placeholder color, not a fetch failure - no warning
		// logged, RouteMapMosaic just draws it like any other successfully fetched tile), which read as
		// a dark band across the whole overview once drawn fit-to-rect. The route's own bounding box
		// tiles don't have this problem since they're guaranteed to have real imagery under the route.
		// targetAspect instead makes BuildAsync itself pad out whichever axis is short of mapRect's
		// shape, so the draw side can fit the mosaic without either letterboxing or cropping into the
		// route (see DrawRouteIntroMap).
		try
		{
			// Task.Run for the same reason as BuildMapMosaicAsync.
			return (await Task.Run(() => RouteMapMosaic.BuildAsync(points, urlTemplate, RouteIntroMapZoomDefault, 0, ct,
				onTileProgress, targetAspect, MapSources.CacheMaxAge(RouteIntro.MapProviderId), key.LabelsUrlTemplate), ct), key);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "Route intro: failed to prepare the route overview map - the card will show without a map");
			return (null, key);
		}
	}

	/// <summary>See ApplyMapMosaic - same superseded-fetch guard, plus projecting the whole route's trail once up front instead of per draw call.</summary>
	public void ApplyRouteIntroMapMosaic(RouteMapMosaic? mosaic, RouteIntroMosaicKey? forKey)
	{
		if (forKey is not null && forKey != _preparedRouteIntroKey)
		{
			mosaic?.Dispose();
			return;
		}

		_routeIntroMosaic?.Dispose();
		_routeIntroMosaic = mosaic;
		_routeIntroTrailPixels = mosaic is null ? null : ProjectRoute(mosaic);
	}

	private List<SKPoint> ProjectRoute(RouteMapMosaic mosaic)
	{
		return [.. _allFrames.Select(f => mosaic.GetPixel(f.Raw.Latitude, f.Raw.Longitude))];
	}

	/// <summary>
	///     Single source of truth for the map card's rect, read both when drawing it and (via
	///     BuildRouteIntroMosaicAsync) when deciding what aspect ratio to fetch the mosaic at - the two
	///     must agree, or the draw side is back to choosing between letterboxing and cropping the route.
	/// </summary>
	private SKRect GetRouteIntroMapRect()
	{
		float refWidth = _width / _scale;
		float refHeight = _height / _scale;
		const float margin = OverlayElementBounds.Margin;
		return new SKRect(margin, margin, refWidth * 0.55f, refHeight - margin);
	}

	/// <summary>Draws the cached card (see _routeIntroCard) at `alpha`, building it first if the key changed. `canvas` must be the frame canvas with only DrawFrame's output-size scale applied.</summary>
	private void DrawRouteIntroCard(SKCanvas canvas, int outW, int outH, float alpha)
	{
		var key = new RouteIntroCardKey(outW, outH, RouteIntro, _routeIntroMosaic, RouteAcrossCuts);
		if (_routeIntroCard is null || _routeIntroCardKey != key)
		{
			_routeIntroCard?.Dispose();
			using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
			surface.Canvas.Clear(SKColors.Transparent);
			surface.Canvas.Scale(outW / (float)_width, outH / (float)_height);
			DrawRouteIntro(surface.Canvas);
			_routeIntroCard = surface.Snapshot();
			_routeIntroCardKey = key;
		}

		canvas.Save();
		canvas.ResetMatrix();
		if (alpha >= 1f)
			canvas.DrawImage(_routeIntroCard, 0, 0, SKSamplingOptions.Default);
		else
			canvas.DrawImage(_routeIntroCard, 0, 0, SKSamplingOptions.Default, AlphaPaint(alpha));

		canvas.Restore();
	}

	private void DrawRouteIntro(SKCanvas canvas)
	{
		float refWidth = _width / _scale;
		float refHeight = _height / _scale;
		const float margin = OverlayElementBounds.Margin;

		canvas.Save();
		canvas.Scale(_scale, _scale);

		using (var backgroundPaint = new SKPaint { Color = new SKColor(10, 12, 16, 225), IsAntialias = true, Style = SKPaintStyle.Fill })
			canvas.DrawRect(0, 0, refWidth, refHeight, backgroundPaint);

		SKRect mapRect = GetRouteIntroMapRect();
		DrawRouteIntroMap(canvas, mapRect);
		DrawRouteIntroStats(canvas, mapRect.Right + margin, mapRect.Top + 30);

		canvas.Restore();
	}

	private void DrawRouteIntroMap(SKCanvas canvas, SKRect mapRect)
	{
		using (var panelPaint = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true, Style = SKPaintStyle.Fill })
			canvas.DrawRect(mapRect, panelPaint);

		if (_routeIntroMosaic is null)
		{
			DrawOutlined(canvas, "MAP UNAVAILABLE", (mapRect.Left + mapRect.Right) / 2, (mapRect.Top + mapRect.Bottom) / 2,
				_labelFont, White, SKTextAlign.Center);
			canvas.DrawRect(mapRect, _ringStroke3White160);
			DrawRouteIntroMapLabel(canvas, mapRect);
			return;
		}

		canvas.Save();
		canvas.ClipRect(mapRect);

		SKImage mosaicImage = _routeIntroMosaic.Image;
		// Cover (Math.Max), not contain: BuildRouteIntroMosaicAsync already padded the mosaic out to
		// mapRect's own aspect ratio (via targetAspectRatio), so the two match up to the rounding from
		// whole tile counts - cover here only ever trims that sub-tile rounding sliver via the ClipRect
		// above, not the route itself.
		float fitScale = Math.Max(mapRect.Width / mosaicImage.Width, mapRect.Height / mosaicImage.Height);
		float drawWidth = mosaicImage.Width * fitScale;
		float drawHeight = mosaicImage.Height * fitScale;
		var destRect = SKRect.Create((mapRect.Left + mapRect.Right) / 2 - drawWidth / 2,
			(mapRect.Top + mapRect.Bottom) / 2 - drawHeight / 2, drawWidth, drawHeight);
		canvas.DrawImage(mosaicImage, destRect, _imageSampling);

		if (_routeIntroTrailPixels is { Count: >= 2 } pixels)
		{
			// Drawn once per card (see DrawRouteIntroCard), so it's built right here and dropped again.
			using var route = new RouteGeometry(RouteAcrossCuts, RouteIntro.ColorBySpeed, _trailSpeedScaleKmh);
			for (int i = 0; i < pixels.Count; i++)
				route.Add(pixels[i], _allFrames[i].SpeedKmh, _allFrames[i].StartsAfterCut);

			var toCard = SKMatrix.CreateScaleTranslation(fitScale, fitScale, destRect.Left, destRect.Top);
			DrawRoute(canvas, route, toCard, TrailColor, 6);
			if (RouteIntro.ShowStartFinish) DrawRouteIntroEnds(canvas, mapRect, pixels, toCard);
		}

		canvas.Restore();
		canvas.DrawRect(mapRect, _ringStroke3White160);
		DrawRouteIntroMapLabel(canvas, mapRect);

		// On the map, the whole time the card is: what every provider's terms ask for at the least.
		if (MapSources.Attribution(RouteIntro.MapProviderId) is { } credit)
			DrawOutlined(canvas, credit, mapRect.Left + 24f, mapRect.Bottom - 24f, _smallFont, White);
	}

	/// <summary>
	///     The trip's first and last fixed positions as dots with a caption each (RouteIntro.StartLabel/FinishLabel, a blank
	///     one leaves only its dot). A loop ending where it began gets one split dot captioned with both.
	/// </summary>
	private void DrawRouteIntroEnds(SKCanvas canvas, SKRect mapRect, List<SKPoint> pixels, SKMatrix toCard)
	{
		int first = -1, last = -1;
		for (int i = 0; i < _allFrames.Count; i++)
		{
			if (!_allFrames[i].Raw.HasGpsFix) continue;
			if (first < 0) first = i;
			last = i;
		}

		if (first < 0) return;

		SKPoint start = toCard.MapPoint(pixels[first]);
		SKPoint finish = toCard.MapPoint(pixels[last]);
		string startLabel = RouteIntro.StartLabel.Trim();
		string finishLabel = RouteIntro.FinishLabel.Trim();
		List<SKPoint> route = RouteOnCard(pixels, toCard);

		if (SKPoint.Distance(start, finish) < RouteEndRadius * 3)
		{
			DrawRouteEndDot(canvas, start, RouteStartColor, RouteFinishColor);
			string both = string.Join(" / ", new[] { startLabel, finishLabel }.Where(l => l.Length > 0));
			DrawRouteEndCaption(canvas, mapRect, start, both, route, []);
			return;
		}

		DrawRouteEndDot(canvas, finish, RouteFinishColor, RouteFinishColor);
		DrawRouteEndDot(canvas, start, RouteStartColor, RouteStartColor);
		List<SKRect> taken = [DotRect(start), DotRect(finish)];
		if (DrawRouteEndCaption(canvas, mapRect, start, startLabel, route, taken) is { } startCaption) taken.Add(startCaption);
		DrawRouteEndCaption(canvas, mapRect, finish, finishLabel, route, taken);
	}

	/// <summary>
	///     The drawn route in the card's coordinates, thinned to points a few pixels apart - enough to tell what a caption
	///     would cover. Only fixed positions; a NaN point breaks it where the route has a gap (a cut drawn as RouteJoin.Gap).
	/// </summary>
	private List<SKPoint> RouteOnCard(List<SKPoint> pixels, SKMatrix toCard)
	{
		List<SKPoint> route = [];
		for (int i = 0; i < pixels.Count; i++)
		{
			if (_allFrames[i].StartsAfterCut && RouteAcrossCuts == RouteJoin.Gap && route.Count > 0) route.Add(new SKPoint(float.NaN, float.NaN));
			if (!_allFrames[i].Raw.HasGpsFix) continue;

			SKPoint point = toCard.MapPoint(pixels[i]);
			if (route.Count == 0 || float.IsNaN(route[^1].X) || SKPoint.Distance(route[^1], point) >= 4) route.Add(point);
		}

		return route;
	}

	private static SKRect DotRect(SKPoint at)
	{
		const float r = RouteEndRadius + 4;
		return new SKRect(at.X - r, at.Y - r, at.X + r, at.Y + r);
	}

	private const float RouteEndRadius = 18f;
	private static readonly SKColor RouteStartColor = new(52, 199, 89);
	private static readonly SKColor RouteFinishColor = new(255, 69, 58);

	private static void DrawRouteEndDot(SKCanvas canvas, SKPoint at, SKColor left, SKColor right)
	{
		using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 150) };
		canvas.DrawCircle(at, RouteEndRadius + 4, paint);

		var dot = SKRect.Create(at.X - RouteEndRadius, at.Y - RouteEndRadius, RouteEndRadius * 2, RouteEndRadius * 2);
		paint.Color = left;
		canvas.DrawArc(dot, 90, 180, true, paint);
		paint.Color = right;
		canvas.DrawArc(dot, 270, 180, true, paint);

		paint.Style = SKPaintStyle.Stroke;
		paint.StrokeWidth = 5;
		paint.Color = SKColors.White;
		canvas.DrawCircle(at, RouteEndRadius, paint);
	}

	/// <summary>
	///     A dark pill next to the dot, on whichever side covers the least of the route: above, below, beside or at a corner,
	///     inside the map and off `taken` (the dots and the other caption). Returns where it went, or null for a blank caption.
	/// </summary>
	private SKRect? DrawRouteEndCaption(SKCanvas canvas, SKRect mapRect, SKPoint at, string text, List<SKPoint> route, List<SKRect> taken)
	{
		if (text.Length == 0) return null;

		SKFont font = _labelFont;
		const float padX = 16f, padY = 10f, gap = 14f, inset = 12f;
		float width = font.MeasureText(text) + padX * 2;
		float height = font.Size + padY * 2;
		float reach = RouteEndRadius + gap;
		SKRect inside = SKRect.Inflate(mapRect, -inset, -inset);

		// In order of preference, the first of the least covering wins.
		(float X, float Y)[] sides = [(0, -1), (0, 1), (1, 0), (-1, 0), (1, -1), (-1, -1), (1, 1), (-1, 1)];
		SKRect best = default;
		int bestCost = int.MaxValue;
		foreach ((float sx, float sy) in sides)
		{
			float left = sx == 0 ? at.X - width / 2 : sx > 0 ? at.X + reach * (sy == 0 ? 1 : 0.7f) : at.X - reach * (sy == 0 ? 1 : 0.7f) - width;
			float top = sy == 0 ? at.Y - height / 2 : sy > 0 ? at.Y + reach * (sx == 0 ? 1 : 0.7f) : at.Y - reach * (sx == 0 ? 1 : 0.7f) - height;
			if (sx == 0) left = Math.Clamp(left, inside.Left, inside.Right - width);
			var pill = SKRect.Create(left, top, width, height);

			int cost = RouteCrossings(route, SKRect.Inflate(pill, 6, 6));
			if (!inside.Contains(pill)) cost += 100_000;
			if (taken.Any(pill.IntersectsWith)) cost += 10_000;
			if (cost >= bestCost) continue;

			best = pill;
			bestCost = cost;
			if (cost == 0) break;
		}

		using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(10, 12, 16, 190) })
			canvas.DrawRoundRect(best, 10, 10, fill);

		SKFontMetrics metrics = font.Metrics;
		float baseline = best.MidY - (metrics.Ascent + metrics.Descent) / 2;
		DrawOutlined(canvas, text, best.MidX, baseline, font, White, SKTextAlign.Center);
		return best;
	}

	/// <summary>How many of the route's segments pass through `rect` (Liang-Barsky clipping per segment).</summary>
	private static int RouteCrossings(List<SKPoint> route, SKRect rect)
	{
		int count = 0;
		for (int i = 1; i < route.Count; i++)
		{
			SKPoint a = route[i - 1], b = route[i];
			if (float.IsNaN(a.X) || float.IsNaN(b.X)) continue;
			if (Math.Max(a.X, b.X) < rect.Left || Math.Min(a.X, b.X) > rect.Right || Math.Max(a.Y, b.Y) < rect.Top ||
			    Math.Min(a.Y, b.Y) > rect.Bottom) continue;

			float dx = b.X - a.X, dy = b.Y - a.Y, t0 = 0, t1 = 1;
			bool Clip(float p, float q)
			{
				if (p == 0) return q >= 0;
				float t = q / p;
				if (p < 0) t0 = Math.Max(t0, t);
				else t1 = Math.Min(t1, t);
				return t0 <= t1;
			}

			if (Clip(-dx, a.X - rect.Left) && Clip(dx, rect.Right - a.X) && Clip(-dy, a.Y - rect.Top) && Clip(dy, rect.Bottom - a.Y)) count++;
		}

		return count;
	}

	/// <summary>
	///     Badge in the map's own bottom-right corner instead of a section header above it - reads as
	///     the map's caption without taking a title row of its own. Drawn the same way whether or not the mosaic itself loaded.
	/// </summary>
	private void DrawRouteIntroMapLabel(SKCanvas canvas, SKRect mapRect)
	{
		const float labelPadding = 24f;
		// _routeIntroLabelFont (36, already used for this card's stat labels), not the shared _labelFont
		// (30) every other widget's small text reuses - bumping that one would resize labels across the
		// whole HUD, not just this badge.
		DrawOutlined(canvas, "ROUTE", mapRect.Right - labelPadding, mapRect.Bottom - labelPadding, _routeIntroLabelFont,
			White, SKTextAlign.Right);
	}

	/// <summary>
	///     The stats RouteIntro.Stats turns on, two to a row in RouteIntroStat.All's order - each pair's two slots fixed, so
	///     turning one stat off never moves another into a different pair. Units come from RouteIntro.Units (a card-level
	///     setting, see OverlaySettings.RouteIntroUnits) - this card has no OverlayElement of its own to carry one.
	/// </summary>
	private void DrawRouteIntroStats(SKCanvas canvas, float x, float startY)
	{
		float y = startY;
		RouteIntroStats[] all = RouteIntroStat.All;
		for (int i = 0; i < all.Length; i += 2)
			DrawStatRow(canvas, x, ref y, RouteIntroStatItem(all[i]), i + 1 < all.Length ? RouteIntroStatItem(all[i + 1]) : null);
	}

	private (string Label, string Value)? RouteIntroStatItem(RouteIntroStats stat)
	{
		if (!RouteIntro.Shows(stat)) return null;

		UnitSystem units = RouteIntro.Units;
		int last = _tripStats.Count - 1;
		string value = stat switch
		{
			RouteIntroStats.Distance => Joined(FormatDistance(_totalDistanceMeters, units)),
			RouteIntroStats.ElevationGain => Joined(FormatAltitude(last >= 0 ? _tripStats.ElevationGainMeters(last) : 0, units)),
			RouteIntroStats.ElevationLoss => Joined(FormatAltitude(last >= 0 ? _tripStats.ElevationLossMeters(last) : 0, units)),
			RouteIntroStats.MaxSpeed => Joined(FormatSpeed(_observedMaxSpeedKmh, units)),
			RouteIntroStats.AverageSpeed => Joined(FormatSpeed(last >= 0 ? _tripStats.AverageSpeedKmh(last) : 0, units)),
			RouteIntroStats.Date => RouteIntroDate(),
			RouteIntroStats.Duration => OverlayTimeFormatting.FormatElapsed(_totalDurationSeconds),
			RouteIntroStats.MovingTime => OverlayTimeFormatting.FormatElapsed(last >= 0 ? _tripStats.MovingSeconds(last) : 0),
			RouteIntroStats.HighestPoint => AltitudeExtreme(Math.Max),
			RouteIntroStats.LowestPoint => AltitudeExtreme(Math.Min),
			RouteIntroStats.MaxLean => MaxLeanText(),
			RouteIntroStats.MaxGForce => MaxWhileMoving(f => f.SmoothedGForce) is { } g ? $"{F(g, "0.0")}G" : "--",
			RouteIntroStats.CameraModel => _cameraModel ?? "--",
			_ => "--"
		};
		return (RouteIntro.LabelOf(stat), value);

		static string Joined((string Value, string Unit) formatted)
		{
			return $"{formatted.Value} {formatted.Unit}";
		}

		string AltitudeExtreme(Func<double, double, double> pick)
		{
			double? extreme = null;
			foreach (DerivedFrame frame in _allFrames)
				if (frame.Raw.HasGpsFix) extreme = extreme is { } e ? pick(e, frame.Raw.AltitudeMeters) : frame.Raw.AltitudeMeters;
			return extreme is { } meters ? Joined(FormatAltitude(meters, units)) : "--";
		}
	}

	private string RouteIntroDate()
	{
		DateTime? utc = _allFrames.Count > 0 ? _allFrames[0].Raw.GpsTimestamp ?? _containerRecordingStartUtc : null;
		if (utc is not { } resolvedUtc) return "--";

		OverlayTimeFormatting.TryFormat(resolvedUtc.ToLocalFromUtc(), null, null, out string dateText);
		return dateText;
	}

	/// <summary>
	///     The deepest lean either way while moving - "--" when there's no tilt at all (a camera whose axes aren't known
	///     reads 0 throughout).
	/// </summary>
	private string MaxLeanText()
	{
		return MaxWhileMoving(f => Math.Abs(f.RollDegrees)) is { } max && max > 0 ? $"{F(max, "0")}°" : "--";
	}

	/// <summary>
	///     The highest `value` while the ride was under way (TripStats.Riding) - stopping, starting or standing, a camera held
	///     or set down tilts and jolts as it likes, as the key moments panel leaves out too. Null when the ride never moved.
	/// </summary>
	private double? MaxWhileMoving(Func<DerivedFrame, double> value)
	{
		bool[] riding = TripStats.Riding(_allFrames);
		double? max = null;
		for (int i = 0; i < _allFrames.Count; i++)
			if (riding[i]) max = Math.Max(max ?? double.MinValue, value(_allFrames[i]));
		return max;
	}

	/// <summary>
	///     One row of up to two stats, RouteIntroStatColumnOffset apart - or nothing at all (no y
	///     advance either) if both are off. A lone right item still draws in the left slot rather than
	///     floating at the column offset with nothing next to it, so toggling one half of a pair off in
	///     Settings doesn't leave a stray indent.
	/// </summary>
	private void DrawStatRow(SKCanvas canvas, float x, ref float y, (string Label, string Value)? left,
		(string Label, string Value)? right)
	{
		if (left is null && right is null) return;

		(string Label, string Value) leftItem = left ?? right!.Value;
		DrawOutlined(canvas, leftItem.Label, x, y, _routeIntroLabelFont, White);
		DrawOutlined(canvas, leftItem.Value, x, y + 66, _routeIntroValueFont, Accent);

		if (left is not null && right is { } rightItem)
		{
			float rightX = x + RouteIntroStatColumnOffset;
			DrawOutlined(canvas, rightItem.Label, rightX, y, _routeIntroLabelFont, White);
			DrawOutlined(canvas, rightItem.Value, rightX, y + 66, _routeIntroValueFont, Accent);
		}

		y += 150;
	}
}
