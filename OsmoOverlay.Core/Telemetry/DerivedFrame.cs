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
	double SmoothedGForce);
