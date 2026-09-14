namespace OsmoOverlay.Core.Telemetry;

public sealed record TelemetryFrame(
	int FrameNumber,
	double SampleTimeSeconds,
	double Latitude,
	double Longitude,
	double AltitudeMeters,
	DateTime? GpsTimestamp,
	double AccelX,
	double AccelY,
	double AccelZ,
	double? GpsSpeedMs = null,
	float? Iso = null,
	double? ShutterSeconds = null,
	int? ColorTemperatureKelvin = null)
{
	public double GForce => Math.Sqrt(AccelX * AccelX + AccelY * AccelY + AccelZ * AccelZ);
}
