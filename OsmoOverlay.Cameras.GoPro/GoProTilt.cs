using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.GoPro;

/// <summary>
///     The camera's tilt as an orientation (TelemetryFrame.Orientation): the shortest rotation taking straight down
///     (0, 1, 0 in x right, y down, z forward) onto the gravity vector the camera reports (GRAV) - tilt only, GRAV says
///     nothing of heading, and CORI's reference is arbitrary. What GoProFormat.OrientationGravity gives back is GRAV itself.
/// </summary>
internal static class GoProTilt
{
	public static TelemetryOrientation? Orientation(Direction gravity)
	{
		double length = Math.Sqrt(gravity.X * gravity.X + gravity.Y * gravity.Y + gravity.Z * gravity.Z);
		if (!double.IsFinite(length) || length < 0.5) return null;

		(double x, double y, double z) = (gravity.X / length, gravity.Y / length, gravity.Z / length);
		// Down onto up has no shortest rotation: any half turn about an axis across them will do.
		if (y < -1 + 1e-9) return TelemetryOrientation.TryCreate(0, 0, 0, 1);

		double norm = Math.Sqrt(2 + 2 * y);
		return TelemetryOrientation.TryCreate((1 + y) / norm, z / norm, 0, -x / norm);
	}

	/// <summary>Straight down (0, 1, 0) turned by the orientation - the gravity vector it was made from.</summary>
	public static Direction Gravity(TelemetryOrientation q)
	{
		return new Direction(2.0 * (q.X * q.Y - q.Z * q.W), 1.0 - 2.0 * (q.X * q.X + q.Z * q.Z), 2.0 * (q.Y * q.Z + q.X * q.W));
	}
}
