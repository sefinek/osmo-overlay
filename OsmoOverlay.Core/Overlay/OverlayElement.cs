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
///     circle center for round gauges/widgets. Units applies to Elevation/Distance/SpeedGauge/TripProgressBar;
///     DateFormat/Locale to DateTimeText and UtcTimeText (raw GPS timestamp, no local-time
///     conversion, but same format/locale fields); Label (caption override) to
///     Elevation/Gradient/Distance/CameraInfo. Map* is MapWidget-only: MapZoom is the fixed
///     close-up zoom the panning widget draws at; MapDynamicZoomMaxFactor caps how far
///     MapDynamicZoom can zoom out (see OverlayRenderer.GetMapZoomFactor). The tile source itself
///     (URL template/attribution/API key) is a global setting shared by every map-based widget -
///     see OverlaySettings - not a per-element field, so it can't drift between MapWidget instances.
///     Trail* is duplicated independently on Compass and MapWidget so the two can be styled
///     differently - TrailColor/TrailWidth for the route line, TrailUseArrow for the current-position
///     marker (heading arrow vs. a static dot) - see OverlayRenderer.DrawTrailMarker.
///     GMeterFullScaleG is GMeter-only: how many G's put the dot at the ring's edge - there's no
///     "correct" default across activities (a bike ride and a track day see very different real G
///     ranges), see OverlayRenderer.DrawGMeter.
///     TripArrivedToleranceMeters/TripArrivedLabel are TripProgressBar-only: how close (in meters)
///     to the recorded total distance counts as "arrived", and what to show in place of "X LEFT"
///     once within that margin - see OverlayRenderer.DrawTripProgressBar.
///     Null means "use the built-in default" (Locale null means OS/thread culture at render time),
///     so old preset files without these fields still deserialize correctly.
/// </summary>
public sealed record OverlayElement(
	OverlayElementType Type,
	float X,
	float Y,
	bool Visible = true,
	UnitSystem Units = UnitSystem.Metric,
	string? DateFormat = null,
	string? Label = null,
	string? Locale = null,
	int MapZoom = 16,
	bool MapDynamicZoom = false,
	double MapDynamicZoomMaxFactor = OverlayRenderer.MapDynamicZoomMaxFactorDefault,
	string? TrailColor = null,
	float TrailWidth = 4.5f,
	bool TrailUseArrow = true,
	double GMeterFullScaleG = OverlayRenderer.GMeterFullScaleGDefault,
	double TripArrivedToleranceMeters = OverlayRenderer.TripArrivedToleranceMetersDefault,
	string TripArrivedLabel = OverlayRenderer.TripArrivedLabelDefault);
