using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Layout constants shared between OverlayRenderer (drawing) and the GUI editor (hit-testing for
///     drag-to-reposition), so the two can't drift apart. Sizes are fixed per element type in V1 -
///     only position is editable. All sizes are expressed at ReferenceWidth x ReferenceHeight (4K);
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
	public const float PitchRadius = 95f;
	public const float MapRadius = 260f;
	public const float GMeterRadius = 110f;
	public const float LabelBelowRadiusOffset = 56f;
	public const float LabelBelowRadiusPadding = LabelBelowRadiusOffset + 14f;

	public const float DateFontSize = 46f;
	public const float LabelFontSize = 30f;
	public const float ValueFontSize = 95f;
	public const float UnitFontSize = 46f;
	public const float SmallFontSize = 36f;

	public const float ProgressBarWidth = 760f;
	public const float ProgressBarHeight = 60f;

	// DrawStat (Elevation/Gradient/Distance) and DrawCameraInfo's own fixed layout - the y-offsets each
	// line's baseline sits at, in the same reference-pixel space GetBounds measures in. Kept here (not
	// re-derived) since they mirror exact literals in OverlayRenderer.TextWidgets.cs.
	private const float StatValueBaselineY = 90f;
	private const float StatUnitGapX = 12f;
	private static readonly float[] CameraInfoLineBaselineYs = [58f, 102f, 146f];

	// Representative sample content GetBounds measures for widgets whose real text isn't known at
	// call time (or is passed in as null) - chosen to be at least as wide/tall as what that widget
	// realistically renders, so the computed hit box is a safe (if occasionally slightly generous)
	// approximation rather than one that can clip the real text.
	private static readonly DateTime SampleDateTime = new(2026, 12, 31, 23, 59, 59);
	private const double SampleElapsedSeconds = 3661; // -> "01:01:01", the widest ElapsedTimeText ever shows (once past an hour)
	private const string SampleCameraModel = "DJI Osmo Action 6";
	private const string SampleElevationValue = "-9999";
	private const string SampleGradientValue = "-99";
	private const string SampleDistanceValue = "9999.99";
	private const string SampleIsoText = "ISO 12800";
	private const string SampleShutterText = "1/8000 S";
	private const string SampleColorTempText = "9900 K";

	// Lives for the process lifetime rather than being disposed - this is a static utility class with
	// no owner to call Dispose, the same "cheap, kept forever" pattern already used for the GUI's own
	// static cursors (see MainWindow.OverlayEditor.cs). Deliberately a *separate* SKTypeface instance
	// from the one OverlayRenderer builds per render job (which it does own and dispose) - both resolve
	// the same font family/style deterministically, so the metrics measured here can't drift from what
	// gets drawn even though the two never share a handle.
	private static readonly SKTypeface HudTypeface = CreateHudTypeface();
	private static readonly Dictionary<string, SKTypeface> TypefacesByFamily = [];
	private static readonly Dictionary<(string Family, float Size), SKFont> FontsBySize = [];

	public static float GetScale(int width, int height)
	{
		return Math.Min(width / ReferenceWidth, height / ReferenceHeight);
	}

	/// <summary>
	///     The exact bold system-font fallback chain OverlayRenderer draws every HUD widget with - moved
	///     here (rather than duplicated) so GetBounds measures with the identical typeface.
	/// </summary>
	public static SKTypeface CreateHudTypeface()
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
	///     only have the type.
	/// </summary>
	public static SKRect GetBounds(OverlayElementType type, float x, float y, float scale,
		string? dateFormat = null, string? locale = null, string? label = null, string? cameraModel = null,
		string? fontFamily = null)
	{
		SKRect local = type switch
		{
			// DrawTimeText/DrawElapsedTime/DrawCameraModel render with the baseline at (0, 0) - MeasureLine
			// uses the font's own ascent/descent (not one sample string's ink extent) for the vertical
			// span, so a short sample still yields a box tall enough for real text with descenders/accents.
			OverlayElementType.DateTimeText => MeasureLine(SampleDateTimeText(dateFormat, locale), DateFontSize, fontFamily),
			OverlayElementType.UtcTimeText => MeasureLine(SampleDateTimeText(dateFormat, locale) + "  UTC", DateFontSize, fontFamily),
			OverlayElementType.ElapsedTimeText => MeasureLine(OverlayTimeFormatting.FormatElapsed(SampleElapsedSeconds), DateFontSize, fontFamily),
			OverlayElementType.CameraModelText => MeasureLine(cameraModel ?? SampleCameraModel, DateFontSize, fontFamily),
			OverlayElementType.Elevation => MeasureStat(label ?? "ELEVATION", SampleElevationValue, "M", fontFamily),
			OverlayElementType.Gradient => MeasureStat(label ?? "GRADIENT", SampleGradientValue, "%", fontFamily),
			OverlayElementType.Distance => MeasureStat(label ?? "TOTAL DISTANCE", SampleDistanceValue, "KM", fontFamily),
			OverlayElementType.CameraInfo => MeasureCameraInfo(label ?? "CAMERA", fontFamily),
			// TripProgressBar's anchor is the bar's vertical/horizontal center (DrawTripProgressBar
			// translates to (x, y) and draws the track symmetrically around it), unlike the text panels
			// above whose anchor is a corner - so the hit box is centered on (x, y) too.
			OverlayElementType.TripProgressBar =>
				new SKRect(-ProgressBarWidth / 2, -ProgressBarHeight / 2, ProgressBarWidth / 2, ProgressBarHeight / 2),
			OverlayElementType.Compass => CircleLocal(CompassRadius),
			OverlayElementType.SunWidget => CircleWithBottomLabelLocal(SunRadius),
			OverlayElementType.PitchGauge => CircleLocal(PitchRadius),
			OverlayElementType.SpeedGauge => CircleLocal(SpeedRadius),
			OverlayElementType.MapWidget => CircleLocal(MapRadius),
			OverlayElementType.GMeter => CircleWithBottomLabelLocal(GMeterRadius),
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
		OverlayTimeFormatting.TryFormat(SampleDateTime, format, locale, out var text);
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

	private static (float Ascent, float Descent, float Width) LineExtent(string text, float fontSize, string? family)
	{
		SKFont font = GetFont(family, fontSize);
		font.GetFontMetrics(out SKFontMetrics metrics);
		return (metrics.Ascent, metrics.Descent, font.MeasureText(text));
	}

	/// <summary>
	///     A single line of DrawOutlined text with its baseline at local (0, 0): horizontal extent from
	///     measuring the actual text, vertical extent from the font's ascent/descent (see GetBounds' doc).
	///     Padded a little on every edge for DrawOutlined's drop-shadow offset + stroke width, which both
	///     draw slightly outside the glyph outline itself.
	/// </summary>
	private static SKRect MeasureLine(string text, float fontSize, string? family)
	{
		(var ascent, var descent, var width) = LineExtent(text, fontSize, family);
		var pad = fontSize * 0.09f;
		return new SKRect(-pad, ascent - pad, width + pad, descent + pad);
	}

	/// <summary>Mirrors DrawStat's layout: an uppercased label at y=0, then VALUE + " " + UNIT at y=StatValueBaselineY.</summary>
	private static SKRect MeasureStat(string label, string value, string unit, string? family)
	{
		(var labelAscent, var labelDescent, var labelWidth) = LineExtent(label.ToUpperInvariant(), LabelFontSize, family);
		(var valueAscent, var valueDescent, var valueWidth) = LineExtent(value, ValueFontSize, family);
		(_, var unitDescent, var unitWidth) = LineExtent(unit, UnitFontSize, family);
		var pad = ValueFontSize * 0.09f;

		var top = Math.Min(labelAscent, StatValueBaselineY + valueAscent) - pad;
		var bottom = Math.Max(labelDescent, Math.Max(StatValueBaselineY + valueDescent, StatValueBaselineY + unitDescent)) + pad;
		var right = Math.Max(labelWidth, valueWidth + StatUnitGapX + unitWidth) + pad;
		return new SKRect(-pad, top, right, bottom);
	}

	/// <summary>Mirrors DrawCameraInfo's layout: an uppercased label at y=0, then three lines at CameraInfoLineBaselineYs.</summary>
	private static SKRect MeasureCameraInfo(string label, string? family)
	{
		(var labelAscent, var labelDescent, var labelWidth) = LineExtent(label.ToUpperInvariant(), LabelFontSize, family);
		var pad = SmallFontSize * 0.09f;

		var top = labelAscent - pad;
		var bottom = labelDescent;
		var right = labelWidth;

		string[] lines = [SampleIsoText, SampleShutterText, SampleColorTempText];
		for (var i = 0; i < lines.Length; i++)
		{
			(_, var descent, var width) = LineExtent(lines[i], SmallFontSize, family);
			bottom = Math.Max(bottom, CameraInfoLineBaselineYs[i] + descent);
			right = Math.Max(right, width);
		}

		return new SKRect(-pad, top, right + pad, bottom + pad);
	}
}
