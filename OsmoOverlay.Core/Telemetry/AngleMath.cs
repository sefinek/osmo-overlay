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
}
