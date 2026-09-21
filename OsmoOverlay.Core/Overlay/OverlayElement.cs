using System.Text.Json.Serialization;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     How a widget transitions in/out of the frame at its AppearAtSeconds/DisappearAtSeconds edges (see
///     OverlayElement). None is an instant hard cut - no fade, no offset - matching every widget's
///     existing always-on behavior exactly, so a preset with no timing set renders identically to before
///     this existed. The slide directions name where the widget slides *in from* (SlideUp enters moving
///     upward from below its resting position, etc.), mirrored in reverse on the way out.
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
	PitchGauge,
	SpeedGauge,
	MapWidget,
	UtcTimeText,
	CameraInfo,
	ElapsedTimeText,
	CameraModelText,
	GMeter,
	TripProgressBar
}

/// <summary>
///     One positionable widget in the HUD. X/Y is the anchor point - top-left for text elements,
///     circle center for round gauges/widgets. Base type carries only what every widget type needs
///     regardless of what it draws: position, visibility, id, per-widget resize (Scale) and the
///     appear/disappear timing/animation (AppearAtSeconds/DisappearAtSeconds/AnimationType/
///     AnimationDurationSeconds - see OverlayRenderer.ElementProgress). Everything type-specific lives
///     on the matching subtype below instead of being a "carried but ignored" field on every other type -
///     see the intermediate abstract types for the shared groups (style, labeled-stat, time-text, trail).
///     Type is a computed discriminator (each leaf overrides it to a fixed value) rather than a stored
///     field - JsonPolymorphic/JsonDerivedType (see the attributes below) already writes/reads this exact
///     value under the same "Type" JSON key old flat preset files used, so old files keep deserializing
///     unchanged and this property stays available for the many call sites that switch/pattern-match on
///     it. It's [JsonIgnore]'d so it doesn't collide with that same-named discriminator during
///     serialization.
///     Id distinguishes multiple instances of the same Type (the GUI lets you drag a widget onto the
///     canvas more than once) - Type alone is not unique within a preset's Elements. Empty string is
///     only a transient/deserialization state: OverlayPreset.CreateDefault always assigns Type.ToString()
///     to the one instance it creates per type, new instances the GUI adds get a random Guid, and
///     OverlayPreset.WithElementIdsBackfilled fixes up presets saved before this field existed.
///     Scale multiplies a widget's size on top of the resolution-based scale every widget already draws
///     at (OverlayElementBounds.GetScale) - applied once in OverlayRenderer.DrawElement, pivoted on X/Y so
///     resizing never moves the widget. 1 (default) renders at the same size as before this field existed.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(DateTimeTextElement), (int)OverlayElementType.DateTimeText)]
[JsonDerivedType(typeof(ElevationElement), (int)OverlayElementType.Elevation)]
[JsonDerivedType(typeof(GradientElement), (int)OverlayElementType.Gradient)]
[JsonDerivedType(typeof(DistanceElement), (int)OverlayElementType.Distance)]
[JsonDerivedType(typeof(CompassElement), (int)OverlayElementType.Compass)]
[JsonDerivedType(typeof(SunWidgetElement), (int)OverlayElementType.SunWidget)]
[JsonDerivedType(typeof(PitchGaugeElement), (int)OverlayElementType.PitchGauge)]
[JsonDerivedType(typeof(SpeedGaugeElement), (int)OverlayElementType.SpeedGauge)]
[JsonDerivedType(typeof(MapWidgetElement), (int)OverlayElementType.MapWidget)]
[JsonDerivedType(typeof(UtcTimeTextElement), (int)OverlayElementType.UtcTimeText)]
[JsonDerivedType(typeof(CameraInfoElement), (int)OverlayElementType.CameraInfo)]
[JsonDerivedType(typeof(ElapsedTimeTextElement), (int)OverlayElementType.ElapsedTimeText)]
[JsonDerivedType(typeof(CameraModelTextElement), (int)OverlayElementType.CameraModelText)]
[JsonDerivedType(typeof(GMeterElement), (int)OverlayElementType.GMeter)]
[JsonDerivedType(typeof(TripProgressBarElement), (int)OverlayElementType.TripProgressBar)]
public abstract record OverlayElement
{
	[JsonIgnore]
	public abstract OverlayElementType Type { get; }

	public required float X { get; init; }
	public required float Y { get; init; }
	public bool Visible { get; init; } = true;
	public string Id { get; init; } = "";
	public float Scale { get; init; } = 1f;
	public double? AppearAtSeconds { get; init; }
	public double? DisappearAtSeconds { get; init; }
	public OverlayAnimationType AnimationType { get; init; } = OverlayAnimationType.None;
	public double AnimationDurationSeconds { get; init; } = OverlayRenderer.AnimationDurationSecondsDefault;
}

/// <summary>
///     FontFamily/TextColor/OutlineColor/OutlineWidth style the text-based widgets and every round
///     gauge's own text readout - shared by every OverlayElementType except Compass/MapWidget/
///     TripProgressBar (see OverlayRenderer.TextWidgets.cs/OverlayRenderer.Gauges.cs). Null/1 means "use
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
///     The four label+value stat widgets (Elevation/Gradient/Distance/CameraInfo): Label overrides the
///     built-in caption, AccentColor styles the value text specifically (distinct from TextColor, which
///     styles the label/unit text) - see OverlayRenderer.TextWidgets.cs DrawStat/DrawCameraInfo.
/// </summary>
public abstract record LabeledStatElement : StyledOverlayElement
{
	public string? AccentColor { get; init; }
	public string? Label { get; init; }
}

public sealed record ElevationElement : LabeledStatElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.Elevation;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

/// <summary>No Units - OverlayRenderer.DrawGradient always shows a plain percentage, unit-agnostic.</summary>
public sealed record GradientElement : LabeledStatElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.Gradient;
}

public sealed record DistanceElement : LabeledStatElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.Distance;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

/// <summary>No Units - CameraInfo shows ISO/shutter/color-temperature, none of which are metric/imperial.</summary>
public sealed record CameraInfoElement : LabeledStatElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.CameraInfo;
}

/// <summary>
///     DateFormat/Locale for DateTimeText and UtcTimeText (raw GPS timestamp, no local-time conversion,
///     but same format/locale fields) - null means "use the built-in default" (Locale null means
///     OS/thread culture at render time), so old preset files without these fields still deserialize
///     correctly.
/// </summary>
public abstract record TimeTextElementBase : StyledOverlayElement
{
	public string? DateFormat { get; init; }
	public string? Locale { get; init; }
}

public sealed record DateTimeTextElement : TimeTextElementBase
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.DateTimeText;
}

public sealed record UtcTimeTextElement : TimeTextElementBase
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.UtcTimeText;
}

public sealed record ElapsedTimeTextElement : StyledOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.ElapsedTimeText;
}

public sealed record CameraModelTextElement : StyledOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.CameraModelText;
}

public sealed record SpeedGaugeElement : StyledOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.SpeedGauge;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
}

public sealed record PitchGaugeElement : StyledOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.PitchGauge;
}

public sealed record SunWidgetElement : StyledOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.SunWidget;
}

/// <summary>
///     GMeterFullScaleG: how many G's put the dot at the ring's edge - there's no "correct" default
///     across activities (a bike ride and a track day see very different real G ranges), see
///     OverlayRenderer.DrawGMeter.
/// </summary>
public sealed record GMeterElement : StyledOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.GMeter;
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
	public float TrailWidth { get; init; } = 4.5f;
	public bool TrailUseArrow { get; init; } = true;
}

public sealed record CompassElement : TrailOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.Compass;
}

/// <summary>
///     MapZoom is the fixed close-up zoom the panning widget draws at; MapDynamicZoomMaxFactor caps how
///     far MapDynamicZoom can zoom out (see OverlayRenderer.GetMapZoomFactor). The tile source itself
///     (URL template/attribution/API key) is a global setting shared by every map-based widget - see
///     OverlaySettings - not a per-element field, so it can't drift between MapWidget instances.
/// </summary>
public sealed record MapWidgetElement : TrailOverlayElement
{
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.MapWidget;
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
	[JsonIgnore]
	public override OverlayElementType Type => OverlayElementType.TripProgressBar;
	public UnitSystem Units { get; init; } = UnitSystem.Metric;
	public double TripArrivedToleranceMeters { get; init; } = OverlayRenderer.TripArrivedToleranceMetersDefault;
	public string TripArrivedLabel { get; init; } = OverlayRenderer.TripArrivedLabelDefault;
}
