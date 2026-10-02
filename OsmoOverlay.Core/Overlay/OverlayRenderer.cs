using System.Globalization;
using System.Runtime.InteropServices;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Core: renderer state (fonts, cached paints), construction/disposal, and the per-frame RenderInto()
///     dispatch that switches on each visible OverlayElement's type. The actual per-widget drawing
///     code lives in sibling partial-class files grouped by widget family: OverlayRenderer.Position.cs
///     (Compass + MapWidget + the shared route trail), OverlayRenderer.Gauges.cs (SpeedGauge +
///     RollGauge + PitchGauge + SunWidget + GMeter + TripProgressBar), OverlayRenderer.TextWidgets.cs
///     (DateTimeText/UtcTimeText + ElapsedTimeText + CameraModelText + Text + Elevation/Gradient/Distance +
///     TripStat + CameraInfo), OverlayRenderer.Charts.cs (ProfileChart), OverlayRenderer.Image.cs (Image),
///     OverlayRenderer.Watermark.cs (the "Made with OsmoOverlay" watermark + map
///     attribution slide), and OverlayRenderer.RouteIntro.cs (the optional fullscreen route-overview
///     card shown at the start of the render).
/// </summary>
public sealed partial class OverlayRenderer : IDisposable
{
	// What a widget's TextColor/AccentColor/OutlineColor/TrailColor fall back to - the GUI's color boxes show the same.
	public const string DefaultTextColorHex = "#FFFFFF";
	public const string DefaultAccentColorHex = "#46BEFF";
	public const string DefaultOutlineColorHex = "#E1000000";
	public const string DefaultTrailColorHex = "#46DC6E";

	private static readonly SKColor White = SKColor.Parse(DefaultTextColorHex);
	private static readonly SKColor Accent = SKColor.Parse(DefaultAccentColorHex);
	private static readonly SKColor TrailColor = SKColor.Parse(DefaultTrailColorHex);
	private static readonly SKColor SunColor = new(255, 175, 45);
	private static readonly SKColor Shadow = SKColor.Parse(DefaultOutlineColorHex);

	// Canvas dimensions and the per-resolution scale every widget draws at (see OverlayElementBounds.GetScale).
	private readonly int _width;
	private readonly int _height;
	private readonly float _scale;

	private readonly string? _cameraModel;
	private readonly DateTime? _containerRecordingStartUtc;

	// The telemetry the whole render draws from - handed in at construction, replaced by SetFrames when the preview's
	// cuts change. Everything below it is computed once from it (ApplyFrames) rather than on every frame.
	private IReadOnlyList<DerivedFrame> _allFrames = [];
	private (double Lateral, double Longitudinal)[] _gMeterDeltas = [];
	private double _startAltitude;
	private double _observedMaxSpeedKmh;
	private double _totalDistanceMeters;
	private double _totalDurationSeconds;
	private TripStats _tripStats = TripStats.Compute([]);
	// A panel's fill when a widget doesn't set its own: black at 55% - much lower and the panels read as barely-there on
	// bright footage (sky, water, sand), their footprint (which matches the GUI's selection/hit box, see OverlayElementBounds)
	// looking like mostly-empty padding.
	public const float PanelOpacityDefault = 0.55f;
	// Far wider than any widget draws around its anchor - only bounds what MeasureElement records.
	private static readonly SKRect MeasureArea = new(-100_000, -100_000, 100_000, 100_000);
	private const int MeasureMaxPixels = 16_000_000;
	private const byte MeasureMinAlpha = 16;
	// The speed a route colored by speed reaches full red at (ComputeTrailSpeedScale).
	private double _trailSpeedScaleKmh;

	private readonly SKTypeface _hudTypeface;
	private readonly SKFont _dateFont;
	private readonly SKFont _labelFont;
	private readonly SKFont _smallFont;
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
	// here the same way fonts already are above - RenderInto() runs once per output frame, so allocating
	// these fresh per widget per frame would be pure per-frame GC churn for a value
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
	private readonly SKPaint _accentStroke4Round;
	private readonly SKPaint _speedBandGreen;
	private readonly SKPaint _speedBandYellow;
	private readonly SKPaint _speedBandOrange;
	private readonly SKPaint _speedBandRed;
	private readonly SKPaint _speedRingOuterPaint;
	private readonly SKPaint _speedRingArcPaint;
	private readonly SKShader _speedRingShader;
	private readonly SKPaint _speedDefaultArcPaint;
	private readonly SKShader _speedDefaultShader;
	private readonly SKPath _speedDefaultNeedle = CreateDefaultNeedle();
	private readonly SKPath _speedRingNeedle = CreateRingNeedle();
	private readonly SKPaint _whiteFill = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
	// Recolored per widget with its MarkerColor, like the panel fill.
	private readonly SKPaint _markerFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

	// Mutable paints reused by DrawOutlined (see below) - unlike the fixed-style paints
	// above, color/stroke width/blur radius vary per call (font size, requested color, fade opacity), so
	// these can't be assigned once at construction - instead their properties are overwritten right
	// before each draw and the same instances are reused, avoiding a fresh SKPaint (and, for the blur
	// variants, a fresh native blur kernel) on every single piece of HUD text drawn every frame. Safe
	// because RenderInto() is only ever called sequentially for a given instance - see RenderJob's single
	// render loop - never concurrently.
	private readonly SKPaint _outlineShadowPaint;
	private readonly SKPaint _outlineStrokePaint;
	private readonly SKPaint _outlineFillPaint;
	// Recolored/resized per route by RouteGeometry.Draw, and set to a layer's or image's opacity by AlphaPaint.
	private readonly SKPaint _routeStrokePaint;
	private readonly SKPaint _routeDashPaint;
	private readonly SKPaint _alphaPaint = new();
	private readonly SKPath _headingArrow = CreateHeadingArrow();
	private readonly Dictionary<float, SKPathEffect> _dashEffects = [];

	// Blur mask filters keyed by sigma (font.Size * 0.04 for text shadows, radius * 0.12 for panel
	// shadows) - both draw from a small, closed set of font sizes/widget radii fixed at construction
	// time, so this fills in lazily and then never grows past a handful of entries for the rest of the
	// renderer's lifetime.
	private readonly Dictionary<float, SKMaskFilter> _blurMaskFilters = [];

	// Per-element FontFamily overrides on the text widgets (see OverlayRenderer.TextWidgets.cs) - unlike
	// the fixed fonts above, these come from arbitrary user input, so they fill in lazily instead of being
	// built upfront. Keyed separately from _blurMaskFilters' single-float key since a font also varies by
	// family; typefaces are cached (and disposed) independently of the fonts built from them since several
	// sizes can share one typeface (see ResolveTypeface/GetFont).
	private readonly Dictionary<string, SKTypeface> _customTypefacesByFamily = [];
	private readonly Dictionary<(string Family, float Size), SKFont> _fontCache = [];

	public OverlayRenderer(int width, int height, double startAltitude, IReadOnlyList<OverlayElement> layout,
		IReadOnlyList<DerivedFrame> allFrames, double observedMaxSpeedKmh = 0, bool showWatermark = true,
		string? cameraModel = null, DateTime? containerRecordingStartUtc = null,
		MapSources? mapSources = null, RouteIntroSettings? routeIntro = null)
	{
		_width = width;
		_height = height;
		_scale = OverlayElementBounds.GetScale(width, height);

		_cameraModel = cameraModel;
		_containerRecordingStartUtc = containerRecordingStartUtc;
		ApplyFrames(allFrames, startAltitude, observedMaxSpeedKmh);

		Layout = layout;
		ShowWatermark = showWatermark;
		MapSources = mapSources ?? new MapSources();
		RouteIntro = routeIntro ?? RouteIntroSettings.Disabled;

		_hudTypeface = OverlayElementBounds.CreateHudTypeface();

		_dateFont = new SKFont(_hudTypeface, OverlayElementBounds.DateFontSize);
		_labelFont = new SKFont(_hudTypeface, OverlayElementBounds.LabelFontSize);
		_valueFont = new SKFont(_hudTypeface, OverlayElementBounds.ValueFontSize);
		_unitFont = new SKFont(_hudTypeface, OverlayElementBounds.UnitFontSize);
		_smallFont = new SKFont(_hudTypeface, OverlayElementBounds.SmallFontSize);
		_watermarkTitleFont = new SKFont(_hudTypeface, 40);
		_watermarkSubtitleFont = new SKFont(_hudTypeface, 30);
		_routeIntroLabelFont = new SKFont(_hudTypeface, 36);
		_routeIntroValueFont = new SKFont(_hudTypeface, 56);

		_panelFillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
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
		_accentStroke4Round = new SKPaint
		{
			Color = Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeCap = SKStrokeCap.Round
		};
		_speedBandGreen = CreateGaugeBandPaint(new SKColor(70, 200, 90));
		_speedBandYellow = CreateGaugeBandPaint(new SKColor(230, 200, 60));
		_speedBandOrange = CreateGaugeBandPaint(new SKColor(235, 140, 50));
		_speedBandRed = CreateGaugeBandPaint(new SKColor(220, 60, 60));
		_speedDefaultShader = SKShader.CreateSweepGradient(new SKPoint(0, 0),
			[new SKColor(60, 190, 70), new SKColor(235, 200, 50), new SKColor(240, 130, 40), new SKColor(225, 45, 50)],
			[0f, 0.42f, 0.74f, 1f], SKShaderTileMode.Clamp, 0, 270, SKMatrix.CreateRotationDegrees(135));
		_speedDefaultArcPaint = new SKPaint
			{ IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 20, StrokeCap = SKStrokeCap.Butt, Shader = _speedDefaultShader };
		_speedRingOuterPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
		_speedRingShader = SKShader.CreateSweepGradient(new SKPoint(0, 0),
			[new SKColor(60, 190, 70), new SKColor(245, 180, 40), new SKColor(240, 120, 40), new SKColor(225, 40, 50)],
			[0f, 0.4f, 0.72f, 1f], SKShaderTileMode.Clamp, 0, RingArcSweep, SKMatrix.CreateRotationDegrees(RingArcStartAngle));
		_speedRingArcPaint = new SKPaint
			{ IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 20, StrokeCap = SKStrokeCap.Butt, Shader = _speedRingShader };

		_outlineShadowPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
		_outlineStrokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
		_outlineFillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
		_routeStrokePaint = new SKPaint
		{
			IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
		};
		_routeDashPaint = new SKPaint
		{
			IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round
		};
	}

	/// <summary>Mutable so the GUI editor can reposition/toggle elements without rebuilding fonts.</summary>
	public IReadOnlyList<OverlayElement> Layout { get; set; }

	/// <summary>Mutable so the GUI can toggle it live from Settings without recreating the renderer.</summary>
	public bool ShowWatermark { get; set; }

	/// <summary>Mutable for the same reason as ShowWatermark above: a key typed in the GUI applies without a new renderer.</summary>
	public MapSources MapSources { get; set; }

	/// <summary>Mutable so the GUI can toggle/reconfigure it live from Settings without recreating the renderer.</summary>
	public RouteIntroSettings RouteIntro { get; set; }

	/// <summary>How the drawn routes cross a cut - mutable like the above, the cut editor switches it live.</summary>
	public RouteJoin RouteAcrossCuts { get; set; }

	public void Dispose()
	{
		_hudTypeface.Dispose();
		_dateFont.Dispose();
		_labelFont.Dispose();
		_valueFont.Dispose();
		_unitFont.Dispose();
		_smallFont.Dispose();
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
		_accentStroke4Round.Dispose();
		_chartFillPaint.Dispose();
		_chartLinePaint.Dispose();
		ClearProfiles();
		DisposeImages();
		_speedBandGreen.Dispose();
		_speedBandYellow.Dispose();
		_speedBandOrange.Dispose();
		_speedBandRed.Dispose();
		_speedRingOuterPaint.Dispose();
		_speedRingArcPaint.Dispose();
		_speedRingShader.Dispose();
		_speedDefaultArcPaint.Dispose();
		_speedDefaultShader.Dispose();
		_speedDefaultNeedle.Dispose();
		_speedRingNeedle.Dispose();
		_whiteFill.Dispose();
		_markerFillPaint.Dispose();
		_outlineShadowPaint.Dispose();
		_outlineStrokePaint.Dispose();
		_outlineFillPaint.Dispose();
		_routeStrokePaint.Dispose();
		_routeDashPaint.Dispose();
		_alphaPaint.Dispose();
		_shadowPaint.Dispose();
		DisposeShadowFilters();
		_headingArrow.Dispose();
		foreach (SKPathEffect effect in _dashEffects.Values) effect.Dispose();
		DisposeRoutes(_compassRoutes);
		DisposeRoutes(_mapRoutes);
		foreach (SKMaskFilter filter in _blurMaskFilters.Values) filter.Dispose();
		foreach (SKFont font in _fontCache.Values) font.Dispose();
		// A family that isn't installed on this machine resolves to _hudTypeface itself (see
		// ResolveTypeface's fallback) - skip those entries so it isn't disposed twice.
		foreach (SKTypeface typeface in _customTypefacesByFamily.Values)
		{
			if (!ReferenceEquals(typeface, _hudTypeface))
				typeface.Dispose();
		}

		_mapMosaic?.Dispose();
		_routeIntroMosaic?.Dispose();
		_routeIntroCard?.Dispose();
	}

	/// <summary>
	///     Swaps the telemetry for another set of the same recording - the preview's, when its cuts change (see
	///     OutputTimeline) - keeping fonts, paints and the map mosaics, so the map doesn't drop back to its placeholder
	///     while tiles are stitched again. The trail and the route-intro card start over from the new frames; the map
	///     widget's mosaic stays as long as MapMosaicCoversRoute, the route intro's until a new one is applied.
	/// </summary>
	public void SetFrames(IReadOnlyList<DerivedFrame> allFrames, double startAltitude, double observedMaxSpeedKmh)
	{
		ApplyFrames(allFrames, startAltitude, observedMaxSpeedKmh);

		_trail.Clear();
		_trailPixels.Clear();
		_trailCacheIndex = -1;
		DisposeRoutes(_compassRoutes);
		DisposeRoutes(_mapRoutes);
		_routeIntroCard?.Dispose();
		_routeIntroCard = null;
		if (_routeIntroMosaic is { } mosaic) _routeIntroTrailPixels = ProjectRoute(mosaic);
	}

	/// <summary>False once SetFrames brought in route points outside what the map widget's mosaic was built around.</summary>
	public bool MapMosaicCoversRoute =>
		_mapMosaic is not { } mosaic || mosaic.Route.Contains(GeoBounds.Of(_allFrames.Select(f => (f.Raw.Latitude, f.Raw.Longitude))));

	private void ApplyFrames(IReadOnlyList<DerivedFrame> allFrames, double startAltitude, double observedMaxSpeedKmh)
	{
		_allFrames = allFrames;
		_startAltitude = startAltitude;
		_observedMaxSpeedKmh = observedMaxSpeedKmh;
		_totalDistanceMeters = allFrames.Count > 0 ? allFrames[^1].CumulativeDistanceMeters : 0;
		_totalDurationSeconds = allFrames.Count > 0 ? allFrames[^1].Raw.SampleTimeSeconds - allFrames[0].Raw.SampleTimeSeconds : 0;
		_tripStats = TripStats.Compute(allFrames);
		_trailSpeedScaleKmh = ComputeTrailSpeedScale(allFrames);
		_gMeterDeltas = GMeterDeltas.Compute(allFrames);
		ClearProfiles();
	}

	public int FrameBufferSize(int? outputWidth = null, int? outputHeight = null)
	{
		return (outputWidth ?? _width) * (outputHeight ?? _height) * 4;
	}

	/// <summary>
	///     Draws straight into `destination` (BGRA, at least FrameBufferSize bytes) instead of a fresh native
	///     bitmap copied out afterwards - at 4K that's two ~33 MB allocations per frame saved, which lets
	///     RenderJob recycle a handful of buffers for the whole render.
	///     Always drawn premultiplied: Skia only has fast raster paths for a premultiplied target - measured
	///     at 4K, blitting an image into an unpremultiplied bitmap took ~30 ms vs ~2.4 ms, and every other
	///     primitive was ~10x slower too. `premultiplied: false` (what ffmpeg's bgra input expects) converts
	///     afterwards, which is far cheaper since almost every overlay pixel is fully transparent or opaque.
	/// </summary>
	public void RenderInto(DerivedFrame frame, byte[] destination, int? outputWidth = null, int? outputHeight = null,
		bool premultiplied = false)
	{
		int outW = outputWidth ?? _width;
		int outH = outputHeight ?? _height;
		var info = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
		if (destination.Length < info.BytesSize)
			throw new ArgumentException($"Destination buffer is {destination.Length} bytes, {info.BytesSize} needed.", nameof(destination));

		UpdateTrail(frame);

		var pin = GCHandle.Alloc(destination, GCHandleType.Pinned);
		try
		{
			using var bitmap = new SKBitmap();
			bitmap.InstallPixels(info, pin.AddrOfPinnedObject(), info.RowBytes);
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(SKColors.Transparent);
			DrawFrame(canvas, frame, outW, outH);
		}
		finally
		{
			pin.Free();
		}

		if (!premultiplied) BgraAlpha.Unpremultiply(destination, info.BytesSize, outW);
	}

	/// <summary>
	///     Draws the overlay straight onto an opaque BGRA frame (width * 4 bytes a row) - the preview's video - rather
	///     than into a transparent buffer blended over it afterwards: the same pixels (source-over is associative),
	///     without clearing and blending a whole frame's worth of mostly transparent overlay.
	/// </summary>
	public void RenderOnto(DerivedFrame frame, byte[] bgra, int width, int height)
	{
		// Opaque pixels read the same premultiplied or not; Premul is what Skia has its fast paths for.
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		if (bgra.Length < info.BytesSize)
			throw new ArgumentException($"Frame buffer is {bgra.Length} bytes, {info.BytesSize} needed.", nameof(bgra));

		UpdateTrail(frame);

		var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
		try
		{
			using var bitmap = new SKBitmap();
			bitmap.InstallPixels(info, pin.AddrOfPinnedObject(), info.RowBytes);
			using var canvas = new SKCanvas(bitmap);
			DrawFrame(canvas, frame, width, height);
		}
		finally
		{
			pin.Free();
		}
	}

	private void DrawFrame(SKCanvas canvas, DerivedFrame frame, int outW, int outH)
	{
		canvas.Scale(outW / (float)_width, outH / (float)_height);

		double sampleTime = frame.Raw.SampleTimeSeconds;
		double introEnd = RouteIntro.DurationSeconds;
		bool isRouteIntroFrame = RouteIntro.Enabled && sampleTime < introEnd;
		// Non-null only inside the crossfade window right before introEnd: 0 at its start (intro still
		// fully opaque) to 1 at introEnd (intro fully gone, widgets fully opaque takes over exactly as
		// the plain isRouteIntroFrame branch below would from here on).
		double transitionStart = RouteIntroTransitionStartSeconds;
		float? crossfadeT = isRouteIntroFrame && sampleTime >= transitionStart
			? (float)((sampleTime - transitionStart) / (introEnd - transitionStart))
			: null;

		string? routeIntroMapAttribution()
		{
			return _routeIntroMosaic is not null ? MapSources.Attribution(RouteIntro.MapProviderId) : null;
		}

		string? mapAttribution;

		if (isRouteIntroFrame)
		{
			if (crossfadeT is { } t)
			{
				DrawRouteIntroCard(canvas, outW, outH, 1 - t);
				// The widgets fade in as one flattened group, not each on its own.
				int saveCount = canvas.SaveLayer(AlphaPaint(t));
				string? widgetsAttribution = DrawWidgets(canvas, frame);
				canvas.RestoreToCount(saveCount);
				// The card's own attribution is about to disappear along with it - once the widgets
				// underneath are visible at all, their attribution requirement (if any) is what matters
				// going forward.
				mapAttribution = widgetsAttribution ?? routeIntroMapAttribution();
			}
			else
			{
				DrawRouteIntroCard(canvas, outW, outH, 1f);
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
		SKTextAlign watermarkAlign = isRouteIntroFrame ? SKTextAlign.Right : SKTextAlign.Center;

		if (ShowWatermark)
		{
			DrawWatermark(canvas, sampleTime, watermarkAnchorX, watermarkAlign);
			if (mapAttribution is not null)
				DrawMapAttributionSlide(canvas, sampleTime, mapAttribution, watermarkAnchorX, watermarkAlign);
		}
		else if (mapAttribution is not null)
		{
			DrawMapAttributionOnly(canvas, mapAttribution, watermarkAnchorX, watermarkAlign);
		}
	}

	/// <summary>The normal (non-route-intro) per-frame widget pass. Returns the map attribution text to show, if any visible MapWidget needs one - see DrawFrame's mapAttribution.</summary>
	private string? DrawWidgets(SKCanvas canvas, DerivedFrame frame)
	{
		string? mapAttribution = null;
		double sampleTime = frame.Raw.SampleTimeSeconds;

		foreach (OverlayElement element in Layout)
		{
			if (!element.Visible) continue;

			ElementState state = ElementAnimation.At(element, sampleTime, OutputDurationSeconds);
			if (state.Progress <= 0f) continue;

			// A shadowed widget is recorded once and played back, so its fade and shadow layers cover just what it drew, not the
			// whole frame. The round ones draw directly: their size is known and they read the canvas matrix (DrawTrailRoute).
			bool shadowed = IsShadowed(element);
			SKRect? round = shadowed ? RoundWidgetBounds(element) : null;
			using SKPicture? recorded = shadowed && round is null ? RecordElement(element, frame) : null;
			SKRect? content = round ?? (recorded is { CullRect.IsEmpty: false } ? recorded.CullRect : null);
			if (shadowed && content is null) continue;

			int saveCount = BeginElement(canvas, element, state, content);
			if (recorded is null) DrawElement(canvas, element, frame);
			else canvas.DrawPicture(recorded);
			if (element is MapWidgetElement map) mapAttribution = MapSources.Attribution(map.MapProviderId) ?? mapAttribution;
			canvas.RestoreToCount(saveCount);
		}

		return mapAttribution;
	}

	/// <summary>One widget around its anchor at (0, 0), in reference pixels - the canvas already set up by BeginElement.</summary>
	private void DrawElement(SKCanvas canvas, OverlayElement element, DerivedFrame frame)
	{
		switch (element)
		{
			case DateTimeTextElement dateTime:
				DrawTimeText(canvas, frame, dateTime, true);
				break;
			case UtcTimeTextElement utcTime:
				DrawTimeText(canvas, frame, utcTime, false);
				break;
			case ElevationElement elevation:
				DrawElevation(canvas, frame, elevation);
				break;
			case GradientElement gradient:
				DrawGradient(canvas, frame, gradient);
				break;
			case DistanceElement distance:
				DrawDistance(canvas, frame, distance);
				break;
			case CompassElement compass:
				DrawCompass(canvas, frame, compass);
				break;
			case SunWidgetElement sun:
				DrawSunWidget(canvas, frame, sun);
				break;
			case RollGaugeElement roll:
				DrawRollGauge(canvas, roll, frame.RollDegrees);
				break;
			case PitchGaugeElement pitch:
				DrawPitchGauge(canvas, pitch, frame.PitchDegrees);
				break;
			case MapWidgetElement map:
				DrawMapWidget(canvas, frame, map);
				break;
			case SpeedGaugeElement speed:
				DrawSpeedGauge(canvas, speed, frame.SpeedKmh);
				break;
			case CameraInfoElement cameraInfo:
				DrawCameraInfo(canvas, frame, cameraInfo);
				break;
			case ElapsedTimeTextElement elapsed:
				DrawElapsedTime(canvas, frame, elapsed);
				break;
			case CameraModelTextElement cameraModel:
				DrawCameraModel(canvas, cameraModel);
				break;
			case GMeterElement gMeter:
				DrawGMeter(canvas, frame, gMeter);
				break;
			case TripProgressBarElement tripProgress:
				DrawTripProgressBar(canvas, frame, tripProgress);
				break;
			case ProfileChartElement profile:
				DrawProfileChart(canvas, frame, profile);
				break;
			case TripStatElement tripStat:
				DrawTripStat(canvas, frame, tripStat);
				break;
			case TextElement text:
				DrawText(canvas, text);
				break;
			case ImageElement image:
				DrawImage(canvas, image);
				break;
		}
	}

	/// <summary>
	///     What `element` draws at `frame`, around its anchor in reference pixels (before its X/Y and scale) - measured from
	///     the drawing itself, outline included, the widget's drop shadow not: recorded with an R-tree, Skia trims the
	///     picture's CullRect to what was drawn - but pads strokes, so the picture is then rasterized and cut down to its
	///     visible pixels. Hit-testing and the selection box frame the widget as it really is, text that grows included.
	///     Null when it draws nothing (an Image without a file).
	/// </summary>
	public SKRect? MeasureElement(OverlayElement element, DerivedFrame frame)
	{
		if (RoundWidgetBounds(element) is { } round) return round;

		using SKPicture picture = RecordElement(element, frame);
		SKRect bounds = picture.CullRect;
		if (bounds.IsEmpty) return null;

		int width = (int)Math.Ceiling(bounds.Width), height = (int)Math.Ceiling(bounds.Height);
		if ((long)width * height > MeasureMaxPixels) return bounds;

		using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
		using (var raster = new SKCanvas(bitmap))
		{
			raster.Translate(-bounds.Left, -bounds.Top);
			raster.DrawPicture(picture);
		}

		if (VisibleBounds(bitmap) is not { } visible) return null;

		visible.Offset(bounds.Left, bounds.Top);
		return visible;
	}

	/// <summary>
	///     A round widget is always its disc plus the ring's half stroke. Drawing it to measure it would rasterize the whole map
	///     (and advance the dynamic zoom's smoothing) on every pointer move.
	/// </summary>
	private static SKRect? RoundWidgetBounds(OverlayElement element)
	{
		const float ringHalfStroke = OverlayElementBounds.RingHalfStroke;
		float radius = element switch
		{
			MapWidgetElement => OverlayElementBounds.MapRadius,
			CompassElement => OverlayElementBounds.CompassRadius,
			_ => 0f
		};
		return radius == 0f ? null : SKRect.Create(-radius - ringHalfStroke, -radius - ringHalfStroke, (radius + ringHalfStroke) * 2, (radius + ringHalfStroke) * 2);
	}

	/// <summary>`element` drawn into a picture around its anchor, with an R-tree that trims CullRect to what was drawn.</summary>
	private SKPicture RecordElement(OverlayElement element, DerivedFrame frame)
	{
		using var recorder = new SKPictureRecorder();
		DrawElement(recorder.BeginRecording(MeasureArea, true), element, frame);
		return recorder.EndRecording();
	}

	/// <summary>The box of the pixels in an Alpha8 bitmap that are visible (at least MeasureMinAlpha), or null when there are none.</summary>
	private static SKRect? VisibleBounds(SKBitmap bitmap)
	{
		ReadOnlySpan<byte> alpha = bitmap.GetPixelSpan();
		int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
		for (int y = 0; y < bitmap.Height; y++)
		{
			ReadOnlySpan<byte> row = alpha.Slice(y * bitmap.RowBytes, bitmap.Width);
			for (int x = 0; x < row.Length; x++)
			{
				if (row[x] < MeasureMinAlpha) continue;
				minX = Math.Min(minX, x);
				maxX = Math.Max(maxX, x);
				minY = Math.Min(minY, y);
				maxY = y;
			}
		}

		return maxX < 0 ? null : new SKRect(minX, minY, maxX + 1, maxY + 1);
	}

	/// <summary>
	///     The shared paint for drawing something at an opacity (a SaveLayer group, the route-intro card's image) -
	///     Skia copies a paint's state when it's used, so the one instance serves every call in turn.
	/// </summary>
	private SKPaint AlphaPaint(float alpha)
	{
		_alphaPaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(alpha * 255f, 0, 255));
		return _alphaPaint;
	}

	/// <summary>
	///     `outlineColor`/`outlineWidthScale` default to the built-in near-black outline at its normal
	///     width - only the text widgets' per-element OutlineColor/OutlineWidth override them (see
	///     OverlayRenderer.TextWidgets.cs); every other caller gets the defaults.
	/// </summary>
	private void DrawOutlined(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color,
		SKTextAlign align = SKTextAlign.Left, float opacity = 1f, SKColor? outlineColor = null, float outlineWidthScale = 1f)
	{
		float dropOffset = font.Size * 0.045f;
		_outlineShadowPaint.Color = new SKColor(0, 0, 0, (byte)(130 * opacity));
		_outlineShadowPaint.MaskFilter = GetBlurMaskFilter(font.Size * 0.04f);
		canvas.DrawText(text, x + dropOffset, y + dropOffset, align, font, _outlineShadowPaint);

		SKColor stroke = outlineColor ?? Shadow;
		_outlineStrokePaint.Color = stroke.WithAlpha((byte)(stroke.Alpha * opacity));
		_outlineStrokePaint.StrokeWidth = font.Size * 0.045f * outlineWidthScale;
		canvas.DrawText(text, x, y, align, font, _outlineStrokePaint);

		_outlineFillPaint.Color = color.WithAlpha((byte)(color.Alpha * opacity));
		canvas.DrawText(text, x, y, align, font, _outlineFillPaint);
	}

	/// <summary>The fill of `element`'s panel: its PanelColor at its PanelOpacity. One shared paint, recolored per widget like the text paints.</summary>
	private SKPaint PanelFillPaint(IPanelElement element)
	{
		return PanelFillPaint(element.PanelColor, element.PanelOpacity);
	}

	private SKPaint PanelFillPaint(string? colorHex, float opacity)
	{
		SKColor color = ResolveColor(colorHex, SKColors.Black);
		_panelFillPaint.Color = color.WithAlpha((byte)Math.Round(color.Alpha * Math.Clamp(opacity, 0f, 1f)));
		return _panelFillPaint;
	}

	/// <summary>Hex string (e.g. "#FFFFFF"), or `fallback` when null/unparsable - fails soft, same policy as TrailColor/DateFormat/Locale. Shared by every per-element color override (TrailColor, and TextColor/AccentColor/OutlineColor below).</summary>
	private static SKColor ResolveColor(string? hex, SKColor fallback)
	{
		return !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out SKColor parsed) ? parsed : fallback;
	}

	/// <summary>
	///     A font at `size` in `family` (or the built-in HUD typeface when `family` is null/not installed
	///     on this machine - see OverlayElementBounds.ResolveTypefaceOrFallback), cached per (family, size)
	///     pair for this renderer's lifetime - only the text widgets ever request a non-null family, so in
	///     the overwhelmingly common case this is a single cache lookup, not a fresh SKFont per frame.
	/// </summary>
	private SKFont GetFont(string? family, float size)
	{
		(string, float) key = (family ?? "", size);
		if (_fontCache.TryGetValue(key, out SKFont? font)) return font;

		font = new SKFont(ResolveTypeface(family), size);
		_fontCache[key] = font;
		return font;
	}

	private SKTypeface ResolveTypeface(string? family)
	{
		if (string.IsNullOrWhiteSpace(family)) return _hudTypeface;
		if (_customTypefacesByFamily.TryGetValue(family, out SKTypeface? cached)) return cached;

		SKTypeface resolved = OverlayElementBounds.ResolveTypefaceOrFallback(family, _hudTypeface);
		_customTypefacesByFamily[family] = resolved;
		return resolved;
	}

	/// <summary>Lazily builds (and thereafter reuses) the blur mask filter for a given sigma - see the fields above for why this is safe to share across calls.</summary>
	private SKMaskFilter GetBlurMaskFilter(float sigma)
	{
		if (_blurMaskFilters.TryGetValue(sigma, out SKMaskFilter? filter)) return filter;

		filter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
		_blurMaskFilters[sigma] = filter;
		return filter;
	}

	/// <summary>Invariant, and never "-0" - .NET keeps the sign of a value that rounds to zero, so a level gauge or flat gradient would flicker "-0"/"0".</summary>
	internal static string F(double value, string format)
	{
		string text = value.ToString(format, CultureInfo.InvariantCulture);
		return text.StartsWith('-') && text.AsSpan(1).IndexOfAnyExcept('0', '.') < 0 ? text[1..] : text;
	}

	/// <summary>
	///     A single EMA-smoothed value driven by SampleTimeSeconds instead of wall-clock time, used by
	///     GetMapZoomFactor (map crop), which needs this shape: smooth toward a per-frame target normally, but snap straight to it when time
	///     goes backwards or jumps forward by more than resetGapSeconds, since that means a scrub/seek
	///     landed on a new position, not a continuous run of frames to smooth across. The same time again (a paused
	///     frame drawn anew) keeps the value, so it doesn't jump to the target.
	/// </summary>
	private sealed class ResettableEma
	{
		private double? _lastSeconds;

		public double Value { get; private set; }

		public double Update(double seconds, double target, double timeConstantSeconds, double resetGapSeconds = 2.0)
		{
			if (_lastSeconds is { } same && seconds == same) return Value;

		if (_lastSeconds is not { } last || seconds < last || seconds - last > resetGapSeconds)
			{
				Value = target;
			}
			else
			{
				double alpha = 1.0 - Math.Exp(-(seconds - last) / timeConstantSeconds);
				Value += (target - Value) * alpha;
			}

			_lastSeconds = seconds;
			return Value;
		}
	}
}
