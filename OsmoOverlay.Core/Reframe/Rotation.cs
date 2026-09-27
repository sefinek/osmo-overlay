using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Reframe;

/// <summary>
///     A direction in a camera's own space, as ffmpeg's v360 has it: x to the right, y down, z forward (a 360 camera's
///     front lens).
/// </summary>
public readonly record struct Direction(double X, double Y, double Z)
{
	public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

	public Direction Normalized()
	{
		var length = Length;
		return length > 0 ? new Direction(X / length, Y / length, Z / length) : new Direction(0, 1, 0);
	}
}

/// <summary>
///     A rotation of directions (a 3x3 matrix, row by row) - what turns a direction of the flat view into the direction
///     in the camera's lenses it is taken from.
/// </summary>
public readonly record struct Rotation(
	double M00,
	double M01,
	double M02,
	double M10,
	double M11,
	double M12,
	double M20,
	double M21,
	double M22)
{
	public static readonly Rotation Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

	/// <summary>
	///     A view's yaw, pitch and roll the way v360 composes them - its quaternion yaw(about +y) * pitch(about +x) *
	///     roll(about +z), i.e. Ry * Rx * Rz - so a view looks where it did through v360.
	/// </summary>
	public static Rotation FromView(ReframeView view)
	{
		return AboutY(AngleMath.DegToRad(view.Yaw)) * AboutX(AngleMath.DegToRad(view.Pitch)) * AboutZ(AngleMath.DegToRad(view.Roll));
	}

	/// <summary>
	///     The smallest rotation turning the world's down (0, 1, 0) into `down`, the direction gravity pulls in the
	///     camera's space - a level world's picture taken from the tilted camera. Upside down (`down` straight up) it
	///     turns about x.
	/// </summary>
	public static Rotation Leveling(Direction down)
	{
		Direction d = down.Normalized();
		// Axis = (0, 1, 0) x d, sine = its length, cosine = (0, 1, 0) . d.
		double ax = d.Z, ay = 0, az = -d.X;
		var sin = Math.Sqrt(ax * ax + az * az);
		var cos = d.Y;
		if (sin < 1e-9) return cos > 0 ? Identity : AboutX(Math.PI);

		ax /= sin;
		az /= sin;
		var v = 1 - cos;
		return new Rotation(
			cos + ax * ax * v, ax * ay * v - az * sin, ax * az * v + ay * sin,
			ay * ax * v + az * sin, cos + ay * ay * v, ay * az * v - ax * sin,
			az * ax * v - ay * sin, az * ay * v + ax * sin, cos + az * az * v);
	}

	public Direction Apply(Direction d)
	{
		return new Direction(M00 * d.X + M01 * d.Y + M02 * d.Z, M10 * d.X + M11 * d.Y + M12 * d.Z, M20 * d.X + M21 * d.Y + M22 * d.Z);
	}

	/// <summary>`b` first, then `a`.</summary>
	public static Rotation operator *(Rotation a, Rotation b)
	{
		return new Rotation(
			a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
			a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
			a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);
	}

	private static Rotation AboutX(double radians)
	{
		double c = Math.Cos(radians), s = Math.Sin(radians);
		return new Rotation(1, 0, 0, 0, c, -s, 0, s, c);
	}

	private static Rotation AboutY(double radians)
	{
		double c = Math.Cos(radians), s = Math.Sin(radians);
		return new Rotation(c, 0, s, 0, 1, 0, -s, 0, c);
	}

	private static Rotation AboutZ(double radians)
	{
		double c = Math.Cos(radians), s = Math.Sin(radians);
		return new Rotation(c, -s, 0, s, c, 0, 0, 0, 1);
	}
}
