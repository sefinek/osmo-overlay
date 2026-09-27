namespace OsmoOverlay.Core.Telemetry;

public readonly record struct SunPosition(double AzimuthDegrees, double ElevationDegrees);

public static class SunCalculator
{
	public static SunPosition Calculate(DateTime utc, double latitude, double longitude)
	{
		double jd = utc.ToOADate() + 2415018.5;
		double n = jd - 2451545.0;

		double meanLongitude = AngleMath.NormalizeDegrees(280.460 + 0.9856474 * n);
		double meanAnomaly = AngleMath.NormalizeDegrees(357.528 + 0.9856003 * n);
		double gRad = AngleMath.DegToRad(meanAnomaly);

		double eclipticLongitude = meanLongitude + 1.915 * Math.Sin(gRad) + 0.020 * Math.Sin(2 * gRad);
		double lambdaRad = AngleMath.DegToRad(AngleMath.NormalizeDegrees(eclipticLongitude));

		double obliquity = 23.439 - 0.0000004 * n;
		double epsilonRad = AngleMath.DegToRad(obliquity);

		double rightAscensionRad = Math.Atan2(Math.Cos(epsilonRad) * Math.Sin(lambdaRad), Math.Cos(lambdaRad));
		double declinationRad = Math.Asin(Math.Sin(epsilonRad) * Math.Sin(lambdaRad));

		double gmst = AngleMath.NormalizeDegrees(280.46061837 + 360.98564736629 * n);
		double hourAngleDeg = AngleMath.NormalizeDegrees(gmst + longitude - AngleMath.RadToDeg(rightAscensionRad));
		double hRad = AngleMath.DegToRad(hourAngleDeg > 180 ? hourAngleDeg - 360 : hourAngleDeg);

		double latRad = AngleMath.DegToRad(latitude);

		double sinAltitude = Math.Sin(latRad) * Math.Sin(declinationRad)
		                     + Math.Cos(latRad) * Math.Cos(declinationRad) * Math.Cos(hRad);
		double altitudeRad = Math.Asin(Math.Clamp(sinAltitude, -1, 1));

		double azimuthDenominator = Math.Cos(latRad) * Math.Cos(altitudeRad);
		double azimuthRad;
		if (Math.Abs(azimuthDenominator) < 1e-9)
		{
			// Sun at the zenith, or observer at the pole: azimuth is undefined - 0 is as valid
			// as any other value and keeps the result finite instead of NaN.
			azimuthRad = 0;
		}
		else
		{
			double cosAzimuth = (Math.Sin(declinationRad) - Math.Sin(latRad) * sinAltitude) / azimuthDenominator;
			azimuthRad = Math.Acos(Math.Clamp(cosAzimuth, -1, 1));
			if (Math.Sin(hRad) > 0) azimuthRad = 2 * Math.PI - azimuthRad;
		}

		return new SunPosition(AngleMath.RadToDeg(azimuthRad), AngleMath.RadToDeg(altitudeRad));
	}
}
