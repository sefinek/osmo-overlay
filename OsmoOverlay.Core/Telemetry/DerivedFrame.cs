namespace OsmoOverlay.Core.Telemetry;

public sealed record DerivedFrame(
	TelemetryFrame Raw,
	double SpeedKmh,
	double HeadingDegrees,
	double GradientPercent,
	double CumulativeDistanceMeters,
	double PitchDegrees,
	SunPosition Sun,
	double LocalEastMeters,
	double LocalNorthMeters,
	double SmoothedGForce,
	// The first frame after a cut (OutputTimeline.MapFrames) or after a gap between files (TelemetryFrame.StartsAfterGap) -
	// where a drawn route must not simply carry on (RouteJoin).
	bool StartsAfterCut = false);
