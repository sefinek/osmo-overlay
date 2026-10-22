namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The camera's own orientation quaternion, unit length, in the camera's axes - what a format reads, not interpreted
///     here (which way is up for it is the format's to say). q and -q are the same rotation, so compare with AngleDegreesTo.
/// </summary>
public readonly record struct TelemetryOrientation(float W, float X, float Y, float Z)
{
	private const double NormTolerance = 0.01;

	/// <summary>
	///     Null for anything that isn't a rotation: a missing or non-finite component, or a length further than 1% from 1
	///     (zero included) - guessing at those would invent an orientation.
	/// </summary>
	public static TelemetryOrientation? TryCreate(double w, double x, double y, double z)
	{
		if (!double.IsFinite(w) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return null;

		double norm = Math.Sqrt(w * w + x * x + y * y + z * z);
		if (Math.Abs(norm - 1) > NormTolerance) return null;

		return new TelemetryOrientation((float)(w / norm), (float)(x / norm), (float)(y / norm), (float)(z / norm));
	}

	/// <summary>The smallest rotation taking one orientation to the other, 0-180 degrees.</summary>
	public double AngleDegreesTo(TelemetryOrientation other)
	{
		double dot = Math.Abs((double)W * other.W + (double)X * other.X + (double)Y * other.Y + (double)Z * other.Z);
		return AngleMath.RadToDeg(2 * Math.Acos(Math.Min(1, dot)));
	}
}
