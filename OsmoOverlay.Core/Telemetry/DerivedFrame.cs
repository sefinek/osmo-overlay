namespace OsmoOverlay.Core.Telemetry;

public sealed record DerivedFrame(
	TelemetryFrame Raw,
	double SpeedKmh,
	double HeadingDegrees,
	double GradientPercent,
	double CumulativeDistanceMeters,
	// Left/right lean (positive = right) and nose up/down (positive = up), vehicle acceleration taken out - CameraTilt.
	double RollDegrees,
	double PitchDegrees,
	SunPosition Sun,
	double LocalEastMeters,
	double LocalNorthMeters,
	double SmoothedGForce,
	// The accelerometer sideways (positive = right) and forward (positive = forward), unsmoothed, from the camera's
	// gravity (ICameraFormat.Gravity) - 0 when the camera's axes aren't known. The G-meter smooths it itself (OverlayRenderer.SmoothGMeterDelta).
	double LateralAccelG,
	double LongitudinalAccelG,
	// The first frame after a cut (OutputTimeline.MapFrames) or after a gap between files (TelemetryFrame.StartsAfterGap) -
	// where a drawn route must not simply carry on (RouteJoin).
	bool StartsAfterCut = false);
