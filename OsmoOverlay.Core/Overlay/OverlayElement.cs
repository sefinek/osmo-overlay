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
///     AppearAtSeconds/DisappearAtSeconds/AnimationType/AnimationDurationSeconds apply to every widget
///     type uniformly (unlike the type-specific fields above) - see OverlayRenderer.ElementProgress.
///     Null AppearAtSeconds means visible from the very first frame; null DisappearAtSeconds means it
///     never goes away. Together with the default AnimationType of None, the default for all four is
///     "always visible, instant, exactly like every widget already behaved" - so an existing preset with
///     none of this set renders unchanged.
///     Scale multiplies a widget's size on top of the resolution-based scale every widget already draws
///     at (OverlayElementBounds.GetScale) - applied once in OverlayRenderer.DrawElement, pivoted on X/Y so
///     resizing never moves the widget. 1 (default) renders at the same size as before this field existed.
///     Id distinguishes multiple instances of the same Type (the GUI lets you drag a widget onto the
///     canvas more than once) - Type alone is no longer unique within a preset's Elements. Empty string
///     is only a transient/deserialization state: OverlayPreset.CreateDefault always assigns Type.ToString()
///     to the one instance it creates per type, new instances the GUI adds get a random Guid, and
///     OverlayPreset.WithElementIdsBackfilled fixes up presets saved before this field existed.
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
	string TripArrivedLabel = OverlayRenderer.TripArrivedLabelDefault,
	double? AppearAtSeconds = null,
	double? DisappearAtSeconds = null,
	OverlayAnimationType AnimationType = OverlayAnimationType.None,
	double AnimationDurationSeconds = OverlayRenderer.AnimationDurationSecondsDefault,
	string Id = "",
	float Scale = 1f);
