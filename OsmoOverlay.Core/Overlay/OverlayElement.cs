namespace OsmoOverlay.Core.Overlay;

public enum OverlayElementType
{
	StatsBlock,
	Compass,
	SunWidget,
	PitchGauge,
	SpeedGauge
}

/// <summary>
///     One positionable widget in the HUD. X/Y is the widget's anchor point - top-left for the
///     rectangular stats block, circle center for the four round gauges/widgets.
/// </summary>
public sealed record OverlayElement(OverlayElementType Type, float X, float Y, bool Visible = true);
