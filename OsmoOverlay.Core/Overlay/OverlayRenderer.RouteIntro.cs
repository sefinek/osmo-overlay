using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The optional fullscreen "whole route" card shown for RouteIntro.DurationSeconds at the start
///     of the render - a static overview (the whole route mosaic drawn fit-to-rect, not panned like
///     DrawMapWidget) plus whichever trip stats RouteIntro.Show* enables. Independent of MapWidget:
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
	///     Render (which draws the crossfade itself) and DrawWatermark/DrawMapAttributionSlide (whose
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
	private string? _preparedRouteIntroKey;
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
		(RouteMapMosaic? mosaic, var key) = await BuildRouteIntroMosaicAsync(onTileProgress, ct);
		ApplyRouteIntroMapMosaic(mosaic, key);
	}

	/// <summary>See BuildMapMosaicAsync - same fetch-only/apply split so a live-preview caller can run the network I/O without holding the render lock.</summary>
	public async Task<(RouteMapMosaic? Mosaic, string? Key)> BuildRouteIntroMosaicAsync(
		Action<int, int>? onTileProgress = null, CancellationToken ct = default)
	{
		if (!RouteIntro.Enabled) return (null, null);

		List<(double Lat, double Lon)> points = [.. _allFrames.Select(f => (f.Raw.Latitude, f.Raw.Longitude))];
		var urlTemplate = ResolveUrlTemplate();
		_preparedRouteIntroKey = urlTemplate;

		SKRect mapRect = GetRouteIntroMapRect();
		var targetAspect = (double)(mapRect.Width / mapRect.Height);

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
				onTileProgress, targetAspect), ct), urlTemplate);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "Route intro: failed to prepare the route overview map - the card will show without a map.");
			return (null, urlTemplate);
		}
	}

	/// <summary>See ApplyMapMosaic - same superseded-fetch guard, plus projecting the whole route's trail once up front instead of per draw call.</summary>
	public void ApplyRouteIntroMapMosaic(RouteMapMosaic? mosaic, string? forKey)
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

	/// <summary>See NeedsMapPrepare - lets a live-preview caller know a fresh fetch is worth making.</summary>
	public bool NeedsRouteIntroMapPrepare()
	{
		return RouteIntro.Enabled && _preparedRouteIntroKey != ResolveUrlTemplate();
	}

	/// <summary>
	///     Single source of truth for the map card's rect, read both when drawing it and (via
	///     BuildRouteIntroMosaicAsync) when deciding what aspect ratio to fetch the mosaic at - the two
	///     must agree, or the draw side is back to choosing between letterboxing and cropping the route.
	/// </summary>
	private SKRect GetRouteIntroMapRect()
	{
		var refWidth = _width / _scale;
		var refHeight = _height / _scale;
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
		{
			canvas.DrawImage(_routeIntroCard, 0, 0, SKSamplingOptions.Default);
		}
		else
		{
			canvas.DrawImage(_routeIntroCard, 0, 0, SKSamplingOptions.Default, AlphaPaint(alpha));
		}

		canvas.Restore();
	}

	private void DrawRouteIntro(SKCanvas canvas)
	{
		var refWidth = _width / _scale;
		var refHeight = _height / _scale;
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
		var fitScale = Math.Max(mapRect.Width / mosaicImage.Width, mapRect.Height / mosaicImage.Height);
		var drawWidth = mosaicImage.Width * fitScale;
		var drawHeight = mosaicImage.Height * fitScale;
		var destRect = SKRect.Create((mapRect.Left + mapRect.Right) / 2 - drawWidth / 2,
			(mapRect.Top + mapRect.Bottom) / 2 - drawHeight / 2, drawWidth, drawHeight);
		canvas.DrawImage(mosaicImage, destRect, SKSamplingOptions.Default);

		if (_routeIntroTrailPixels is { Count: >= 2 } pixels)
		{
			// Drawn once per card (see DrawRouteIntroCard), so it's built right here and dropped again.
			using var route = new RouteGeometry(RouteAcrossCuts, RouteIntro.ColorBySpeed, _trailSpeedScaleKmh);
			for (var i = 0; i < pixels.Count; i++)
				route.Add(pixels[i], _allFrames[i].SpeedKmh, _allFrames[i].StartsAfterCut);

			DrawRoute(canvas, route, SKMatrix.CreateScaleTranslation(fitScale, fitScale, destRect.Left, destRect.Top), TrailColor, 6);
		}

		canvas.Restore();
		canvas.DrawRect(mapRect, _ringStroke3White160);
		DrawRouteIntroMapLabel(canvas, mapRect);
	}

	/// <summary>
	///     Badge in the map's own bottom-right corner instead of a section header above it - reads as
	///     the map's caption, and reclaims the vertical space a separate title row used to take above
	///     the card. Drawn the same way whether or not the mosaic itself loaded.
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

	/// <summary>Units come from RouteIntro.Units (a card-level setting, see OverlaySettings.RouteIntroUnits) rather than a per-widget OverlayElement.Units - this card has no OverlayElement of its own to carry one.</summary>
	private void DrawRouteIntroStats(SKCanvas canvas, float x, float startY)
	{
		(string Label, string Value)? distance = null;
		if (RouteIntro.ShowDistance)
		{
			var (value, unit) = FormatDistance(_totalDistanceMeters, RouteIntro.Units);
			distance = ("DISTANCE", $"{value} {unit}");
		}

		(string Label, string Value)? elevationGain = null;
		if (RouteIntro.ShowElevationGain)
		{
			var (value, unit) = FormatDistance(_totalElevationGainMeters, RouteIntro.Units);
			elevationGain = ("ELEVATION GAIN", $"{value} {unit}");
		}

		(string Label, string Value)? maxSpeed = null;
		if (RouteIntro.ShowMaxSpeed)
		{
			var (value, unit) = FormatSpeed(_observedMaxSpeedKmh, RouteIntro.Units);
			maxSpeed = ("MAX SPEED", $"{value} {unit}");
		}

		(string Label, string Value)? avgSpeed = null;
		if (RouteIntro.ShowAvgSpeed)
		{
			var (value, unit) = FormatSpeed(_avgSpeedKmh, RouteIntro.Units);
			avgSpeed = ("AVG SPEED", $"{value} {unit}");
		}

		(string Label, string Value)? date = null;
		if (RouteIntro.ShowDate)
		{
			DateTime? utc = _allFrames.Count > 0 ? _allFrames[0].Raw.GpsTimestamp ?? _containerRecordingStartUtc : null;
			string dateText;
			if (utc is { } resolvedUtc) OverlayTimeFormatting.TryFormat(resolvedUtc.ToLocalFromUtc(), null, null, out dateText);
			else dateText = "--";
			date = ("DATE", dateText);
		}

		(string Label, string Value)? duration = null;
		if (RouteIntro.ShowDuration) duration = ("DURATION", OverlayTimeFormatting.FormatElapsed(_totalDurationSeconds));

		(string Label, string Value)? camera = RouteIntro.ShowCameraModel ? ("CAMERA", _cameraModel ?? "--") : null;

		var y = startY;
		// Paired thematically - distance with elevation, the two speeds, date with duration - so
		// related numbers read side by side instead of the card turning into one long single-file
		// list; camera has no natural partner, so it stays full-width on its own row.
		DrawStatRow(canvas, x, ref y, distance, elevationGain);
		DrawStatRow(canvas, x, ref y, maxSpeed, avgSpeed);
		DrawStatRow(canvas, x, ref y, date, duration);
		DrawStatRow(canvas, x, ref y, camera, null);
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
			var rightX = x + RouteIntroStatColumnOffset;
			DrawOutlined(canvas, rightItem.Label, rightX, y, _routeIntroLabelFont, White);
			DrawOutlined(canvas, rightItem.Value, rightX, y + 66, _routeIntroValueFont, Accent);
		}

		y += 150;
	}
}
