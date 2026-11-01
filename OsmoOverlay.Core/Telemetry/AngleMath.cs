namespace OsmoOverlay.Core.Telemetry;

public static class AngleMath
{
	public static double DegToRad(double deg)
	{
		return deg * Math.PI / 180.0;
	}

	public static double RadToDeg(double rad)
	{
		return rad * 180.0 / Math.PI;
	}

	public static double NormalizeDegrees(double deg)
	{
		deg %= 360;
		return deg < 0 ? deg + 360 : deg;
	}

	/// <summary>The angle `fraction` of the way from `from` to `to`, the shorter way round.</summary>
	public static double LerpDegrees(double from, double to, double fraction)
	{
		double delta = NormalizeDegrees(to - from);
		if (delta > 180) delta -= 360;
		return from + delta * fraction;
	}
}
