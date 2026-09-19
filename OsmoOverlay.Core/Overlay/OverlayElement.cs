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
	GMeter
}

/// <summary>
///     One positionable widget in the HUD. X/Y is the anchor point - top-left for text elements,
///     circle center for round gauges/widgets. Units applies to Elevation/Distance/SpeedGauge;
///     DateFormat/Locale to DateTimeText and UtcTimeText (raw GPS timestamp, no local-time
///     conversion, but same format/locale fields); Label (caption override) to
///     Elevation/Gradient/Distance/CameraInfo. Map* is MapWidget-only: null MapTileUrlTemplate/MapAttribution
///     means the default OpenStreetMap source; MapDynamicZoomMaxFactor caps how far
///     MapDynamicZoom can zoom out (see OverlayRenderer.GetMapZoomFactor); MapShowAttribution
///     should normally stay on - most tile providers require visible credit wherever the map is
///     shown, and turning it off moves that responsibility onto the user; MapApiKey fills a literal
///     "{api_key}" placeholder for providers that need one (e.g. CARTO), and is a no-op otherwise.
///     Trail* is duplicated independently on Compass and MapWidget so the two can be styled
///     differently - TrailColor/TrailWidth for the route line, TrailUseArrow for the current-position
///     marker (heading arrow vs. a static dot) - see OverlayRenderer.DrawTrailMarker.
///     GMeterFullScaleG is GMeter-only: how many G's put the dot at the ring's edge - there's no
///     "correct" default across activities (a bike ride and a track day see very different real G
///     ranges), see OverlayRenderer.DrawGMeter.
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
	string? MapTileUrlTemplate = null,
	int MapZoom = 16,
	string? MapAttribution = null,
	bool MapDynamicZoom = false,
	double MapDynamicZoomMaxFactor = OverlayRenderer.MapDynamicZoomMaxFactorDefault,
	bool MapShowAttribution = true,
	string? MapApiKey = null,
	string? TrailColor = null,
	float TrailWidth = 4.5f,
	bool TrailUseArrow = true,
	double GMeterFullScaleG = OverlayRenderer.GMeterFullScaleGDefault);
