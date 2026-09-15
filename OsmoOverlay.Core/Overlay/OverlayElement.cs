namespace OsmoOverlay.Core.Overlay;

public enum OverlayElementType
{
	DateTimeText,
	Elevation,
	Gradient,
	Distance,
	Compass,
	SunWidget,
	PitchGauge,
	SpeedGauge
}

/// <summary>
///     One positionable widget in the HUD. X/Y is the widget's anchor point - top-left for the
///     text elements, circle center for the four round gauges/widgets. Units/DateFormat/Label/Locale
///     are per-widget display settings: Units applies to Elevation/Distance/SpeedGauge, DateFormat and
///     Locale only to DateTimeText, Label (a custom caption override) to Elevation/Gradient/Distance.
///     Null means "use the built-in default" for that widget (Locale null means the OS/thread culture
///     at render time), so old preset files without these fields still deserialize correctly.
/// </summary>
public sealed record OverlayElement(
	OverlayElementType Type,
	float X,
	float Y,
	bool Visible = true,
	UnitSystem Units = UnitSystem.Metric,
	string? DateFormat = null,
	string? Label = null,
	string? Locale = null);
