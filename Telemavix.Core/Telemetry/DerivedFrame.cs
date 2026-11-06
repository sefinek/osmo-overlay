using Telemavix.Core.Overlay;

namespace Telemavix.Core.Telemetry;

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
	// gravity (ICameraFormat.Gravity) - 0 when the camera's axes aren't known. The G-meter smooths it (GMeterDeltas).
	double LateralAccelG,
	double LongitudinalAccelG,
	// The first frame after a cut (OutputTimeline.MapFrames) or after a gap between files (TelemetryFrame.StartsAfterGap) -
	// where a drawn route must not simply carry on (RouteJoin) - CutJoin is the cut's own choice, null for the default (and for a gap between files).
	bool StartsAfterCut = false,
	RouteJoin? CutJoin = null,
	// The heading steadied for a widget that turns with it (HeadingStabilizer); null (frames made by hand) = HeadingDegrees.
	double? StableHeadingDegrees = null)
{
	/// <summary>The direction of travel a map turning with it uses when its heading is steadied.</summary>
	public double SteadyHeadingDegrees => StableHeadingDegrees ?? HeadingDegrees;

	/// <summary>How the route reaches this frame from the one before - null when it simply carries on.</summary>
	public RouteJoin? JoinBefore => StartsAfterCut ? CutJoin ?? RouteJoins.Default : null;
}
