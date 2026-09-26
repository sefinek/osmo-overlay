namespace OsmoOverlay.Core.Telemetry;

public static class TelemetryMath
{
	private const double EarthRadiusMeters = 6371000.0;

	public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
	{
		var dLat = AngleMath.DegToRad(lat2 - lat1);
		var dLon = AngleMath.DegToRad(lon2 - lon1);
		var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
		        + Math.Cos(AngleMath.DegToRad(lat1)) * Math.Cos(AngleMath.DegToRad(lat2))
		                                             * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
		var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
		return EarthRadiusMeters * c;
	}

	public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
	{
		double phi1 = AngleMath.DegToRad(lat1), phi2 = AngleMath.DegToRad(lat2);
		var dLon = AngleMath.DegToRad(lon2 - lon1);
		var y = Math.Sin(dLon) * Math.Cos(phi2);
		var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
		return (AngleMath.RadToDeg(Math.Atan2(y, x)) + 360) % 360;
	}
}
