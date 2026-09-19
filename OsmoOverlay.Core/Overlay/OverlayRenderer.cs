using System.Globalization;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Core: renderer state (fonts, cached paints), construction/disposal, and the per-frame Render()
///     dispatch that switches on each visible OverlayElement's type. The actual per-widget drawing
///     code lives in sibling partial-class files grouped by widget family: OverlayRenderer.Position.cs
///     (Compass + MapWidget + the shared route trail), OverlayRenderer.Gauges.cs (SpeedGauge +
///     PitchGauge + SunWidget + GMeter + TripProgressBar), OverlayRenderer.TextWidgets.cs
///     (DateTimeText/UtcTimeText + ElapsedTimeText + CameraModelText + Elevation/Gradient/Distance +
///     CameraInfo), OverlayRenderer.Watermark.cs (the "Made with OsmoOverlay" watermark + map
///     attribution slide), and OverlayRenderer.RouteIntro.cs (the optional fullscreen route-overview
///     card shown at the start of the render).
/// </summary>
public sealed partial class OverlayRenderer : IDisposable
{
	private static readonly SKColor White = SKColors.White;
	private static readonly SKColor Accent = new(70, 190, 255);
	private static readonly SKColor TrailColor = new(70, 220, 110);
	private static readonly SKColor SunColor = new(255, 175, 45);
	private static readonly SKColor Shadow = new(0, 0, 0, 225);
	private static readonly SKColor PanelFill = new(0, 0, 0, 55);

	// Canvas dimensions and the per-resolution scale every widget draws at (see OverlayElementBounds.GetScale).
	private readonly int _width;
	private readonly int _height;
	private readonly float _scale;

	// Per-file data the whole render depends on, handed in once at construction.
	private readonly IReadOnlyList<DerivedFrame> _allFrames;
	private readonly string? _cameraModel;
	private readonly DateTime? _containerRecordingStartUtc;
	private readonly double _startAltitude;
	private readonly double _observedMaxSpeedKmh;
	// Cached once from _allFrames rather than recomputed on every one of DrawRouteIntro's per-frame
	// calls during the whole route-intro card - the source data (cumulative distance, sample time,
	// altitude) is already fully known as soon as the frame list is handed in, and none of these
	// change from one frame to the next.
	private readonly double _totalDistanceMeters;
	private readonly double _totalDurationSeconds;
	private readonly double _totalElevationGainMeters;
	private readonly double _avgSpeedKmh;

	private readonly SKTypeface _hudTypeface;
	private readonly SKFont _dateFont;
	private readonly SKFont _labelFont;
	private readonly SKFont _smallFont;
	private readonly SKFont _speedFont;
	private readonly SKFont _speedUnitFont;
	private readonly SKFont _unitFont;
	private readonly SKFont _valueFont;
	private readonly SKFont _watermarkSubtitleFont;
	private readonly SKFont _watermarkTitleFont;
	// Route intro's stat list gets its own, larger sizes rather than reusing _labelFont/_dateFont -
	// those are shared by every other widget (Compass, TextWidgets, ...), so bumping them up would
	// resize the whole HUD, not just this one card.
	private readonly SKFont _routeIntroLabelFont;
	private readonly SKFont _routeIntroValueFont;

	// Fixed-style paints (color/width never change frame to frame) reused across widgets, cached once
	// here the same way fonts already are above - Render() runs once per output frame, so allocating
	// these fresh per widget per frame (as this file used to) is pure per-frame GC churn for a value
	// that's always identical.
	private readonly SKPaint _panelFillPaint;
	private readonly SKPaint _ringStroke3White160;
	private readonly SKPaint _ringStroke3White140;
	private readonly SKPaint _ringStroke5White150;
	private readonly SKPaint _thinStroke2White70;
	private readonly SKPaint _dotOutlineBlackFill;
	private readonly SKPaint _dotFillAccent;
	private readonly SKPaint _blackStroke3;
	private readonly SKPaint _blackStroke2;
	private readonly SKPaint _sunFillPaint;
	private readonly SKPaint _whiteStroke6Round;
	private readonly SKPaint _speedBandGreen;
	private readonly SKPaint _speedBandYellow;
	private readonly SKPaint _speedBandOrange;
	private readonly SKPaint _speedBandRed;

	public OverlayRenderer(int width, int height, double startAltitude, IReadOnlyList<OverlayElement> layout,
		IReadOnlyList<DerivedFrame> allFrames, double observedMaxSpeedKmh = 0, bool showWatermark = true,
		string? cameraModel = null, DateTime? containerRecordingStartUtc = null,
		string? mapTileUrlTemplate = null, string? mapAttribution = null, bool mapShowAttribution = true,
		string? mapApiKey = null, RouteIntroSettings? routeIntro = null)
	{
		_width = width;
		_height = height;
		_scale = OverlayElementBounds.GetScale(width, height);

		_allFrames = allFrames;
		_cameraModel = cameraModel;
		_containerRecordingStartUtc = containerRecordingStartUtc;
		_startAltitude = startAltitude;
		_observedMaxSpeedKmh = observedMaxSpeedKmh;
		_totalDistanceMeters = allFrames.Count > 0 ? allFrames[^1].CumulativeDistanceMeters : 0;
		_totalDurationSeconds = allFrames.Count > 0 ? allFrames[^1].Raw.SampleTimeSeconds - allFrames[0].Raw.SampleTimeSeconds : 0;
		// Sum of positive altitude deltas only (a simple running climb total, not the true barometric
		// "elevation gain" a dedicated sensor would give) - GPS altitude jitter means this reads a bit
		// high on flat ground, but it's the only altitude source this app has.
		_totalElevationGainMeters = 0;
		for (var i = 1; i < allFrames.Count; i++)
		{
			var delta = allFrames[i].Raw.AltitudeMeters - allFrames[i - 1].Raw.AltitudeMeters;
			if (delta > 0) _totalElevationGainMeters += delta;
		}

		_avgSpeedKmh = _totalDurationSeconds > 0 ? _totalDistanceMeters / _totalDurationSeconds * 3.6 : 0;

		Layout = layout;
		ShowWatermark = showWatermark;
		MapTileUrlTemplate = mapTileUrlTemplate;
		MapAttribution = mapAttribution;
		MapShowAttribution = mapShowAttribution;
		MapApiKey = mapApiKey;
		RouteIntro = routeIntro ?? RouteIntroSettings.Disabled;

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
		_routeIntroLabelFont = new SKFont(_hudTypeface, 36);
		_routeIntroValueFont = new SKFont(_hudTypeface, 56);

		_panelFillPaint = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		_ringStroke3White160 = new SKPaint
			{ Color = new SKColor(255, 255, 255, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
		_ringStroke3White140 = new SKPaint
			{ Color = new SKColor(255, 255, 255, 140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
		_ringStroke5White150 = new SKPaint
			{ Color = new SKColor(255, 255, 255, 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5 };
		_thinStroke2White70 = new SKPaint
			{ Color = new SKColor(255, 255, 255, 70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
		_dotOutlineBlackFill = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
		_dotFillAccent = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		_blackStroke3 = new SKPaint
			{ Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
		_blackStroke2 = new SKPaint
			{ Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
		_sunFillPaint = new SKPaint { Color = SunColor, IsAntialias = true, Style = SKPaintStyle.Fill };
		_whiteStroke6Round = new SKPaint
		{
			Color = White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round
		};
		_speedBandGreen = CreateGaugeBandPaint(new SKColor(70, 200, 90));
		_speedBandYellow = CreateGaugeBandPaint(new SKColor(230, 200, 60));
		_speedBandOrange = CreateGaugeBandPaint(new SKColor(235, 140, 50));
		_speedBandRed = CreateGaugeBandPaint(new SKColor(220, 60, 60));
	}

	/// <summary>Mutable so the GUI editor can reposition/toggle elements without rebuilding fonts.</summary>
	public IReadOnlyList<OverlayElement> Layout { get; set; }

	/// <summary>Mutable so the GUI can toggle it live from Settings without recreating the renderer.</summary>
	public bool ShowWatermark { get; set; }

	// Mutable for the same reason as ShowWatermark above - see OverlaySettings for why these are
	// global rather than per-element.
	public string? MapTileUrlTemplate { get; set; }
	public string? MapAttribution { get; set; }
	public bool MapShowAttribution { get; set; }
	public string? MapApiKey { get; set; }

	/// <summary>Mutable so the GUI can toggle/reconfigure it live from Settings without recreating the renderer.</summary>
	public RouteIntroSettings RouteIntro { get; set; }

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
		_routeIntroLabelFont.Dispose();
		_routeIntroValueFont.Dispose();
		_panelFillPaint.Dispose();
		_ringStroke3White160.Dispose();
		_ringStroke3White140.Dispose();
		_ringStroke5White150.Dispose();
		_thinStroke2White70.Dispose();
		_dotOutlineBlackFill.Dispose();
		_dotFillAccent.Dispose();
		_blackStroke3.Dispose();
		_blackStroke2.Dispose();
		_sunFillPaint.Dispose();
		_whiteStroke6Round.Dispose();
		_speedBandGreen.Dispose();
		_speedBandYellow.Dispose();
		_speedBandOrange.Dispose();
		_speedBandRed.Dispose();
		_mapMosaic?.Dispose();
		_routeIntroMosaic?.Dispose();
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

		var sampleTime = frame.Raw.SampleTimeSeconds;
		var introEnd = RouteIntro.DurationSeconds;
		var isRouteIntroFrame = RouteIntro.Enabled && sampleTime < introEnd;
		// Non-null only inside the crossfade window right before introEnd: 0 at its start (intro still
		// fully opaque) to 1 at introEnd (intro fully gone, widgets fully opaque takes over exactly as
		// the plain isRouteIntroFrame branch below would from here on).
		var transitionStart = RouteIntroTransitionStartSeconds;
		float? crossfadeT = isRouteIntroFrame && sampleTime >= transitionStart
			? (float)((sampleTime - transitionStart) / (introEnd - transitionStart))
			: null;

		string? routeIntroMapAttribution() => _routeIntroMosaic is not null && MapShowAttribution
			? MapAttribution ?? MapTileFetcher.OpenStreetMapAttribution
			: null;

		string? mapAttribution;

		if (isRouteIntroFrame)
		{
			if (crossfadeT is { } t)
			{
				DrawWithAlpha(canvas, 1 - t, c => DrawRouteIntro(c, frame));
				string? widgetsAttribution = null;
				DrawWithAlpha(canvas, t, c => widgetsAttribution = DrawWidgets(c, frame));
				// The card's own attribution is about to disappear along with it - once the widgets
				// underneath are visible at all, their attribution requirement (if any) is what matters
				// going forward.
				mapAttribution = widgetsAttribution ?? routeIntroMapAttribution();
			}
			else
			{
				DrawRouteIntro(canvas, frame);
				mapAttribution = routeIntroMapAttribution();
			}
		}
		else
		{
			mapAttribution = DrawWidgets(canvas, frame);
		}

		// On the route-intro card, centering under the whole frame (the normal-frame default) lands the
		// watermark under the map alone (which only occupies the card's left portion) rather than the
		// card as a whole - right-aligned to the same margin the stats column and map panel already use
		// reads as part of that summary instead.
		float? watermarkAnchorX = isRouteIntroFrame ? _width - OverlayElementBounds.Margin * _scale : null;
		var watermarkAlign = isRouteIntroFrame ? SKTextAlign.Right : SKTextAlign.Center;

		if (ShowWatermark)
		{
			DrawWatermark(canvas, frame.Raw.SampleTimeSeconds, watermarkAnchorX, watermarkAlign);
			if (mapAttribution is not null)
				DrawMapAttributionSlide(canvas, frame.Raw.SampleTimeSeconds, mapAttribution, watermarkAnchorX, watermarkAlign);
		}
		else if (mapAttribution is not null)
		{
			DrawMapAttributionOnly(canvas, mapAttribution, watermarkAnchorX, watermarkAlign);
		}

		return bitmap.Bytes;
	}

	/// <summary>The normal (non-route-intro) per-frame widget pass. Returns the map attribution text to show, if any visible MapWidget needs one - see Render's mapAttribution.</summary>
	private string? DrawWidgets(SKCanvas canvas, DerivedFrame frame)
	{
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
					if (MapShowAttribution) mapAttribution = MapAttribution ?? MapTileFetcher.OpenStreetMapAttribution;
					break;
				case OverlayElementType.SpeedGauge:
					DrawSpeedGauge(canvas, element, frame.SpeedKmh);
					break;
				case OverlayElementType.CameraInfo:
					DrawCameraInfo(canvas, frame, element);
					break;
				case OverlayElementType.ElapsedTimeText:
					DrawElapsedTime(canvas, frame, element);
					break;
				case OverlayElementType.CameraModelText:
					DrawCameraModel(canvas, element);
					break;
				case OverlayElementType.GMeter:
					DrawGMeter(canvas, frame, element);
					break;
				case OverlayElementType.TripProgressBar:
					DrawTripProgressBar(canvas, frame, element);
					break;
			}
		}

		return mapAttribution;
	}

	/// <summary>
	///     Group opacity for the route-intro crossfade (see Render): SaveLayer/Restore composites
	///     everything `draw` does as one flattened group at `alpha`, rather than needing every widget
	///     it calls into to accept and thread through an opacity parameter of its own.
	/// </summary>
	private static void DrawWithAlpha(SKCanvas canvas, float alpha, Action<SKCanvas> draw)
	{
		using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(alpha * 255f, 0, 255)) };
		canvas.SaveLayer(paint);
		draw(canvas);
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

	private static string F(double value, string format)
	{
		return value.ToString(format, CultureInfo.InvariantCulture);
	}

	/// <summary>
	///     A single EMA-smoothed value driven by SampleTimeSeconds instead of wall-clock time, used by
	///     both GetMapZoomFactor (map crop) and SmoothGMeterDelta (G-meter baseline/signal) - both need
	///     the same shape: smooth toward a per-frame target normally, but snap straight to it when time
	///     goes backwards or jumps forward by more than resetGapSeconds, since that means a scrub/seek
	///     landed on a new position, not a continuous run of frames to smooth across.
	/// </summary>
	private sealed class ResettableEma
	{
		private double? _lastSeconds;

		public double Value { get; private set; }

		public double Update(double seconds, double target, double timeConstantSeconds, double resetGapSeconds = 2.0)
		{
			if (_lastSeconds is not { } last || seconds <= last || seconds - last > resetGapSeconds)
			{
				Value = target;
			}
			else
			{
				var alpha = 1.0 - Math.Exp(-(seconds - last) / timeConstantSeconds);
				Value += (target - Value) * alpha;
			}

			_lastSeconds = seconds;
			return Value;
		}
	}
}
