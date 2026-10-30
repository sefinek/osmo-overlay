using OsmoOverlay.Core.Localization;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Layout constants shared between OverlayRenderer (drawing) and the GUI editor (hit-testing for
///     drag-to-reposition), so the two can't drift apart. All sizes are expressed at ReferenceWidth x ReferenceHeight (4K);
///     GetScale gives the factor both sides must apply so the layout looks the same (proportionally)
///     at any actual video resolution instead of overlapping on smaller frames.
///     The font sizes/typeface below are the single source OverlayRenderer builds its fonts from and
///     GetBounds measures against - so a hit box can't silently drift out of sync with what actually
///     gets drawn the way two independently maintained numbers could.
/// </summary>
public static class OverlayElementBounds
{
	public const float ReferenceWidth = 3840f;
	public const float ReferenceHeight = 2160f;

	public const float Margin = 70f;
	public const float CompassRadius = 260f;
	public const float SunRadius = 90f;
	public const float SpeedRadius = 260f;
	public const float TiltRadius = 95f;
	public const float MapRadius = 260f;
	// Half the ring stroked round Map and Compass, which reaches past their radius.
	public const float RingHalfStroke = 1.5f;
	public const float GMeterRadius = 110f;
	public const float LabelBelowRadiusOffset = 56f;
	public const float LabelBelowRadiusPadding = LabelBelowRadiusOffset + 14f;
	// ElapsedTimeText's optional caption: its baseline this far above the time's.
	public const float CaptionAboveOffset = 56f;

	public const float DateFontSize = 46f;
	public const float LabelFontSize = 36f;
	public const float ValueFontSize = 88f;
	public const float UnitFontSize = 42f;
	public const float SmallFontSize = 36f;
	public const float WatermarkTitleFontSize = 40f;
	public const float WatermarkSubtitleFontSize = 30f;
	// From the subtitle's baseline (the watermark's Y) up to the title's.
	public const float WatermarkLineGap = 46f;
	public static string WatermarkTitle => CoreStrings.Overlay_Watermark;
	private const string SampleWatermarkSubtitle = "github.com/sefinek/osmo-overlay  •  v0.0.0";
	// The map credit: a source note the terms ask to be readable, not a part of the HUD - smaller and fainter than its text.
	public const float CreditFontSize = 24f;
	public const float SpeedFontSize = 115f;
	public const float SpeedUnitFontSize = 38f;

	// Per-widget OverlayElement.Scale range - enforced by the GUI's resize handle/scale box and when a
	// preset file is loaded or imported, so a hand-edited value can't render a widget inverted or invisible.
	public const float MinElementScale = 0.4f;
	public const float MaxElementScale = 2.5f;

	// The map credit has to stay readable.
	public const float MinMapCreditScale = 0.75f;

	public static float MinScale(OverlayElement element)
	{
		return element is MapAttributionElement ? MinMapCreditScale : MinElementScale;
	}

	public static float ClampScale(OverlayElement element, float scale)
	{
		return float.IsFinite(scale) ? Math.Clamp(scale, MinScale(element), MaxElementScale) : 1f;
	}

	public const float ProgressBarWidth = 760f;
	public const float ProgressBarHeight = 60f;

	// ProfileChart: label and current value on a line at y=0, the chart panel below it.
	public const float ChartWidth = 760f;
	public const float ChartTop = 24f;
	public const float ChartHeight = 220f;

	// Image: larger sides are refused rather than decoded (a 16K x 16K PNG is a 1 GB bitmap). An image not picked
	// yet gets a box of ImagePlaceholder size, so there's something to grab and open the settings from.
	public const int MaxImageDimension = 8192;
	private static readonly SKSize ImagePlaceholder = new(480, 270);

	// DrawStat (Elevation/Gradient/Distance) and DrawCameraInfo's own fixed layout - the y-offsets each
	// line's baseline sits at, in the same reference-pixel space GetBounds measures in. Kept here (not
	// re-derived) since they mirror exact literals in OverlayRenderer.TextWidgets.cs.
	internal const float StatValueBaselineY = 90f;
	internal const float StatUnitGapX = 12f;
	private static readonly float[] CameraInfoLineBaselineYs = [58f, 102f, 146f];

	// Representative sample content GetBounds measures for widgets whose real text isn't known at
	// call time (or is passed in as null) - chosen to be at least as wide/tall as what that widget
	// realistically renders, so the computed hit box is a safe (if occasionally slightly generous)
	// approximation rather than one that can clip the real text.
	private static readonly DateTime SampleDateTime = new(2026, 12, 31, 23, 59, 59);
	private const double SampleElapsedSeconds = 3661; // -> "01:01:01", the widest ElapsedTimeText ever shows (once past an hour)
	private const string SampleCameraModel = "DJI Osmo Action 6";
	// The map credit widget's stand-in while no Map widget gives it a text of its own.
	public const string SampleMapCredit = "Map data © OpenStreetMap contributors";
	private const string SampleElevationValue = "-9999";
	private const string SampleGradientValue = "-99";
	private const string SampleDistanceValue = "9999.99";
	private const string SampleIsoText = "ISO 12800";
	private const string SampleShutterText = "1/8000 S";
	private const string SampleColorTempText = "9900 K";
	private const string SampleTripStatValue = "9999";
	private const string SampleChartValue = "9999 KM/H";

	// Lives for the process lifetime rather than being disposed - this is a static utility class with
	// no owner to call Dispose, the same "cheap, kept forever" pattern already used for the GUI's own
	// static cursors (see MainWindow.OverlayEditor.cs). Deliberately a *separate* SKTypeface instance
	// from the one OverlayRenderer builds per render job (which it does own and dispose) - both resolve
	// the same font family/style deterministically, so the metrics measured here can't drift from what
	// gets drawn even though the two never share a handle.
	private static readonly SKTypeface HudTypeface = CreateHudTypeface();

	/// <summary>The family the widgets draw in without one of their own (CreateHudTypeface's pick on this system).</summary>
	public static string HudFontFamily => HudTypeface.FamilyName;
	private static readonly Dictionary<string, SKTypeface> TypefacesByFamily = [];
	private static readonly Dictionary<(string Family, float Size), SKFont> FontsBySize = [];
	// Read by the GUI's hit-testing and by OverlayRenderer on the preview's compose thread - hence the lock. One entry per
	// file ever picked, so it's simply emptied past MaxCachedImageSizes.
	private const int MaxCachedImageSizes = 64;
	private static readonly Dictionary<string, (DateTime Modified, SKSizeI? Size)> ImageSizes = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Lock ImageSizesLock = new();

	public static float GetScale(int width, int height)
	{
		return Math.Min(width / ReferenceWidth, height / ReferenceHeight);
	}

	/// <summary>
	///     A widget's X/Y live in the ReferenceWidth x ReferenceHeight space, so a preset fits any resolution and aspect
	///     ratio: each axis scales on its own (a widget at the right margin stays there on a 4:3 frame), while sizes use
	///     the single GetScale factor. This is the position in the video's own pixels.
	/// </summary>
	public static (float X, float Y) ToPixels(float x, float y, int width, int height)
	{
		return (x * width / ReferenceWidth, y * height / ReferenceHeight);
	}

	/// <summary>The inverse of ToPixels: a position in video pixels as a widget's X/Y.</summary>
	public static (float X, float Y) ToReference(float x, float y, int width, int height)
	{
		return (x * ReferenceWidth / width, y * ReferenceHeight / height);
	}

	/// <summary>
	///     The exact bold system-font fallback chain OverlayRenderer draws every HUD widget with - here
	///     (rather than duplicated) so GetBounds measures with the identical typeface.
	/// </summary>
	public static SKTypeface CreateHudTypeface()
	{
		var style = new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
		string[] candidates = OperatingSystem.IsWindows()
			? ["Segoe UI"]
			: OperatingSystem.IsMacOS()
				? ["Helvetica Neue", "Arial"]
				: ["Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"];

		foreach (string family in candidates)
		{
			SKTypeface? typeface = SKFontManager.Default.MatchFamily(family, style);
			if (typeface is not null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
				return typeface;
			typeface?.Dispose();
		}

		return SKTypeface.FromFamilyName(null, style);
	}

	/// <summary>
	///     Resolves `family` to a bold SKTypeface via SKFontManager, or `fallback` when the family isn't
	///     installed on this machine (a preset authored elsewhere might name a font this one doesn't have) -
	///     same fail-soft policy as DateFormat/Locale/TrailColor. Returns a freshly matched typeface the
	///     caller now owns (and must dispose) when resolution succeeds and differs from `fallback`; returns
	///     `fallback` itself (not a new instance) otherwise, so a caller must compare by reference before
	///     deciding whether to dispose what it gets back - see OverlayRenderer.ResolveTypeface/Dispose.
	/// </summary>
	public static SKTypeface ResolveTypefaceOrFallback(string? family, SKTypeface fallback)
	{
		if (string.IsNullOrWhiteSpace(family)) return fallback;

		var style = new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
		SKTypeface? matched = SKFontManager.Default.MatchFamily(family, style);
		if (matched is not null && matched.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
			return matched;

		matched?.Dispose();
		return fallback;
	}

	/// <summary>
	///     A widget's hit-test/selection-box bounds in full-res pixel space. `dateFormat`/`locale`/`label`/
	///     `cameraModel`/`fontFamily` let a caller that already has the real OverlayElement (and, for
	///     CameraModelText, the file's actual camera model) measure the box against what will really be
	///     drawn instead of a generic placeholder - all optional since a caller mid-drag (SnapToGuides) may
	///     only have the type. `text` is a Text widget's own text, `imagePath` an Image widget's file.
	/// </summary>
	public static SKRect GetBounds(OverlayElementType type, float x, float y, float scale,
		string? dateFormat = null, string? locale = null, string? label = null, string? cameraModel = null,
		string? fontFamily = null, string? text = null, string? imagePath = null, GpsAlertTexts? gpsAlertTexts = null, OverlayElement? element = null)
	{
		SKRect local = type switch
		{
			// DrawTimeText/DrawElapsedTime/DrawCameraModel render with the baseline at (0, 0) - MeasureLine
			// uses the font's own ascent/descent (not one sample string's ink extent) for the vertical
			// span, so a short sample still yields a box tall enough for real text with descenders/accents.
			OverlayElementType.DateTimeText => MeasureLine(SampleDateTimeText(dateFormat, locale), DateFontSize, fontFamily),
			OverlayElementType.UtcTimeText => MeasureLine(SampleDateTimeText(dateFormat, locale) + "  UTC", DateFontSize, fontFamily),
			OverlayElementType.ElapsedTimeText => string.IsNullOrWhiteSpace(label)
				? MeasureLine(OverlayTimeFormatting.FormatElapsed(SampleElapsedSeconds), DateFontSize, fontFamily)
				: SKRect.Union(MeasureLine(OverlayTimeFormatting.FormatElapsed(SampleElapsedSeconds), DateFontSize, fontFamily),
					OffsetY(MeasureLine(label.ToUpperInvariant(), LabelFontSize, fontFamily), -CaptionAboveOffset)),
			OverlayElementType.CameraModelText => MeasureLine(cameraModel ?? SampleCameraModel, DateFontSize, fontFamily),
			OverlayElementType.MapAttribution => MeasureCredit(text ?? SampleMapCredit, fontFamily),
			OverlayElementType.GpsAlert => GpsAlertBox(fontFamily, gpsAlertTexts),
			OverlayElementType.Watermark => SKRect.Union(
				OffsetY(Centered(MeasureLine(WatermarkTitle, WatermarkTitleFontSize, fontFamily)), -WatermarkLineGap),
				Centered(MeasureLine(text ?? SampleWatermarkSubtitle, WatermarkSubtitleFontSize, fontFamily))),
			OverlayElementType.Elevation => MeasureStat(label ?? CoreStrings.Overlay_Elevation, SampleElevationValue, "M", fontFamily),
			OverlayElementType.Gradient => MeasureStat(label ?? CoreStrings.Overlay_Gradient, SampleGradientValue, "%", fontFamily),
			OverlayElementType.Distance => MeasureStat(label ?? CoreStrings.Overlay_TotalDistance, SampleDistanceValue, "KM", fontFamily),
			OverlayElementType.AmbientTemperature => MeasureStat(label ?? CoreStrings.Overlay_AmbientTemperature, "23.4", "°C", fontFamily),
			OverlayElementType.ImuTemperature => MeasureStat(label ?? CoreStrings.Overlay_ImuTemperature, "29.8", "°C", fontFamily),
			OverlayElementType.DeviceDistance => MeasureStat(label ?? CoreStrings.Overlay_DeviceDistance, SampleDistanceValue, "KM", fontFamily),
			OverlayElementType.Height => MeasureStat(label ?? CoreStrings.Overlay_Height, SampleElevationValue, "M", fontFamily),
			OverlayElementType.HomeDistance => MeasureStat(label ?? CoreStrings.Overlay_HomeDistance, SampleDistanceValue, "KM", fontFamily),
			OverlayElementType.VerticalSpeed => MeasureStat(label ?? CoreStrings.Overlay_VerticalSpeed, "-99.9", "M/S", fontFamily),
			OverlayElementType.Satellites => MeasureSatellites(label ?? CoreStrings.Overlay_Satellites, (element as SatellitesElement)?.ShowBars ?? true, fontFamily),
			OverlayElementType.DroneBattery => MeasureDroneBattery(element as DroneBatteryElement ?? new DroneBatteryElement { X = 0, Y = 0, FontFamily = fontFamily, Label = label }),
			OverlayElementType.FlightMode => SKRect.Union(MeasureLine((label ?? CoreStrings.Overlay_FlightModeLabel).ToUpperInvariant(), LabelFontSize, fontFamily),
				OffsetY(MeasureLine("Confirm Landing", FlightModeSize, fontFamily), FlightModeBaseline)),
			OverlayElementType.Signal => MeasureSignal(element as SignalElement ?? new SignalElement { X = 0, Y = 0, FontFamily = fontFamily, Label = label }),
			OverlayElementType.FlightAlerts => FlightAlertsBox(fontFamily, (element as FlightAlertsElement)?.MaxLines ?? 1),
			OverlayElementType.DeviceBattery => MeasureStat(label ?? CoreStrings.Overlay_DeviceBattery, "100", "%", fontFamily),
			OverlayElementType.AudioLevel => SKRect.Union(new SKRect(0, 20, OverlayRenderer.AudioMeterWidth, 56),
				SKRect.Union(MeasureLine((label ?? CoreStrings.Overlay_AudioLevel).ToUpperInvariant(), LabelFontSize, fontFamily),
					OffsetY(MeasureLine("RMS -128   PEAK -128 dBFS", SmallFontSize, fontFamily), 106))),
			OverlayElementType.WindProcessing => SKRect.Union(MeasureStat(label ?? CoreStrings.Overlay_WindProcessing, "100", "/100", fontFamily),
				OffsetY(MeasureLine(CoreStrings.Overlay_Inactive.ToUpperInvariant(), SmallFontSize, fontFamily), 150)),
			OverlayElementType.ImageLuma => MeasureStat(label ?? CoreStrings.Overlay_ImageLuma, "255", "/255", fontFamily),
			OverlayElementType.CameraInfo => MeasureCameraInfo(label ?? CoreStrings.Overlay_Camera, fontFamily),
			// The widest built-in caption, since the one shown depends on the stat picked.
			OverlayElementType.TripStat => MeasureStat(label ?? WidestTripStatLabel(fontFamily), SampleTripStatValue, "KM/H", fontFamily),
			OverlayElementType.Text => MeasureText(text ?? OverlayRenderer.TextDefault, fontFamily),
			OverlayElementType.ProfileChart => MeasureChart(label ?? OverlayRenderer.DefaultProfileLabel(ProfileSeries.Elevation), fontFamily),
			OverlayElementType.Image => ImageSize(imagePath) is { } size
				? new SKRect(0, 0, size.Width, size.Height)
				: new SKRect(0, 0, ImagePlaceholder.Width, ImagePlaceholder.Height),
			// TripProgressBar's anchor is the bar's vertical/horizontal center (DrawTripProgressBar
			// translates to (x, y) and draws the track symmetrically around it), unlike the text panels
			// above whose anchor is a corner - so the hit box is centered on (x, y) too.
			OverlayElementType.TripProgressBar =>
				new SKRect(-ProgressBarWidth / 2, -ProgressBarHeight / 2, ProgressBarWidth / 2, ProgressBarHeight / 2),
			OverlayElementType.Compass => CircleLocal(CompassRadius),
			OverlayElementType.SunWidget => CircleWithBottomLabelLocal(SunRadius),
			OverlayElementType.RollGauge or OverlayElementType.PitchGauge => CircleLocal(TiltRadius),
			OverlayElementType.SpeedGauge => CircleLocal(SpeedRadius),
			OverlayElementType.MapWidget => CircleLocal(MapRadius),
			OverlayElementType.GMeter => CircleWithBottomLabelLocal(GMeterRadius),
			OverlayElementType.RcSticks => new SKRect(0, 0, StickPadSize * 2 + StickPadGap, StickPadSize),
			OverlayElementType.AttitudeIndicator or OverlayElementType.Orientation3D => CircleLocal(DroneDialRadius),
			_ => SKRect.Empty
		};

		if (local.IsEmpty) return local;
		return new SKRect(x + local.Left * scale, y + local.Top * scale, x + local.Right * scale, y + local.Bottom * scale);
	}

	private static SKRect CircleLocal(float radius)
	{
		return new SKRect(-radius, -radius, radius, radius);
	}

	private static SKRect CircleWithBottomLabelLocal(float radius)
	{
		return new SKRect(-radius, -radius, radius, radius + LabelBelowRadiusPadding);
	}

	private static string SampleDateTimeText(string? format, string? locale)
	{
		OverlayTimeFormatting.TryFormat(SampleDateTime, format, locale, out string text);
		return text;
	}

	private static SKTypeface ResolveTypeface(string? family)
	{
		if (string.IsNullOrWhiteSpace(family)) return HudTypeface;
		if (TypefacesByFamily.TryGetValue(family, out SKTypeface? cached)) return cached;

		SKTypeface resolved = ResolveTypefaceOrFallback(family, HudTypeface);
		TypefacesByFamily[family] = resolved;
		return resolved;
	}

	private static SKFont GetFont(string? family, float size)
	{
		(string, float) key = (family ?? "", size);
		if (FontsBySize.TryGetValue(key, out SKFont? font)) return font;

		font = new SKFont(ResolveTypeface(family), size);
		FontsBySize[key] = font;
		return font;
	}

	/// <summary>The built-in TripStat caption that takes the most room in this font - the languages differ in which one it is.</summary>
	private static string WidestTripStatLabel(string? family)
	{
		return Enum.GetValues<TripStatKind>().Select(OverlayRenderer.DefaultTripStatLabel)
			.MaxBy(l => LineExtent(l.ToUpperInvariant(), LabelFontSize, family).Width)!;
	}

	private static (float Ascent, float Descent, float Width) LineExtent(string text, float fontSize, string? family)
	{
		SKFont font = GetFont(family, fontSize);
		font.GetFontMetrics(out SKFontMetrics metrics);
		return (metrics.Ascent, metrics.Descent, font.MeasureText(text));
	}

	/// <summary>Mirrors OverlayRenderer.DrawCredit: the credit's lines going up from the baseline at y=0, the last one on it.</summary>
	private static SKRect MeasureCredit(string text, string? family)
	{
		SKFont font = GetFont(family, CreditFontSize);
		string[] lines = text.Split('\n');
		SKRect bounds = MeasureLine(lines[^1], CreditFontSize, family);
		for (int i = 0; i < lines.Length - 1; i++)
			bounds = SKRect.Union(bounds, OffsetY(MeasureLine(lines[i], CreditFontSize, family), -(lines.Length - 1 - i) * font.Spacing));

		return bounds;
	}

	private static SKRect OffsetY(SKRect rect, float dy)
	{
		return new SKRect(rect.Left, rect.Top + dy, rect.Right, rect.Bottom + dy);
	}

	/// <summary>A line measured from its start, moved to sit centered on the anchor - how the watermark draws it.</summary>
	private static SKRect Centered(SKRect line)
	{
		line.Offset(-(line.Left + line.Right) / 2, 0);
		return line;
	}

	// The GPS alert's panel: its height, the icon's size and the gaps, its two lines' sizes and baselines. Its width is fixed
	// per font - wide enough for every text it can show, so it doesn't change width as the notice changes.
	public const float GpsAlertHeight = 108;
	public const float GpsAlertIcon = 64;
	public const float GpsAlertGap = 22;
	public const float GpsAlertTitleSize = 36;
	public const float GpsAlertDetailSize = 27;
	public const float GpsAlertTitleBaseline = 48;
	public const float GpsAlertDetailBaseline = 87;

	/// <summary>The GPS alert's panel around its anchor (top center), whatever it says.</summary>
	public static SKRect GpsAlertBox(string? fontFamily, GpsAlertTexts? texts)
	{
		(string[] titles, string[] details) = OverlayRenderer.GpsAlertLines(texts ?? new GpsAlertTexts());
		return GpsAlertBox(fontFamily, titles, details);
	}

	/// <summary>The panel for these lines (OverlayRenderer.GpsAlertLines, by GpsAlertMessage).</summary>
	internal static SKRect GpsAlertBox(string? fontFamily, string[] titles, string[] details)
	{
		float text = 0;
		foreach (string title in titles)
			text = Math.Max(text, LineExtent(title, GpsAlertTitleSize, fontFamily).Width);
		for (int i = 0; i < details.Length; i++)
			text = Math.Max(text, LineExtent(OverlayRenderer.GpsDetailAtWidest(details[i], (GpsAlertMessage)i), GpsAlertDetailSize, fontFamily).Width);

		float width = GpsAlertGap * 2 + GpsAlertIcon + GpsAlertGap + text;
		return new SKRect(-width / 2, 0, width / 2, GpsAlertHeight);
	}

	/// <summary>
	///     A single line of DrawOutlined text with its baseline at local (0, 0): horizontal extent from
	///     measuring the actual text, vertical extent from the font's ascent/descent (see GetBounds' doc).
	///     Padded a little on every edge for DrawOutlined's drop-shadow offset + stroke width, which both
	///     draw slightly outside the glyph outline itself.
	/// </summary>
	private static SKRect MeasureLine(string text, float fontSize, string? family)
	{
		(float ascent, float descent, float width) = LineExtent(text, fontSize, family);
		float pad = fontSize * 0.09f;
		return new SKRect(-pad, ascent - pad, width + pad, descent + pad);
	}

	// The remote's sticks and the two dials of the aircraft's attitude (OverlayRenderer.DroneControls.cs).
	public const float StickPadSize = 130;
	public const float StickPadGap = 24;
	public const float DroneDialRadius = 110;

	public const float FlightModeSize = 56;
	public const float FlightModeBaseline = 66;

	// The signal bars (GPS level, radio links) and the signal widget's two lines (OverlayRenderer.Signal.cs).
	public const float SignalBarsGap = 26;
	public const float SignalBarWidth = 13;
	public const float SignalBarStep = 20;
	public const int SignalBarCount = 5;
	public const float SignalBarMinHeight = 16;
	public const float SignalBarHeightStep = 11;
	public const float SignalLineSize = 40;
	public const float SignalLineStep = 44;
	public const float SignalTextX = 36;

	/// <summary>
	///     The baseline of link `index` of `lines`: the last one sits on the stat widgets' value baseline, so the signal's lines and bars end where the
	///     GPS count and its bars do and the two read as one row.
	/// </summary>
	public static float SignalBaseline(int lines, int index) => StatValueBaselineY - (lines - 1 - index) * SignalLineStep;

	public static float SignalBarsWidth => SignalBarCount * SignalBarStep - (SignalBarStep - SignalBarWidth);

	/// <summary>The GPS widget: the stat plus the bars after the value.</summary>
	private static SKRect MeasureSatellites(string label, bool bars, string? family)
	{
		SKRect stat = MeasureStat(label, "99", "", family);
		if (!bars) return stat;

		(float valueAscent, _, float valueWidth) = LineExtent("99", ValueFontSize, family);
		float barsRight = valueWidth + SignalBarsGap + SignalBarsWidth;
		return new SKRect(stat.Left, Math.Min(stat.Top, StatValueBaselineY - SignalBarMinHeight - 4 * SignalBarHeightStep), Math.Max(stat.Right, barsRight), stat.Bottom);
	}

	/// <summary>Mirrors DrawSignal: the caption, a line per link and the bars after the widest line.</summary>
	private static SKRect MeasureSignal(SignalElement element)
	{
		string? family = element.FontFamily;
		(float labelAscent, _, float labelWidth) = LineExtent((element.Label ?? CoreStrings.Overlay_Signal).ToUpperInvariant(), LabelFontSize, family);
		(float lineAscent, float lineDescent, float lineWidth) = LineExtent("100%", SignalLineSize, family);
		int lines = (element.ShowUplink ? 1 : 0) + (element.ShowDownlink ? 1 : 0);
		float pad = SignalLineSize * 0.09f;
		float right = Math.Max(labelWidth, SignalTextX + lineWidth + (element.ShowBars ? SignalBarsGap + SignalBarsWidth : 0));
		float bottom = lines == 0 ? 0 : StatValueBaselineY + lineDescent;
		return new SKRect(-pad, labelAscent - pad, right + pad, Math.Max(bottom, 0) + pad);
	}

	// The drone battery widget: the icon beside the percent, the extra lines under it (OverlayRenderer.DroneBattery.cs).
	public const float DroneBatteryIconWidth = 50;
	/// <summary>The charge's figure, a little under a stat's value so the percent doesn't outweigh the icon.</summary>
	public const float DroneBatteryValueFontSize = ValueFontSize * 0.88f;
	public const float DroneBatteryIconGap = 22;
	public const float DroneBatteryIconTop = 18;
	public const float DroneBatteryIconBottom = 134;
	public const float DroneBatteryCapWidth = 20;
	public const float DroneBatteryCapHeight = 8;
	// The percent and the first line under it sit as a pair about the icon's middle (the icon spans IconTop to IconBottom), not on the stat widgets' value baseline.
	public const float DroneBatteryValueBaseline = 81;
	public const float DroneBatteryLineBaseline = 125;
	public const float DroneBatteryLineStep = 44;

	/// <summary>The flight alerts' stack - the most lines the widget can be set to show at once.</summary>
	public const int FlightAlertsMaxLines = 5;

	/// <summary>Mirrors DrawDroneBattery: the caption, then the icon and the percent, then the lines the element is set to show.</summary>
	private static SKRect MeasureDroneBattery(DroneBatteryElement element)
	{
		string? family = element.FontFamily;
		(float labelAscent, float labelDescent, float labelWidth) = LineExtent((element.Label ?? CoreStrings.Overlay_DroneBattery).ToUpperInvariant(), LabelFontSize, family);
		(float valueAscent, float valueDescent, float valueWidth) = LineExtent("100", DroneBatteryValueFontSize, family);
		(_, _, float unitWidth) = LineExtent("%", UnitFontSize, family);
		string[] lines = OverlayRenderer.DroneBatteryLines(element, OverlayRenderer.SampleDroneBattery);
		(_, float lineDescent, _) = LineExtent("0", SmallFontSize, family);
		float left = element.ShowIcon ? DroneBatteryIconWidth + DroneBatteryIconGap : 0;
		float pad = ValueFontSize * 0.09f;

		float right = Math.Max(labelWidth, left + Math.Max(valueWidth + StatUnitGapX + unitWidth, lines.Length == 0 ? 0 : lines.Max(l => LineExtent(l, SmallFontSize, family).Width)));
		float top = Math.Min(labelAscent, DroneBatteryValueBaseline + valueAscent);
		float bottom = Math.Max(labelDescent, DroneBatteryValueBaseline + valueDescent);
		if (lines.Length > 0) bottom = Math.Max(bottom, DroneBatteryLineBaseline + (lines.Length - 1) * DroneBatteryLineStep + lineDescent);
		if (element.ShowIcon)
		{
			top = Math.Min(top, DroneBatteryIconTop);
			bottom = Math.Max(bottom, DroneBatteryIconBottom);
		}

		return new SKRect(-pad, top - pad, right + pad, bottom + pad);
	}

	/// <summary>A flight alert's panel for text `text` wide: roomier than the text, and never narrower than a typical message, so a stack of them lines up.</summary>
	public static float FlightAlertWidth(float text)
	{
		return Math.Max(FlightAlertMinWidth, GpsAlertGap * 3 + GpsAlertIcon + text + FlightAlertExtraPadding);
	}

	public const float FlightAlertMinWidth = 820;
	public const float FlightAlertExtraPadding = 60;

	/// <summary>The flight alerts' stack of <paramref name="lines"/> panels around its anchor (top center), for a typical event - the real width follows the one shown.</summary>
	public static SKRect FlightAlertsBox(string? fontFamily, int lines)
	{
		int count = Math.Clamp(lines, 1, FlightAlertsMaxLines);
		float text = Math.Max(LineExtent(CoreStrings.Overlay_CriticalVoltage.ToUpperInvariant(), GpsAlertTitleSize, fontFamily).Width,
			LineExtent(string.Format(CoreStrings.Overlay_CellVoltageDetail, "3.30"), GpsAlertDetailSize, fontFamily).Width);
		float width = FlightAlertWidth(text);
		return new SKRect(-width / 2, 0, width / 2, count * GpsAlertHeight + (count - 1) * 14);
	}

	/// <summary>The widgets drawn as a caption over a value and a unit (DrawStat), which can drop the caption or the unit and choose the caption's case.</summary>
	public static bool SupportsStatParts(OverlayElement element)
	{
		return element is ElevationElement or GradientElement or DistanceElement or TemperatureElementBase or DeviceDistanceElement or DeviceBatteryElement
			or ImageLumaElement or HeightElement or HomeDistanceElement or VerticalSpeedElement or TripStatElement;
	}

	/// <summary>The stat widgets whose reading has digits after a decimal point to choose.</summary>
	public static bool SupportsDecimals(OverlayElement element)
	{
		return SupportsStatParts(element) && element is not (DeviceBatteryElement or ImageLumaElement);
	}

	/// <summary>The widgets whose lines of text TextAlign lines up (the rest are one line, or a layout of their own).</summary>
	public static bool SupportsTextAlign(OverlayElement element)
	{
		return element is ElevationElement or GradientElement or DistanceElement or TemperatureElementBase or DeviceDistanceElement or DeviceBatteryElement
			or ImageLumaElement or HeightElement or HomeDistanceElement or VerticalSpeedElement or TripStatElement or TextElement or ElapsedTimeTextElement
			or CameraInfoElement or FlightModeElement;
	}

	/// <summary>
	///     The right edge of the text a widget lines its text up to - its measured box without the padding around the ink. The measured box
	///     is from sample values, so the edge stays put as the readout's digits change.
	/// </summary>
	internal static float TextBoxRight(OverlayElement element)
	{
		float pad = element switch
		{
			TextElement or ElapsedTimeTextElement => DateFontSize,
			CameraInfoElement => SmallFontSize,
			FlightModeElement => FlightModeSize,
			_ => ValueFontSize
		} * 0.09f;
		SKRect box = GetBounds(element.Type, 0, 0, 1, label: (element as LabeledStatElement)?.Label ?? (element as ElapsedTimeTextElement)?.Label,
			fontFamily: (element as StyledOverlayElement)?.FontFamily, text: (element as TextElement)?.Text, element: element);
		return box.IsEmpty ? 0 : box.Right - pad;
	}

	/// <summary>Where a line `line` wide starts in a box `box` wide for this alignment.</summary>
	internal static float AlignedX(TextAlign align, float box, float line)
	{
		return align switch
		{
			TextAlign.Center => (box - line) / 2,
			TextAlign.Right => box - line,
			_ => 0
		};
	}

	/// <summary>Mirrors DrawStat's layout: an uppercased label at y=0, then VALUE + " " + UNIT at y=StatValueBaselineY.</summary>
	private static SKRect MeasureStat(string label, string value, string unit, string? family)
	{
		(float labelAscent, float labelDescent, float labelWidth) = LineExtent(label.ToUpperInvariant(), LabelFontSize, family);
		(float valueAscent, float valueDescent, float valueWidth) = LineExtent(value, ValueFontSize, family);
		(_, float unitDescent, float unitWidth) = LineExtent(unit, UnitFontSize, family);
		float pad = ValueFontSize * 0.09f;

		float top = Math.Min(labelAscent, StatValueBaselineY + valueAscent) - pad;
		float bottom = Math.Max(labelDescent, Math.Max(StatValueBaselineY + valueDescent, StatValueBaselineY + unitDescent)) + pad;
		float right = Math.Max(labelWidth, valueWidth + StatUnitGapX + unitWidth) + pad;
		return new SKRect(-pad, top, right, bottom);
	}

	/// <summary>Mirrors DrawText: one line per TextLines entry, font.Spacing apart.</summary>
	private static SKRect MeasureText(string text, string? family)
	{
		SKFont font = GetFont(family, DateFontSize);
		string[] lines = TextLines(text);
		float width = lines.Max(line => font.MeasureText(line));
		font.GetFontMetrics(out SKFontMetrics metrics);
		float pad = DateFontSize * 0.09f;
		return new SKRect(-pad, metrics.Ascent - pad, width + pad, (lines.Length - 1) * font.Spacing + metrics.Descent + pad);
	}

	/// <summary>Mirrors DrawProfileChart: label and value on the y=0 line, the panel from ChartTop down.</summary>
	private static SKRect MeasureChart(string label, string? family)
	{
		(float labelAscent, _, float labelWidth) = LineExtent(label.ToUpperInvariant(), LabelFontSize, family);
		(float valueAscent, _, _) = LineExtent(SampleChartValue, SmallFontSize, family);
		float pad = SmallFontSize * 0.09f;
		return new SKRect(-pad, Math.Min(labelAscent, valueAscent) - pad, Math.Max(ChartWidth, labelWidth) + pad, ChartTop + ChartHeight + pad);
	}

	/// <summary>A Text widget's lines - line breaks of any kind, never none (an empty text is one empty line).</summary>
	public static string[] TextLines(string? text)
	{
		return (text ?? "").ReplaceLineEndings("\n").Split('\n');
	}

	/// <summary>
	///     An image file's pixel size read from its header (no decoding), or null when it isn't a readable image or is
	///     larger than MaxImageDimension a side. Cached per path until the file's modification time changes.
	/// </summary>
	public static SKSizeI? ImageSize(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;

		DateTime modified;
		try
		{
			modified = File.GetLastWriteTimeUtc(path);
		}
		catch (Exception)
		{
			return null;
		}

		lock (ImageSizesLock)
		{
			if (ImageSizes.TryGetValue(path, out (DateTime Modified, SKSizeI? Size) cached) && cached.Modified == modified)
				return cached.Size;
		}

		SKSizeI? size = null;
		try
		{
			using var codec = SKCodec.Create(path);
			if (codec is { Info: { Width: > 0 and <= MaxImageDimension, Height: > 0 and <= MaxImageDimension } info })
				size = new SKSizeI(info.Width, info.Height);
		}
		catch (Exception)
		{
			// Not an image Skia can read - no size, like a missing file.
		}

		lock (ImageSizesLock)
		{
			if (ImageSizes.Count >= MaxCachedImageSizes && !ImageSizes.ContainsKey(path)) ImageSizes.Clear();
			ImageSizes[path] = (modified, size);
		}

		return size;
	}

	/// <summary>Mirrors DrawCameraInfo's layout: an uppercased label at y=0, then three lines at CameraInfoLineBaselineYs.</summary>
	private static SKRect MeasureCameraInfo(string label, string? family)
	{
		(float labelAscent, float labelDescent, float labelWidth) = LineExtent(label.ToUpperInvariant(), LabelFontSize, family);
		float pad = SmallFontSize * 0.09f;

		float top = labelAscent - pad;
		float bottom = labelDescent;
		float right = labelWidth;

		string[] lines = [SampleIsoText, SampleShutterText, SampleColorTempText];
		for (int i = 0; i < lines.Length; i++)
		{
			(_, float descent, float width) = LineExtent(lines[i], SmallFontSize, family);
			bottom = Math.Max(bottom, CameraInfoLineBaselineYs[i] + descent);
			right = Math.Max(right, width);
		}

		return new SKRect(-pad, top, right + pad, bottom + pad);
	}
}
