using System.Text.Json.Serialization;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     How a widget transitions into or out of the frame at its AppearAtSeconds/DisappearAtSeconds edges (see
///     ElementAnimation). None is an instant hard cut - no fade, no offset. The slide directions name the way the widget
///     moves: coming in, SlideUp rises into place from below; going out, SlideUp rises out of it.
/// </summary>
public enum OverlayAnimationType
{
	None,
	Fade,
	SlideUp,
	SlideDown,
	SlideLeft,
	SlideRight
}

public enum OverlayElementType
{
	DateTimeText,
	Elevation,
	Gradient,
	Distance,
	Compass,
	SunWidget,
	RollGauge,
	SpeedGauge,
	MapWidget,
	UtcTimeText,
	CameraInfo,
	ElapsedTimeText,
	CameraModelText,
	GMeter,
	TripProgressBar,
	PitchGauge,
	ProfileChart,
	TripStat,
	Text,
	Image
}

/// <summary>
///     One positionable widget in the HUD. X/Y is the anchor point - top-left for text elements,
///     circle center for round gauges/widgets. Base type carries only what every widget type needs
///     regardless of what it draws: position, visibility, id, per-widget resize (Scale) and the
///     appear/disappear timing/animation (AppearAtSeconds/DisappearAtSeconds, AnimationType/AnimationDurationSeconds on
///     the way in, OutAnimationType/OutAnimationDurationSeconds on the way out - see ElementAnimation). Everything type-specific lives
///     on the matching subtype below instead of being a "carried but ignored" field on every other type -
///     see the intermediate abstract types for the shared groups (style, labeled-stat, time-text, trail).
///     Type is a computed discriminator (each leaf overrides it to a fixed value) rather than a stored
///     field - JsonPolymorphic/JsonDerivedType (see the attributes below) writes/reads this value under the
///     "Type" JSON key, and this property stays available for the many call sites that switch/pattern-match
///     on it. It's [JsonIgnore]'d so it doesn't collide with that same-named discriminator during
///     serialization.
///     Id distinguishes multiple instances of the same Type (the GUI lets you drag a widget onto the
///     canvas more than once) - Type alone is not unique within a preset's Elements. Empty string is
///     only a transient construction state: OverlayPreset.CreateDefault always assigns Type.ToString()
///     to the one instance it creates per type, new instances the GUI adds get a random Guid, and a
///     loaded preset with an element without one is rejected (OverlayPresetStore.Validate).
///     Scale multiplies a widget's size on top of the resolution-based scale every widget already draws
///     at (OverlayElementBounds.GetScale) - applied once in OverlayRenderer.BeginElement, pivoted on X/Y so
///     resizing never moves the widget. 1 (default) is the widget's base size.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(DateTimeTextElement), (int)OverlayElementType.DateTimeText)]
[JsonDerivedType(typeof(ElevationElement), (int)OverlayElementType.Elevation)]
[JsonDerivedType(typeof(GradientElement), (int)OverlayElementType.Gradient)]
[JsonDerivedType(typeof(DistanceElement), (int)OverlayElementType.Distance)]
[JsonDerivedType(typeof(CompassElement), (int)OverlayElementType.Compass)]
[JsonDerivedType(typeof(SunWidgetElement), (int)OverlayElementType.SunWidget)]
[JsonDerivedType(typeof(RollGaugeElement), (int)OverlayElementType.RollGauge)]
[JsonDerivedType(typeof(SpeedGaugeElement), (int)OverlayElementType.SpeedGauge)]
[JsonDerivedType(typeof(MapWidgetElement), (int)OverlayElementType.MapWidget)]
[JsonDerivedType(typeof(UtcTimeTextElement), (int)OverlayElementType.UtcTimeText)]
[JsonDerivedType(typeof(CameraInfoElement), (int)OverlayElementType.CameraInfo)]
[JsonDerivedType(typeof(ElapsedTimeTextElement), (int)OverlayElementType.ElapsedTimeText)]
[JsonDerivedType(typeof(CameraModelTextElement), (int)OverlayElementType.CameraModelText)]
[JsonDerivedType(typeof(GMeterElement), (int)OverlayElementType.GMeter)]
[JsonDerivedType(typeof(TripProgressBarElement), (int)OverlayElementType.TripProgressBar)]
[JsonDerivedType(typeof(PitchGaugeElement), (int)OverlayElementType.PitchGauge)]
[JsonDerivedType(typeof(ProfileChartElement), (int)OverlayElementType.ProfileChart)]
[JsonDerivedType(typeof(TripStatElement), (int)OverlayElementType.TripStat)]
[JsonDerivedType(typeof(TextElement), (int)OverlayElementType.Text)]
[JsonDerivedType(typeof(ImageElement), (int)OverlayElementType.Image)]
public abstract record OverlayElement
{
	[JsonIgnore] public abstract OverlayElementType Type { get; }

	public required float X { get; init; }
	public required float Y { get; init; }
	public bool Visible { get; init; } = true;
	public string Id { get; init; } = "";
	public float Scale { get; init; } = 1f;
	public double? AppearAtSeconds { get; init; }
	public double? DisappearAtSeconds { get; init; }
	public OverlayAnimationType AnimationType { get; init; } = OverlayAnimationType.None;
	public double AnimationDurationSeconds { get; init; } = OverlayRenderer.AnimationDurationSecondsDefault;
	// Null: no way out (a hard cut), and the default length once one is picked (ElementAnimation).
	public OverlayAnimationType? OutAnimationType { get; init; }
	public double? OutAnimationDurationSeconds { get; init; }

	/// <summary>
	///     The layer this widget is on in the GUI's layer view - widgets sharing one are drawn one after another. Null: a layer
	///     of its own (the default), keyed by its Id, so a widget another one was put next to keeps that layer by its own Id.
	///     The renderer doesn't read it - the layout's order is the draw order, which the GUI keeps grouped by layer.
	/// </summary>
	public string? LayerId { get; init; }
}

/// <summary>
///     FontFamily/TextColor/OutlineColor/OutlineWidth style the text-based widgets and every round
///     gauge's own text readout - shared by every OverlayElementType except Compass/MapWidget/
///     TripProgressBar/Image (see OverlayRenderer.TextWidgets.cs/OverlayRenderer.Gauges.cs). Null/1 means "use
///     the built-in default" (white text, the built-in near-black outline at its normal width, the
///     built-in HUD font) - an invalid hex or a font family not installed on this machine falls back
///     rather than throwing, same fail-soft policy as TrailColor/DateFormat.
/// </summary>
public abstract record StyledOverlayElement : OverlayElement
{
	public string? FontFamily { get; init; }
	public string? TextColor { get; init; }
	public string? OutlineColor { get; init; }
	public float OutlineWidth { get; init; } = 1f;
}

/// <summary>
///     The labeled widgets (Elevation/Gradient/Distance/CameraInfo/TripStat/ProfileChart): Label overrides the
///     built-in caption, AccentColor styles the value text specifically (distinct from TextColor, which
///     styles the label/unit text; ProfileChart's line and fill too) - see OverlayRenderer.TextWidgets.cs
///     DrawStat/DrawCameraInfo and OverlayRenderer.Charts.cs.
/// </summary>
public abstract record LabeledStatElement : StyledOverlayElement
{
	public string? AccentColor { get; init; }
	public string? Label { get; init; }
}

/// <summary>Reference: the GPS altitude itself (what "ELEVATION" reads as), or the change since the render's first frame.</summary>
public sealed record ElevationElement : LabeledStatElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.Elevation;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
	public ElevationReference Reference { get; init; } = ElevationReference.SeaLevel;
}

public enum ElevationReference
{
	Start,
	SeaLevel
}

/// <summary>No Units - OverlayRenderer.DrawGradient always shows a plain percentage, unit-agnostic.</summary>
public sealed record GradientElement : LabeledStatElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.Gradient;
}

public sealed record DistanceElement : LabeledStatElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.Distance;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

/// <summary>No Units - CameraInfo shows ISO/shutter/color-temperature, none of which are metric/imperial.</summary>
public sealed record CameraInfoElement : LabeledStatElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.CameraInfo;
}

/// <summary>
///     DateFormat/Locale for DateTimeText and UtcTimeText (raw GPS timestamp, no local-time conversion,
///     but same format/locale fields) - null means "use the built-in default" (Locale null means
///     OS/thread culture at render time).
/// </summary>
public abstract record TimeTextElementBase : StyledOverlayElement
{
	public string? DateFormat { get; init; }
	public string? Locale { get; init; }
}

public sealed record DateTimeTextElement : TimeTextElementBase
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.DateTimeText;
}

public sealed record UtcTimeTextElement : TimeTextElementBase
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.UtcTimeText;
}

/// <summary>Label: an optional caption above the time - null/empty draws the time alone (unlike LabeledStatElement, no built-in caption).</summary>
public sealed record ElapsedTimeTextElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.ElapsedTimeText;
	public string? Label { get; init; }
}

public sealed record CameraModelTextElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.CameraModelText;
}

public sealed record SpeedGaugeElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.SpeedGauge;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

/// <summary>Left/right lean - DerivedFrame.RollDegrees.</summary>
public sealed record RollGaugeElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.RollGauge;
}

/// <summary>Nose up/down - DerivedFrame.PitchDegrees.</summary>
public sealed record PitchGaugeElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.PitchGauge;
}

public sealed record SunWidgetElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.SunWidget;
}

/// <summary>
///     GMeterFullScaleG: how many G's put the dot at the ring's edge - there's no "correct" default
///     across activities (a bike ride and a track day see very different real G ranges), see
///     OverlayRenderer.DrawGMeter.
/// </summary>
public sealed record GMeterElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.GMeter;
	public double GMeterFullScaleG { get; init; } = OverlayRenderer.GMeterFullScaleGDefault;
}

/// <summary>
///     Trail* is duplicated independently on Compass and MapWidget (each instance has its own values)
///     so the two can be styled differently - TrailColor/TrailWidth for the route line, TrailUseArrow
///     for the current-position marker (heading arrow vs. a static dot) - see
///     OverlayRenderer.DrawTrailMarker. Compass/MapWidget ignore every style field (FontFamily/TextColor/
///     etc.), which is why this branches off OverlayElement directly rather than StyledOverlayElement.
/// </summary>
public abstract record TrailOverlayElement : OverlayElement
{
	public string? TrailColor { get; init; }
	// Colors the route by the speed it was travelled at - TrailColor while slow, warming to red (SpeedColorScale).
	public bool TrailColorBySpeed { get; init; } = true;
	public float TrailWidth { get; init; } = 4.5f;
	public bool TrailUseArrow { get; init; } = true;
}

public sealed record CompassElement : TrailOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.Compass;
}

/// <summary>
///     MapZoom is the fixed close-up zoom the panning widget draws at; MapDynamicZoomMaxFactor caps how
///     far MapDynamicZoom can zoom out (see OverlayRenderer.GetMapZoomFactor). The tile source itself
///     (URL template/attribution/API key) is a global setting shared by every map-based widget - see
///     OverlaySettings - not a per-element field, so it can't drift between MapWidget instances.
/// </summary>
public sealed record MapWidgetElement : TrailOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.MapWidget;
	public int MapZoom { get; init; } = 16;
	public bool MapDynamicZoom { get; init; }
	public double MapDynamicZoomMaxFactor { get; init; } = OverlayRenderer.MapDynamicZoomMaxFactorDefault;
}

/// <summary>
///     TripArrivedToleranceMeters/TripArrivedLabel: how close (in meters) to the recorded total distance
///     counts as "arrived", and what to show in place of "X LEFT" once within that margin - see
///     OverlayRenderer.DrawTripProgressBar. No style fields - this widget always draws in its own fixed
///     white/black look, never the per-element FontFamily/TextColor/etc.
/// </summary>
public sealed record TripProgressBarElement : OverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.TripProgressBar;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
	public double TripArrivedToleranceMeters { get; init; } = OverlayRenderer.TripArrivedToleranceMetersDefault;
	public string TripArrivedLabel { get; init; } = OverlayRenderer.TripArrivedLabelDefault;
}

public enum ProfileSeries
{
	Elevation,
	Speed
}

public enum ProfileAxis
{
	Distance,
	Time
}

/// <summary>The whole recording's elevation or speed as a chart, what's been covered so far highlighted - see OverlayRenderer.Charts.cs.</summary>
public sealed record ProfileChartElement : LabeledStatElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.ProfileChart;
	public ProfileSeries Series { get; init; } = ProfileSeries.Elevation;
	public ProfileAxis Axis { get; init; } = ProfileAxis.Distance;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

public enum TripStatKind
{
	MaxSpeed,
	AverageSpeed,
	ElevationGain,
	ElevationLoss,
	MovingTime
}

/// <summary>A running statistic - its value up to the frame shown, not the whole recording's (TripStats).</summary>
public sealed record TripStatElement : LabeledStatElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.TripStat;
	public TripStatKind Stat { get; init; } = TripStatKind.MaxSpeed;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

/// <summary>Free text, one or more lines.</summary>
public sealed record TextElement : StyledOverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.Text;
	public string Text { get; init; } = OverlayRenderer.TextDefault;
}

/// <summary>
///     A PNG/JPEG/WebP file (a logo, a watermark of your own) drawn at its own pixel size at the 4K reference, top-left
///     at X/Y. ImagePath is absolute - a preset moved to another machine draws nothing until it's picked again.
///     Opacity 0..1.
/// </summary>
public sealed record ImageElement : OverlayElement
{
	[JsonIgnore] public override OverlayElementType Type => OverlayElementType.Image;
	public string? ImagePath { get; init; }
	public float Opacity { get; init; } = 1f;
}
